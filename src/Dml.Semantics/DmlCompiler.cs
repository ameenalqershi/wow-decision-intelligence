using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using WowDi.Contracts;
using WowDi.Dml.Binding;
using WowDi.Dml.Syntax;
using WowDi.Udir;

namespace WowDi.Dml.Semantics;

/// <summary>Host-owned type aliases. These select representation, not implicit conversions.</summary>
public sealed record DmlTypeAlias(string Unit, ScalarKind Kind, string? Currency = null);
public sealed record DmlCompileOptions(string TenantId,string ModelId,string RevisionId,TimeBasis TimeBasis,
    MeasurementSemantics Measurements,ImmutableArray<string> FrozenInputDigests,
    ImmutableArray<DmlTypeAlias> RationalAliases,ImmutableArray<string> RequiredCapabilities);
public sealed record DmlCompilation(UdirDocument Original,NormalizedModel Normalized,BoundDocument Bound,
    ImmutableDictionary<string,ImmutableArray<byte>> GeneratedInputs,string FactsDigest);
public sealed record DmlCompilationResult(DmlCompilation? Compilation,SemanticDiagnostic? Diagnostic)
{
    public bool Accepted=>Compilation is not null&&Diagnostic is null;
}

/// <summary>Semantic admission of the supported mathematical DML 0.1 profile. No solver, connector or host evaluation.</summary>
public static class DmlCompiler
{
    public static DmlCompilationResult Compile(BoundDocument bound,DmlCompileOptions options,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(bound);ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Measurements);
        cancellationToken.ThrowIfCancellationRequested();
        try {return new(new Compiler(bound,options,cancellationToken).Run(),null);}
        catch(Failure e){return new(null,new(e.Code,e.Message,e.Span,e.RelatedSpan));}
        catch(ContractException e){return new(null,new(e.Code,"Model admission failed.",bound.Syntax.Span));}
        catch(OverflowException){return new(null,new("DML-NUM-001","Exact arithmetic exceeds the supported profile.",bound.Syntax.Span));}
    }

    private sealed class Failure(string code,string message,SourceSpan span,SourceSpan? relatedSpan=null):Exception(message)
    {internal string Code{get;}=code;internal SourceSpan Span{get;}=span;internal SourceSpan? RelatedSpan{get;}=relatedSpan;}
    private sealed record Term(string Id,SemanticType Type,Rational? Number,bool? Boolean);
    private sealed class Compiler(BoundDocument bound,DmlCompileOptions options,CancellationToken cancellation)
    {
        private readonly Dictionary<SemanticType,ScalarType> types=[];
        private readonly Dictionary<string,SemanticType> symbolTypes=new(StringComparer.Ordinal);
        private readonly Dictionary<string,Symbol> symbols=new(StringComparer.Ordinal);
        private readonly Dictionary<string,Term> values=new(StringComparer.Ordinal);
        private readonly List<Expression> expressions=[];
        private readonly List<Constraint> rows=[];
        private readonly List<Objective> goals=[];
        private readonly List<Scenario> scenarios=[];
        private readonly HashSet<string> overridden=bound.Syntax.Declarations.OfType<ScenarioDeclaration>()
            .SelectMany(s=>s.Overrides).Select(o=>o.Name.Text).ToHashSet(StringComparer.Ordinal);
        private readonly Dictionary<string,DmlTypeAlias> aliases=new(StringComparer.Ordinal);
        private readonly HashSet<string> capabilities=new(StringComparer.Ordinal);
        private static readonly SemanticType Boolean=new(ScalarKind.Boolean,null,null);


        internal DmlCompilation Run()
        {
            if(options.FrozenInputDigests.IsDefaultOrEmpty||options.RequiredCapabilities.IsDefault||options.RationalAliases.IsDefault)
                throw Bad("DML-SEM-001","Explicit frozen host inputs and profile are required.",bound.Syntax.Span);
            foreach(var alias in options.RationalAliases)
            {
                if(alias.Kind is not(ScalarKind.Rational or ScalarKind.Quantity or ScalarKind.Rate or ScalarKind.Duration or ScalarKind.Money)||
                    (alias.Kind==ScalarKind.Money?alias.Currency!=alias.Unit:alias.Currency is not null)||
                    !aliases.TryAdd(alias.Unit,alias))
                    throw Bad("DML-SEM-001","Invalid host type alias.",bound.Syntax.Span);
            }
            foreach(var required in options.RequiredCapabilities)
                if(!capabilities.Add(required))throw Bad("DML-SEM-001","Duplicate required capability.",bound.Syntax.Span);
            foreach(var declaration in bound.Syntax.Declarations)
            {
                cancellation.ThrowIfCancellationRequested();
                if(declaration is WindowDeclaration window)
                {
                    ValidateWindow(window);
                    throw Bad("DML-CAP-001","UDIR v1 has no operational window representation.",window.Span);
                }
                if(declaration is SourceDeclaration or TriggerDeclaration or DependencyDeclaration)
                    throw Bad("DML-CAP-001","Operational declarations require an approved source/trigger/dependency admission profile.",declaration.Span);
            }
            foreach(var declaration in bound.ValueOrder)
            {
                cancellation.ThrowIfCancellationRequested();
                switch(declaration.Syntax)
                {
                    case ValueDeclaration v:
                    {
                        var type=Declared(v.Type);
                        var value=CompileTerm(v.Value,type,constant:true);
                        RequireSame(type,value.Type,v.Value.Span,v.Type.Span);
                        RequireValue(value,v.Value.Span);
                        values.Add(v.Name.Text,value);
                        symbolTypes.Add(v.Name.Text,type);
                        var domain=value.Number?.ToString();
                        symbols.Add(v.Name.Text,new(v.Name.Text,v.Role.Kind==TokenKind.Param?SymbolRole.Parameter:SymbolRole.Resource,
                            TypeId(type),v.Span,domain,domain));
                        break;
                    }
                    case DecisionDeclaration d:
                    {
                        var type=Declared(d.Type);
                        if(type==Boolean&&(d.Lower is not null||d.Upper is not null))
                            throw Bad("DML-DOMAIN-001","Boolean domains are implicit.",d.Span);
                        string? lo=null,hi=null;
                        if(d.Lower is not null&&d.Upper is not null)
                        {
                            var a=CompileTerm(d.Lower,type,true);var b=CompileTerm(d.Upper,type,true);
                            RequireSame(type,a.Type,d.Lower.Span,d.Type.Span);RequireSame(type,b.Type,d.Upper.Span,d.Type.Span);
                            RequireValue(a,d.Lower.Span);RequireValue(b,d.Upper.Span);
                            if(a.Number is null||b.Number is null||a.Number.Value.CompareTo(b.Number.Value)>0)
                                throw Bad("DML-DOMAIN-001","Invalid decision interval.",d.Span);
                            lo=a.Number.Value.ToString();hi=b.Number.Value.ToString();
                        }
                        symbolTypes.Add(d.Name.Text,type);
                        symbols.Add(d.Name.Text,new(d.Name.Text,SymbolRole.Variable,TypeId(type),d.Span,lo,hi));
                        break;
                    }
                }
            }
            var priorities=new HashSet<int>();
            foreach(var declaration in bound.Syntax.Declarations)
            {
                cancellation.ThrowIfCancellationRequested();
                switch(declaration)
                {
                    case ConstraintDeclaration c:
                    {
                        // Infer both sides without coercing symbols. Contextual numeric literals are allowed.
                        var (a,b)=Comparable(c.Left,c.Right);
                        var op=c.Relation.Kind switch {TokenKind.Equal=>Operation.Equal,TokenKind.LessEqual=>Operation.LessOrEqual,_=>Operation.GreaterOrEqual};
                        if(a.Type==Boolean&&op!=Operation.Equal)throw Bad("DML-TYPE-001","Boolean ordering is not supported.",c.Relation.Span);
                        string? penalty=null;
                        if(c.Penalty is not null)
                        {
                            var p=Rational.Decimal(c.Penalty.Text);
                            if(p.CompareTo(Rational.Origin)<=0)throw Bad("DML-DOMAIN-001","Soft penalty must be positive.",c.Penalty.Span);
                            penalty=p.ToString();
                        }
                        var predicate=Node(Boolean,op,[a.Id,b.Id],c.Span);
                        rows.Add(new(c.Name.Text,predicate.Id,penalty is null,c.Span,penalty));
                        break;
                    }
                    case ObjectiveDeclaration o:
                    {
                        if(!int.TryParse(o.Priority.Text,NumberStyles.None,CultureInfo.InvariantCulture,out var priority)||priority<1||!priorities.Add(priority))
                            throw Bad("DML-OBJ-002","Objective priority must be positive and unique.",o.Priority.Span);
                        var value=CompileTerm(o.Value);
                        if(value.Type==Boolean)throw Bad("DML-TYPE-001","Objective must be numeric.",o.Value.Span);
                        goals.Add(new(o.Name.Text,value.Id,o.Direction.Kind==TokenKind.Minimize?Direction.Minimize:Direction.Maximize,
                            priority,"1","0",o.Span));
                        break;
                    }
                }
            }
            CompileScenarios();
            var facts=values.OrderBy(p=>p.Key,StringComparer.Ordinal).ToDictionary(p=>p.Key,
                p=>p.Value.Boolean is bool b?(b?"true":"false"):p.Value.Number!.Value.ToString(),StringComparer.Ordinal);
            var factBytes=JsonSerializer.SerializeToUtf8Bytes(facts);
            var factDigest=Convert.ToHexStringLower(SHA256.HashData(factBytes));
            var inputs=ImmutableDictionary.CreateBuilder<string,ImmutableArray<byte>>(StringComparer.Ordinal);
            inputs[bound.Source.Sha256]=bound.SourceBytes;
            inputs[factDigest]=ImmutableArray.CreateRange(factBytes);
            var model=new Model(options.TenantId,options.ModelId,options.RevisionId,"udir/1",
                [..options.FrozenInputDigests.Concat(inputs.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                [bound.Source],options.TimeBasis,[..types.Values.OrderBy(x=>x.Id,StringComparer.Ordinal)],
                [..symbols.Values.OrderBy(s=>s.Id,StringComparer.Ordinal)],[..expressions],[..rows],[..goals],[..scenarios],
                [..capabilities.Order(StringComparer.Ordinal)]);
            var doc=UdirDocument.Create(model);
            var normalized=ExactNormalizer.Normalize(doc,options.Measurements,cancellation);
            if(!normalized.Accepted)throw Bad(normalized.Diagnostic!.Code,normalized.Diagnostic.Message,normalized.Diagnostic.Span??bound.Syntax.Span);
            return new(doc,normalized.Model!,bound,inputs.ToImmutable(),factDigest);
        }

        private void CompileScenarios()
        {
            var declarations=bound.Syntax.Declarations.OfType<ScenarioDeclaration>().ToArray();
            if(declarations.Length==0)return;
            // v0.1 branches are independent alternatives, not a probability distribution.
            const string root="root";
            if(bound.Declarations.ContainsKey(root))throw Bad("DML-SCENARIO-001","Reserved scenario root identity.",bound.Declarations[root].NameSpan);
            var baseValues=overridden.Order(StringComparer.Ordinal).Select(id=>new ScenarioValue(id,
                values[id].Boolean is bool b?(b?"true":"false"):values[id].Number!.Value.ToString())).ToImmutableArray();
            scenarios.Add(new(root,0,"base",bound.Syntax.Span,baseValues));
            foreach(var declaration in declarations)
            {
                var overrides=ImmutableArray.CreateBuilder<ScenarioValue>();
                foreach(var item in declaration.Overrides)
                {
                    var symbol=symbols[item.Name.Text];
                    var type=values[symbol.Id].Type;
                    var value=CompileTerm(item.Value,type,true);
                    RequireSame(type,value.Type,item.Span,symbol.Span);RequireValue(value,item.Span);
                    string raw=value.Boolean is bool b?(b?"true":"false"):value.Number!.Value.ToString();
                    overrides.Add(new(symbol.Id,raw));
                    if(value.Number is Rational n)
                    {
                        var lo=Rational.Parse(symbol.Lower!);var hi=Rational.Parse(symbol.Upper!);
                        // Explicit alternatives widen the admitted non-variable fact domain; base facts and branch facts remain fixed.
                        symbols[symbol.Id]=symbol with{Lower=(n.CompareTo(lo)<0?n:lo).ToString(),Upper=(n.CompareTo(hi)>0?n:hi).ToString()};
                    }
                }
                scenarios.Add(new(declaration.Name.Text,1,declaration.Name.Text,declaration.Span,overrides.ToImmutable(),root));
            }
            capabilities.Add("staged.uncertainty");
        }

        private void ValidateWindow(WindowDeclaration w)
        {
            static bool Offset(string s)=>s.EndsWith('Z')||s.Length>=6&&(s[^6]=='+'||s[^6]=='-')&&s[^3]==':';
            string[] formats=["yyyy-MM-dd'T'HH:mm:ssK","yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"];
            if(!Offset(w.From.Text)||!Offset(w.Until.Text)||
                !DateTimeOffset.TryParseExact(w.From.Text,formats,CultureInfo.InvariantCulture,DateTimeStyles.None,out var from)||
                !DateTimeOffset.TryParseExact(w.Until.Text,formats,CultureInfo.InvariantCulture,DateTimeStyles.None,out var until)||until<=from)
                throw Bad("DML-TIME-001","Window requires explicit offsets and positive duration.",w.Span);
            if(w.Calendar.Text!=options.TimeBasis.CalendarId)
                throw Bad("DML-TIME-001","Window calendar differs from the admitted basis.",w.Calendar.Span);
        }

        private SemanticType Declared(TypeSyntax syntax)
        {
            var unit=Unit(syntax.Unit);
            var t=syntax.Kind.Kind switch
            {
                TokenKind.Bool=>Boolean,
                TokenKind.Int=>new(ScalarKind.Integer,unit,null),
                _=>RationalType(unit)
            };
            // Use the authoritative registry to reject unknown atoms even for unused declarations.
            ValidateUnit(t,syntax.Span);
            return t;
        }
        private SemanticType RationalType(string? unit)
        {
            if(unit is not null&&aliases.TryGetValue(unit,out var alias))
                return new(alias.Kind,alias.Kind==ScalarKind.Money?null:unit,alias.Currency);
            return new(ScalarKind.Rational,unit,null);
        }
        private static string? Unit(UnitSyntax? unit)=>unit is null?null:string.Concat(unit.Parts.Select(p=>p.Text));
        private static string? Unit(SemanticType t)=>t.Currency??t.Unit;
        private void ValidateUnit(SemanticType type,SourceSpan span)
        {
            if(Unit(type) is not string unit)return;
            try{_ = options.Measurements.ConvertUnit("1",unit,unit);}
            catch(ContractException e){throw Bad(e.Code=="measure.currency"?"DML-FX-001":"DML-UNIT-001","Unknown or incompatible measurement unit; explicit conversion is required.",span);}
        }
        private string TypeId(SemanticType type)
        {
            if(types.TryGetValue(type,out var existing))return existing.Id;
            var id="t"+types.Count.ToString("D6",CultureInfo.InvariantCulture);
            types.Add(type,new(id,type.Kind,type.Unit,type.Currency));
            return id;
        }
        private Term Node(SemanticType type,Operation op,ImmutableArray<string> args,SourceSpan span,Rational? number=null,bool? boolean=null,string? symbol=null)
        {
            cancellation.ThrowIfCancellationRequested();
            // Bound compiler growth before the UDIR writer enforces the exact byte envelope.
            if(expressions.Count>=DmlParser.MaximumTokens)throw Bad("DML-LIMIT-002","Expression limit exceeded.",span);
            if(type.Kind==ScalarKind.Integer&&number is Rational n&&!n.Integral)
                throw Bad("DML-NUM-002","Integer result requires an exact integral value.",span);
            var id="e"+expressions.Count.ToString("D8",CultureInfo.InvariantCulture);
            string? literal=op==Operation.Literal?(boolean is bool b?(b?"true":"false"):number!.Value.ToString()):null;
            expressions.Add(new(id,op,TypeId(type),args,span,literal,symbol));
            return new(id,type,number,boolean);
        }
        private Term CompileTerm(ExpressionSyntax syntax,SemanticType? expected=null,bool constant=false)
        {
            cancellation.ThrowIfCancellationRequested();
            try {return Core(syntax,expected,constant);}
            catch(OverflowException){throw Bad("DML-NUM-001","Exact arithmetic exceeds the supported profile.",syntax.Span);}
            catch(DivideByZeroException){throw Bad("DML-DIV-001","Division by zero.",syntax.Span);}
        }
        private Term Core(ExpressionSyntax syntax,SemanticType? expected,bool constant)
        {
            switch(syntax)
            {
                case NumberSyntax n:
                {
                    var number=Rational.Decimal(n.Number.Text);
                    var unit=Unit(n.Unit);
                    var type=expected is not null&&expected!=Boolean&&Unit(expected)==unit?expected:
                        number.Integral?new(ScalarKind.Integer,unit,null):RationalType(unit);
                    ValidateUnit(type,n.Span);
                    return Node(type,Operation.Literal,[],n.Span,number);
                }
                case BooleanSyntax b:return Node(Boolean,Operation.Literal,[],b.Span,boolean:b.Value.Kind==TokenKind.True);
                case ParenthesizedSyntax p:return CompileTerm(p.Value,expected,constant);
                case ReferenceSyntax r:
                {
                    var symbol=symbols[r.Name.Text];
                    var st=symbolTypes[symbol.Id];
                    if(constant&&(symbol.Role==SymbolRole.Variable||overridden.Contains(symbol.Id)))
                        throw Bad("DML-SEM-002","Initializer or bound depends on a decision or scenario-varying value.",r.Span);
                    values.TryGetValue(symbol.Id,out var value);
                    // Do not fold nonconstant references, even when their base fact is known.
                    return Node(st,Operation.Symbol,[],r.Span,constant?value?.Number:null,constant?value?.Boolean:null,symbol.Id);
                }
                case UnarySyntax u:
                {
                    var value=CompileTerm(u.Operand,expected,constant);
                    if(value.Type==Boolean)throw Bad("DML-TYPE-001","Cannot negate Boolean.",u.Span);
                    return Node(value.Type,Operation.Negate,[value.Id],u.Span,value.Number is Rational n?-n:null);
                }
                case BinarySyntax b:
                {
                    var op=b.Operator.Kind switch {TokenKind.Plus=>Operation.Add,TokenKind.Minus=>Operation.Subtract,
                        TokenKind.Star=>Operation.Multiply,_=>Operation.Divide};
                    bool additive=op is Operation.Add or Operation.Subtract;
                    var hint=additive?(expected??Hint(b.Left)??Hint(b.Right)??LiteralHint(b.Left,b.Right)):null;
                    var a=CompileTerm(b.Left,hint,constant);
                    var right=CompileTerm(b.Right,additive?a.Type:null,constant);
                    if(a.Type==Boolean||right.Type==Boolean)throw Bad("DML-TYPE-001","Arithmetic requires numeric operands.",b.Span);
                    SemanticType type;
                    if(additive){RequireSame(a.Type,right.Type,b.Right.Span,b.Left.Span);type=a.Type;}
                    else
                    {
                        var unit=CombineUnits(Unit(a.Type),Unit(right.Type),op==Operation.Divide,b.Span);
                        type=expected is not null&&Unit(expected)==unit&&expected!=Boolean?expected:
                            unit is not null&&aliases.ContainsKey(unit)?RationalType(unit):
                            op==Operation.Multiply&&a.Type.Kind==ScalarKind.Integer&&right.Type.Kind==ScalarKind.Integer?
                            new(ScalarKind.Integer,unit,null):RationalType(unit);
                        ValidateUnit(type,b.Span);
                    }
                    Rational? number=null;
                    if(a.Number is Rational x&&right.Number is Rational y)
                        number=op switch{Operation.Add=>x+y,Operation.Subtract=>x-y,Operation.Multiply=>x*y,_=>x/y};
                    return Node(type,op,[a.Id,right.Id],b.Span,number);
                }
                default:throw Bad("DML-CAP-001","Unsupported expression.",syntax.Span);
            }
        }
        private SemanticType? Hint(ExpressionSyntax e)=>e switch
        {
            ReferenceSyntax r when symbolTypes.TryGetValue(r.Name.Text,out var t)=>t,
            ParenthesizedSyntax p=>Hint(p.Value),
            UnarySyntax u=>Hint(u.Operand),
            _=>null
        };
        // A literal-only expression may use the common exact rational representation.
        // Never promote an already typed symbol or silently rescale an operand.
        private SemanticType? LiteralHint(ExpressionSyntax a,ExpressionSyntax b)
        {
            var left=LiteralType(a);var right=LiteralType(b);
            if(left is null||right is null||left==Boolean||right==Boolean||Unit(left)!=Unit(right))return null;
            return left.Kind==ScalarKind.Integer&&right.Kind==ScalarKind.Integer?left:RationalType(Unit(left));
        }
        private SemanticType? LiteralType(ExpressionSyntax e)
        {
            switch(e)
            {
                case NumberSyntax n:
                    var unit=Unit(n.Unit);
                    return Rational.Decimal(n.Number.Text).Integral?new(ScalarKind.Integer,unit,null):RationalType(unit);
                case ParenthesizedSyntax p:return LiteralType(p.Value);
                case UnarySyntax u:return LiteralType(u.Operand);
                case BinarySyntax b when b.Operator.Kind is TokenKind.Plus or TokenKind.Minus:
                    return LiteralHint(b.Left,b.Right);
                case BinarySyntax b:
                    var left=LiteralType(b.Left);var right=LiteralType(b.Right);
                    if(left is null||right is null)return null;
                    var divide=b.Operator.Kind==TokenKind.Slash;
                    var combined=CombineUnits(Unit(left),Unit(right),divide,b.Span);
                    return !divide&&left.Kind==ScalarKind.Integer&&right.Kind==ScalarKind.Integer&&
                        (combined is null||!aliases.ContainsKey(combined))?new(ScalarKind.Integer,combined,null):RationalType(combined);
                default:return null;
            }
        }
        private (Term,Term) Comparable(ExpressionSyntax a,ExpressionSyntax b)
        {
            var hint=Hint(a)??Hint(b)??LiteralHint(a,b);
            var left=CompileTerm(a,hint);var right=CompileTerm(b,left.Type);
            RequireSame(left.Type,right.Type,b.Span,a.Span);
            return(left,right);
        }
        private void RequireSame(SemanticType a,SemanticType b,SourceSpan span,SourceSpan? related=null)
        {
            if(a==b)return;
            var ca=CurrencyAtoms(a);var cb=CurrencyAtoms(b);
            if(ca.Count>0&&cb.Count>0&&!ca.SetEquals(cb))
                throw new Failure("DML-FX-001","Currency conversion requires an explicit versioned FX snapshot.",span,related);
            throw new Failure("DML-UNIT-001","Operand types or units differ; explicit normalization is required.",span,related);
        }
        private HashSet<string> CurrencyAtoms(SemanticType type)
        {
            var atoms=(Unit(type)??"").Split(['*','/'],StringSplitOptions.RemoveEmptyEntries);
            return atoms.Where(a=>aliases.TryGetValue(a,out var alias)&&alias.Kind==ScalarKind.Money)
                .ToHashSet(StringComparer.Ordinal);
        }
        private static void RequireValue(Term value,SourceSpan span)
        {
            if(value.Number is null&&value.Boolean is null)throw Bad("DML-SEM-002","Expected a frozen constant value.",span);
        }
        // Syntactic cancellation of identical unit atoms only. M04 independently validates every resulting product/quotient.
        private static string? CombineUnits(string? left,string? right,bool divide,SourceSpan span)
        {
            var powers=new SortedDictionary<string,int>(StringComparer.Ordinal);
            void Add(string? unit,int sign)
            {
                if(unit is null)return;
                int start=0,multiplier=sign;
                for(int i=0;i<=unit.Length;i++)
                {
                    if(i<unit.Length&&unit[i] is not('*' or '/'))continue;
                    var atom=unit[start..i];powers[atom]=powers.GetValueOrDefault(atom)+multiplier;
                    multiplier=i<unit.Length&&unit[i]=='/'?-sign:sign;start=i+1;
                }
            }
            Add(left,1);Add(right,divide?-1:1);
            var numerator=powers.Where(p=>p.Value>0).SelectMany(p=>Enumerable.Repeat(p.Key,p.Value)).ToArray();
            var denominator=powers.Where(p=>p.Value<0).SelectMany(p=>Enumerable.Repeat(p.Key,-p.Value)).ToArray();
            if(numerator.Length==0&&denominator.Length!=0)
                throw Bad("DML-CAP-001","Reciprocal-only units are not representable in UDIR v1 unit grammar.",span);
            if(numerator.Length==0)return null;
            var result=string.Join('*',numerator)+(denominator.Length==0?"":"/"+string.Join('/',denominator));
            if(result.Length>DmlParser.MaximumUnitLength)throw Bad("DML-LIMIT-006","Combined unit exceeds supported length.",span);
            return result;
        }
        private static Failure Bad(string code,string message,SourceSpan span)=>new(code,message,span);
    }
}
