using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using WowDi.Contracts;
using WowDi.Dml.Semantics;
using WowDi.Solver.Contracts;
using WowDi.Udir;

namespace WowDi.Dml.Lowering;

/// <summary>Conservative exact affine lowering. Does not load a backend or certify an incumbent.</summary>
public static class ExactLowerer
{
    public static LoweringResult Lower(NormalizedModel model,SolverCapabilities manifest,NumericEncoding encoding,
        ReadOnlyMemory<byte>? facts=null,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(model);ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            manifest.Validate();
            if(!Enum.IsDefined(encoding))return new(null,new("lower.encoding","Unknown numeric encoding.",null));
            return new(new Worker(model,manifest,encoding,facts,cancellationToken).Run(),null);
        }
        catch(Failure e){return new(null,new(e.Code,e.Message,e.Span));}
        catch(ContractException e){return new(null,new(e.Code,"Invalid capability manifest.",null));}
        catch(OverflowException){return new(null,new("lower.overflow","Exact arithmetic exceeds the admitted profile.",null));}
        catch(JsonException){return new(null,new("lower.factsFormat","Invalid frozen fact snapshot.",null));}
    }
    private sealed class Failure(string code,string message,SourceSpan? span=null):Exception(message)
    {internal string Code{get;}=code;internal SourceSpan? Span{get;}=span;}
    private sealed record Affine(Rational Constant,ImmutableSortedDictionary<int,Rational> Terms)
    {
        internal static Affine Value(Rational n)=>new(n,ImmutableSortedDictionary<int,Rational>.Empty);
        internal static Affine Variable(int index)=>new(Rational.Origin,ImmutableSortedDictionary<int,Rational>.Empty.Add(index,Rational.One));
    }
    private sealed class Worker(NormalizedModel normalized,SolverCapabilities manifest,NumericEncoding encoding,
        ReadOnlyMemory<byte>? suppliedFacts,CancellationToken ct)
    {
        private const int MaximumCoefficientWork=1_000_000;
        private readonly Rational safeLimit=new(encoding==NumericEncoding.Int64?long.MaxValue/2:9007199254740991L,1);
        private readonly Dictionary<string,NativeColumn> columns=new(StringComparer.Ordinal);
        private readonly Dictionary<string,Rational> fixedValues=new(StringComparer.Ordinal);
        private readonly Dictionary<string,Affine> expressions=new(StringComparer.Ordinal);
        private readonly Dictionary<string,SourceSpan> spans=normalized.Origins.GroupBy(o=>o.NormalizedId,StringComparer.Ordinal)
            .ToDictionary(g=>g.Key,g=>g.First().Span,StringComparer.Ordinal);
        private int work;
        private string? factsDigest;
        private ImmutableArray<NativeColumn> ordered;

        internal LoweredModel Run()
        {
            var model=normalized.Original.Model;
            if(manifest.MissingFeatures(normalized.Original).Length>0)throw Bad("lower.capability","Required capability is absent.");
            if(model.Requires.Any(f=>f is not ("integer.linear" or "rational.linear")))throw Bad("lower.profile","Required feature has no admitted lowering implementation.");
            if(!model.Scenarios.IsEmpty)throw Bad("lower.scenario","Scenario lowering is not qualified.");
            if(normalized.Constraints.Any(r=>!r.Hard))throw Bad("lower.soft","Soft-constraint lowering is not qualified.");
            if(normalized.Objectives.Length>1||normalized.Objectives.Any(o=>o.Weight!="1"||o.Tolerance!="0"))
                throw Bad("lower.objective","Objective policy is not supported by this profile.");
            var facts=ReadFacts(model);
            var types=model.Types.ToDictionary(t=>t.Id,StringComparer.Ordinal);
            var variableBuilder=ImmutableArray.CreateBuilder<NativeColumn>();
            foreach(var symbol in model.Symbols.OrderBy(s=>s.Id,StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var type=types[symbol.TypeId];
                if(symbol.Role!=SymbolRole.Variable)
                {
                    if(!facts.Remove(symbol.Id,out var raw))
                    {
                        if(symbol.Lower is null||symbol.Lower!=symbol.Upper)throw Bad("lower.missingFact","Non-variable fact is not frozen.",symbol.Span);
                        raw=symbol.Lower;
                    }
                    var value=ParseValue(raw,type.Kind,symbol.Span);
                    if((symbol.Lower is not null&&value.CompareTo(Parse(symbol.Lower,symbol.Span))<0)||
                        (symbol.Upper is not null&&value.CompareTo(Parse(symbol.Upper,symbol.Span))>0))
                        throw Bad("lower.factDomain","Frozen fact is outside the admitted domain.",symbol.Span);
                    fixedValues.Add(symbol.Id,value);continue;
                }
                if(!manifest.Domains.Contains(type.Kind))throw Bad("lower.domain","Variable domain is not supported.",symbol.Span);
                bool integral=type.Kind is ScalarKind.Integer or ScalarKind.Instant or ScalarKind.Boolean;
                var feature=integral?"integer.linear":"rational.linear";
                if(!manifest.Features.Contains(feature,StringComparer.Ordinal))
                    throw Bad("lower.capability","Required affine-domain feature is absent.",symbol.Span);
                if(encoding==NumericEncoding.Int64&&!integral)throw Bad("lower.rounding","Continuous variable cannot use integer encoding.",symbol.Span);
                var lo=type.Kind==ScalarKind.Boolean?Rational.Origin:Parse(symbol.Lower??throw Bad("lower.unbounded","Finite variable bounds are required.",symbol.Span),symbol.Span);
                var hi=type.Kind==ScalarKind.Boolean?Rational.One:Parse(symbol.Upper??throw Bad("lower.unbounded","Finite variable bounds are required.",symbol.Span),symbol.Span);
                CheckNativeBound(lo,symbol.Span);CheckNativeBound(hi,symbol.Span);
                if(type.Kind is ScalarKind.Instant or ScalarKind.Duration)
                {
                    if(type.Unit!=model.TimeBasis.TickUnit||manifest.MaxHorizonTicks is not long horizon||
                        lo.CompareTo(Rational.Origin)<0||hi.CompareTo(new Rational(horizon,1))>0)
                        throw Bad("lower.horizon","Temporal domain exceeds the explicit tick horizon.",symbol.Span);
                }
                var col=new NativeColumn(variableBuilder.Count,symbol.Id,type.Kind,lo.ToString(),hi.ToString(),symbol.Span);
                columns.Add(symbol.Id,col);variableBuilder.Add(col);
            }
            if(facts.Count!=0)throw Bad("lower.extraFact","Unexpected frozen fact.");
            ordered=variableBuilder.ToImmutable();
            var rows=ImmutableArray.CreateBuilder<NativeRow>();
            foreach(var row in normalized.Constraints)
            {
                ct.ThrowIfCancellationRequested();
                var predicate=normalized.Expressions[row.PredicateId];
                Affine affine;Operation relation;
                if(predicate.Operation is Operation.Equal or Operation.LessOrEqual or Operation.GreaterOrEqual)
                {
                    affine=Add(Get(predicate.Arguments[0]),Get(predicate.Arguments[1]),true);
                    relation=predicate.Operation;
                }
                else
                {
                    affine=Add(Get(predicate.Id),Affine.Value(Rational.One),true);
                    relation=Operation.Equal;
                }
                var (terms,rhs,scale)=Scale(affine.Terms,-affine.Constant,row.Span);
                rows.Add(new(rows.Count,row.Id,relation,terms,rhs,scale,row.Span));
            }
            NativeObjective? objective=null;
            if(normalized.Objectives is [var goal])
            {
                var value=Get(goal.ExpressionId);
                var (terms,offset,scale)=Scale(value.Terms,value.Constant,goal.Span);
                objective=new(goal.Id,goal.Direction,terms,offset,scale,goal.Span);
            }
            return new(normalized,Hash(normalized.Original.Write()),factsDigest,manifest,encoding,ordered,rows.ToImmutable(),objective);
        }

        private Dictionary<string,string> ReadFacts(Model model)
        {
            var result=new Dictionary<string,string>(StringComparer.Ordinal);
            if(suppliedFacts is not {} memory)return result;
            if(memory.Length is <1 or >1048576)throw Bad("lower.factsSize","Invalid frozen fact size.");
            var bytes=memory.ToArray();factsDigest=Hash(bytes);
            if(!model.InputDigests.Contains(factsDigest,StringComparer.Ordinal))throw Bad("lower.factsDigest","Fact snapshot is not a frozen input.");
            using var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=4,AllowDuplicateProperties=false});
            if(doc.RootElement.ValueKind!=JsonValueKind.Object)throw Bad("lower.factsFormat","Expected flat fact object.");
            foreach(var item in doc.RootElement.EnumerateObject())
            {
                if(item.Value.ValueKind!=JsonValueKind.String||!result.TryAdd(item.Name,item.Value.GetString()!))
                    throw Bad("lower.factsFormat","Expected unique string-valued facts.");
            }
            return result;
        }
        private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
        private static Rational Parse(string raw,SourceSpan? span)
        {
            if(raw.Length>514||raw.Count(c=>c=='/')>1)throw Bad("lower.number","Invalid exact number.",span);
            try
            {
                var n=Rational.Parse(raw);
                if(n.ToString()!=raw)throw Bad("lower.number","Noncanonical exact number.",span);
                return n;
            }
            catch(Exception e) when(e is FormatException or DivideByZeroException or OverflowException)
            {throw Bad("lower.number","Invalid exact number.",span);}
        }
        private static Rational ParseValue(string raw,ScalarKind kind,SourceSpan? span)
        {
            if(kind==ScalarKind.Boolean)return raw switch{"true"=>Rational.One,"false"=>Rational.Origin,_=>throw Bad("lower.number","Invalid Boolean fact.",span)};
            var number=Parse(raw,span);
            if(kind is ScalarKind.Integer or ScalarKind.Instant&&!number.Integral)throw Bad("lower.rounding","Nonintegral fact.",span);
            return number;
        }
        private static Rational Abs(Rational value)=>value.N.Sign<0?-value:value;
        private void CheckNativeBound(Rational n,SourceSpan span)
        {
            if(Abs(n).CompareTo(safeLimit)>0)throw Bad("lower.overflow","Native bound exceeds conservative numeric range.",span);
            if(encoding==NumericEncoding.Int64?!n.Integral:!Binary64Exact(n))
                throw Bad("lower.rounding","Native bound requires rounding.",span);
        }
        private static bool Binary64Exact(Rational n)
        {
            if(n.Zero)return true;
            if((n.D&(n.D-BigInteger.One))!=0)return false;
            var significant=BigInteger.Abs(n.N);long exponent=1-n.D.GetBitLength();
            while(significant.IsEven){significant>>=1;exponent++;}
            long bits=significant.GetBitLength();
            return bits<=53&&exponent>=-1074&&exponent+bits-1<=1023;
        }

        private Affine Get(string root)
        {
            var stack=new Stack<(string Id,bool Ready)>();stack.Push((root,false));
            while(stack.TryPop(out var item))
            {
                ct.ThrowIfCancellationRequested();
                if(expressions.ContainsKey(item.Id))continue;
                var node=normalized.Expressions[item.Id];
                if(!item.Ready)
                {
                    stack.Push((item.Id,true));
                    foreach(var arg in node.Arguments.Reverse())if(!expressions.ContainsKey(arg))stack.Push((arg,false));
                    continue;
                }
                var span=spans.GetValueOrDefault(node.Id);
                Affine? a=node.Arguments.Length>0?expressions[node.Arguments[0]]:null;
                Affine? b=node.Arguments.Length>1?expressions[node.Arguments[1]]:null;
                var value=node.Operation switch
                {
                    Operation.Literal=>Affine.Value(ParseValue(node.Literal!,node.Type.Kind,span)),
                    Operation.Symbol=>columns.TryGetValue(node.SymbolId!,out var col)?Affine.Variable(col.Index):Affine.Value(fixedValues[node.SymbolId!]),
                    Operation.Negate=>Multiply(a!,-Rational.One),
                    Operation.Add=>Add(a!,b!,false),
                    Operation.Subtract=>Add(a!,b!,true),
                    Operation.Multiply when a!.Terms.IsEmpty=>Multiply(b!,a.Constant),
                    Operation.Multiply when b!.Terms.IsEmpty=>Multiply(a!,b.Constant),
                    Operation.Divide when b!.Terms.IsEmpty&&!b.Constant.Zero=>Multiply(a!,Rational.One/b.Constant),
                    _=>throw Bad("lower.nonlinear","Expression is outside the admitted affine profile.",span)
                };
                Charge(value.Terms.Count);
                expressions.Add(item.Id,value);
            }
            return expressions[root];
        }
        private void Charge(int amount)
        {
            work=checked(work+amount+1);
            if(work>MaximumCoefficientWork)throw Bad("lower.limit","Affine coefficient work limit exceeded.");
        }
        private Affine Add(Affine a,Affine b,bool subtract)
        {
            Charge(a.Terms.Count+b.Terms.Count);
            var builder=a.Terms.ToBuilder();
            foreach(var (index,coefficient) in b.Terms)
            {
                var n=(builder.TryGetValue(index,out var prior)?prior:Rational.Origin)+(subtract?-coefficient:coefficient);
                if(n.Zero)builder.Remove(index);else builder[index]=n;
            }
            return new(a.Constant+(subtract?-b.Constant:b.Constant),builder.ToImmutable());
        }
        private Affine Multiply(Affine a,Rational scalar)
        {
            Charge(a.Terms.Count);
            return scalar.Zero?Affine.Value(Rational.Origin):new(a.Constant*scalar,
                a.Terms.ToImmutableSortedDictionary(p=>p.Key,p=>p.Value*scalar));
        }
        private (ImmutableArray<NativeCoefficient>,string,string) Scale(ImmutableSortedDictionary<int,Rational> terms,Rational offset,SourceSpan span)
        {
            Charge(terms.Count);
            BigInteger denominator=offset.D;
            foreach(var n in terms.Values)
            {
                denominator=denominator/BigInteger.GreatestCommonDivisor(denominator,n.D)*n.D;
                if(denominator.GetBitLength()>851)throw Bad("lower.overflow","Exact scaling exceeds the profile.",span);
            }
            var scale=new Rational(denominator,1);
            var nativeOffset=offset*scale;
            CheckCoefficient(nativeOffset,span);
            var magnitude=Abs(nativeOffset);
            var native=ImmutableArray.CreateBuilder<NativeCoefficient>();
            foreach(var (index,n) in terms)
            {
                var coefficient=n*scale;CheckCoefficient(coefficient,span);
                var column=ordered[index];
                var lo=Abs(Parse(column.Lower,span));var hi=Abs(Parse(column.Upper,span));
                magnitude+=Abs(coefficient)*(lo.CompareTo(hi)>0?lo:hi);
                if(magnitude.CompareTo(safeLimit)>0)throw Bad("lower.overflow","Affine activity exceeds conservative numeric range.",span);
                native.Add(new(index,coefficient.ToString()));
            }
            return(native.ToImmutable(),nativeOffset.ToString(),scale.ToString());
        }
        private void CheckCoefficient(Rational value,SourceSpan span)
        {
            if(!value.Integral)throw Bad("lower.rounding","Coefficient was not scaled exactly.",span);
            if(Abs(value).CompareTo(safeLimit)>0||
                manifest.MaxCoefficientMagnitude is string limit&&Abs(value).CompareTo(Parse(limit,span))>0)
                throw Bad("lower.overflow","Coefficient exceeds native profile.",span);
        }
        private static Failure Bad(string code,string message,SourceSpan? span=null)=>new(code,message,span);
    }
}
