using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using WowDi.Contracts;
using WowDi.Udir;

namespace WowDi.Dml.Semantics;

/// <summary>Conservative exact transformation. Never publishes a different document as the same revision.</summary>
public static class ExactNormalizer
{
    private static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    public static NormalizationResult Normalize(UdirDocument document,MeasurementSemantics measurements,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(document);ArgumentNullException.ThrowIfNull(measurements);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _=document.Write(); // enforce existing wire/resource boundary even for an in-memory document
            document.ValidateMeasurements(measurements);
            return new(new Worker(document,cancellationToken).Run(),null);
        }
        catch(ContractException e){return new(null,new(e.Code,"Original model admission failed.",null));}
        catch(Failure e){return new(null,new(e.Code,e.Message,e.Span));}
    }

    private sealed class Failure(string code,string message,SourceSpan span):Exception(message)
    {internal string Code{get;}=code;internal SourceSpan Span{get;}=span;}

    private sealed record Interval(Rational Lower,Rational Upper)
    {
        internal bool ExcludesZero=>Lower.CompareTo(Rational.Origin)>0||Upper.CompareTo(Rational.Origin)<0;
        internal bool Singleton=>Lower==Upper;
        internal static Interval Point(Rational x)=>new(x,x);
    }
    private sealed record Fact(NormalizedExpression Node,Interval? Range,bool Integral,string? Literal);

    private sealed class Worker(UdirDocument document,CancellationToken cancellation)
    {
        private readonly Dictionary<string,SemanticType> types=document.Model.Types.ToDictionary(t=>t.Id,SemanticType.From,StringComparer.Ordinal);
        private readonly Dictionary<string,Symbol> symbols=document.Model.Symbols.ToDictionary(s=>s.Id,StringComparer.Ordinal);
        private readonly Dictionary<string,Fact> facts=new(StringComparer.Ordinal);
        private readonly Dictionary<string,NormalizedExpression> nodes=new(StringComparer.Ordinal);
        private readonly Dictionary<string,byte[]> nodeKeys=new(StringComparer.Ordinal);
        private readonly ImmutableArray<ExpressionOrigin>.Builder origins=ImmutableArray.CreateBuilder<ExpressionOrigin>();

        internal NormalizedModel Run()
        {
            var model=document.Model;
            var originals=model.Expressions.ToDictionary(e=>e.Id,StringComparer.Ordinal);
            var pending=originals.Values.ToDictionary(e=>e.Id,e=>e.Arguments.Length,StringComparer.Ordinal);
            var reverse=originals.Keys.ToDictionary(k=>k,_=>new List<string>(),StringComparer.Ordinal);
            foreach(var e in originals.Values)foreach(var child in e.Arguments)reverse[child].Add(e.Id);
            var ready=new PriorityQueue<string,string>(StringComparer.Ordinal);
            foreach(var (id,count) in pending)if(count==0)ready.Enqueue(id,id);
            while(ready.TryDequeue(out var id,out _))
            {
                cancellation.ThrowIfCancellationRequested();
                var e=originals[id];
                try {facts.Add(id,Normalize(e));}
                catch(OverflowException){throw Bad("DML-NUM-001","Exact arithmetic exceeds the supported numeric profile.",e);}
                catch(DivideByZeroException){throw Bad("DML-DIV-001","Division by zero.",e);}
                foreach(var parent in reverse[id])if(--pending[parent]==0)ready.Enqueue(parent,parent);
            }
            if(facts.Count!=originals.Count)throw new InvalidOperationException("Validated UDIR contained an expression cycle.");

            var constraints=model.Constraints.OrderBy(c=>c.Id,StringComparer.Ordinal)
                .Select(c=>new NormalizedConstraint(c.Id,facts[c.PredicateId].Node.Id,c.Hard,c.Penalty,c.Span)).ToImmutableArray();
            var objectives=model.Objectives.OrderBy(o=>o.Priority)
                .Select(o=>new NormalizedObjective(o.Id,facts[o.ExpressionId].Node.Id,o.Direction,o.Priority,o.Weight,o.Tolerance,o.Span)).ToImmutableArray();

            // Include only semantically reachable nodes in the projection, while retaining all original lineage.
            var reachable=new HashSet<string>(StringComparer.Ordinal);
            var stack=new Stack<string>(constraints.Select(c=>c.PredicateId).Concat(objectives.Select(o=>o.ExpressionId)));
            while(stack.TryPop(out var id))if(reachable.Add(id))foreach(var arg in nodes[id].Arguments)stack.Push(arg);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new
            {
                profile="wowdi.normalized-math/1",
                semantics=model.SemanticsVersion,
                timeBasis=model.TimeBasis,
                symbols=model.Symbols.OrderBy(s=>s.Id,StringComparer.Ordinal).Select(s=>new {s.Id,s.Role,type=types[s.TypeId],s.Lower,s.Upper}),
                expressions=reachable.Order(StringComparer.Ordinal).Select(id=>nodes[id]),
                constraints=constraints.Select(c=>new{c.Id,c.PredicateId,c.Hard,c.Penalty}),
                objectives=objectives.Select(o=>new{o.Id,o.ExpressionId,o.Direction,o.Priority,o.Weight,o.Tolerance}),
                scenarios=model.Scenarios.OrderBy(s=>s.Id,StringComparer.Ordinal).Select(s=>new
                {s.Id,s.Stage,s.InformationSet,s.ParentId,s.Probability,overrides=s.Overrides.OrderBy(x=>x.SymbolId,StringComparer.Ordinal)}),
                requires=model.Requires.Order(StringComparer.Ordinal)
            },Json);
            return new(document,nodes.ToImmutableDictionary(StringComparer.Ordinal),
                [..origins.OrderBy(o=>o.OriginalId,StringComparer.Ordinal)],constraints,objectives,
                ImmutableArray.CreateRange(bytes),Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }

        private Fact Normalize(Expression e)
        {
            var type=types[e.TypeId];
            bool mustBeInteger=type.Kind is ScalarKind.Integer or ScalarKind.Instant;
            if(e.Op==Operation.Literal)
            {
                var n=type.Kind==ScalarKind.Boolean?(Rational?)null:Rational.Parse(e.Literal!);
                return Record(e,type,e.Op,[],e.Literal,null,n.HasValue?Interval.Point(n.Value):null,n?.Integral??false,"identity");
            }
            if(e.Op==Operation.Symbol)
            {
                var s=symbols[e.SymbolId!];
                var symbolRange=s.Lower is not null&&s.Upper is not null?new Interval(Rational.Parse(s.Lower),Rational.Parse(s.Upper)):null;
                return Record(e,type,e.Op,[],null,s.Id,symbolRange,mustBeInteger||symbolRange is {Singleton:true}&&symbolRange.Lower.Integral,"identity");
            }
            var a=facts[e.Arguments[0]];
            var b=e.Arguments.Length==2?facts[e.Arguments[1]]:null;
            if(e.Op==Operation.Divide&&(b!.Range is null||!b.Range.ExcludesZero))
                throw Bad("DML-DIV-001","Divisor cannot be proven nonzero over its domain.",e);
            bool comparison=e.Op is Operation.Equal or Operation.LessOrEqual or Operation.GreaterOrEqual;
            if(a.Literal is not null&&(b is null||b.Literal is not null))
            {
                string literal;
                Rational? numeric=null;
                if(a.Node.Type.Kind==ScalarKind.Boolean)
                    literal=a.Literal==b!.Literal?"true":"false";
                else
                {
                    var x=Rational.Parse(a.Literal);
                    var y=b is null?Rational.Origin:Rational.Parse(b.Literal!);
                    if(comparison)
                        literal=(e.Op switch {Operation.Equal=>x==y,Operation.LessOrEqual=>x.CompareTo(y)<=0,_=>x.CompareTo(y)>=0})?"true":"false";
                    else
                    {
                        numeric=e.Op switch {Operation.Negate=>-x,Operation.Add=>x+y,Operation.Subtract=>x-y,
                            Operation.Multiply=>x*y,Operation.Divide=>x/y,_=>throw new InvalidOperationException()};
                        if(mustBeInteger&&!numeric.Value.Integral)throw Bad("DML-NUM-002","Exact result is not integral.",e);
                        literal=numeric.Value.ToString();
                    }
                }
                return Record(e,type,Operation.Literal,[],literal,null,numeric.HasValue?Interval.Point(numeric.Value):null,numeric?.Integral??false,"constant-fold");
            }

            Interval? range=comparison?null:Range(e.Op,a.Range,b?.Range);
            bool integral=!comparison&&(e.Op switch
            {
                Operation.Negate=>a.Integral,
                Operation.Add or Operation.Subtract or Operation.Multiply=>a.Integral&&b!.Integral,
                Operation.Divide=>a.Integral&&b!.Range is {Singleton:true}&&
                    (b.Range.Lower==Rational.One||b.Range.Lower==-Rational.One),
                _=>false
            }||range is {Singleton:true}&&range.Lower.Integral);
            if(mustBeInteger&&!integral)throw Bad("DML-NUM-002","Integral result cannot be established without rounding.",e);
            var args=b is null?ImmutableArray.Create(a.Node.Id):ImmutableArray.Create(a.Node.Id,b.Node.Id);
            string rewrite="identity";
            if((e.Op is Operation.Multiply or Operation.Equal || e.Op==Operation.Add&&type.Kind!=ScalarKind.Instant)&&args.Length==2&&StringComparer.Ordinal.Compare(args[0],args[1])>0)
            {args=[args[1],args[0]];rewrite="commute";}
            return Record(e,type,e.Op,args,null,null,range,integral,rewrite);
        }

        private static Interval? Range(Operation op,Interval? a,Interval? b)
        {
            if(a is null)return null;
            if(op==Operation.Negate)return new(-a.Upper,-a.Lower);
            if(b is null)return null;
            if(op==Operation.Add)return new(a.Lower+b.Lower,a.Upper+b.Upper);
            if(op==Operation.Subtract)return new(a.Lower-b.Upper,a.Upper-b.Lower);
            if(op is Operation.Multiply or Operation.Divide)
            {
                var values=new[]{a.Lower,a.Upper}.SelectMany(x=>new[]{b.Lower,b.Upper}.Select(y=>op==Operation.Multiply?x*y:x/y)).ToArray();
                return new(values.Min(),values.Max());
            }
            return null;
        }

        private Fact Record(Expression original,SemanticType type,Operation op,ImmutableArray<string> args,string? literal,string? symbol,
            Interval? range,bool integral,string rewrite)
        {
            var key=JsonSerializer.SerializeToUtf8Bytes(new {type,op,args,literal,symbol},Json);
            var id=Convert.ToHexStringLower(SHA256.HashData(key));
            if(nodeKeys.TryGetValue(id,out var prior))
            {
                if(!prior.AsSpan().SequenceEqual(key))throw Bad("DML-NUM-003","Canonical node identity collision.",original);
            }
            else
            {
                nodeKeys.Add(id,key);
                nodes.Add(id,new(id,op,type,args,literal,symbol));
            }
            origins.Add(new(original.Id,original.Span,id,rewrite));
            return new(nodes[id],range,integral,literal);
        }
        private static Failure Bad(string code,string message,Expression e)=>new(code,message,e.Span);
    }
}
