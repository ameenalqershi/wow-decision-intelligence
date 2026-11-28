using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using WowDi.Contracts;
using WowDi.Solver.Contracts;
using WowDi.Udir;
using WowDi.Verification.Independent;

namespace WowDi.Optimization.Separability;

public enum ComponentOutcome { Unsupported, BudgetExceeded, Infeasible, VerifiedOptimum, VerificationFailed }
public sealed record ComponentScope(ImmutableArray<string> Variables,ImmutableArray<string> Constraints);
public sealed record SeparationResult(string ModelSha256,ComponentOutcome Outcome,string? Diagnostic,
    ImmutableArray<ComponentScope> Components,ImmutableArray<string> ConstantConstraints,int CheckedAssignments,
    string? Objective,ImmutableDictionary<string,string>? Assignment,VerificationReport? Verification)
{
    public string Profile=>"wowdi.x02.integer-affine/1";
}

/// <summary>Bounded exact original-UDIR decomposition. No cache, native backend, or publication authority.</summary>
public static class ExactComponents
{
    private const int MaxAssignments=4096;
    private const int MaxNodes=4096;
    private const int MaxVariables=256;
    private const long MaxMagnitude=1_000_000_000_000L;
    private const long MaxSolveWork=2_000_000;
    private sealed class Unsupported(string code):Exception(code);
    private sealed record Variable(string Id,ScalarKind Kind,BigInteger Lo,BigInteger Hi);
    private sealed record Affine(BigInteger Constant,ImmutableDictionary<int,BigInteger> Terms)
    {
        internal BigInteger Evaluate(IReadOnlyDictionary<int,BigInteger> x)
        {var n=Constant;foreach(var t in Terms)n+=t.Value*x[t.Key];return n;}
    }
    private sealed record Node(Affine Form,ImmutableHashSet<int> Reads,Operation? Relation);
    private sealed record Row(string Id,Node Value);

    public static SeparationResult Solve(UdirDocument document,MeasurementSemantics measurements,VerificationScope scope,
        int maximumAssignments=MaxAssignments,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(document);ArgumentNullException.ThrowIfNull(measurements);ArgumentNullException.ThrowIfNull(scope);
        if(maximumAssignments is <1 or >MaxAssignments)throw new ArgumentOutOfRangeException(nameof(maximumAssignments));
        ct.ThrowIfCancellationRequested();
        var hash=Convert.ToHexStringLower(SHA256.HashData(document.Write()));
        var m=document.Model;
        SeparationResult Fail(ComponentOutcome outcome,string code)=>new(hash,outcome,code,[],[],0,null,null,null);
        if(scope.ModelSha256!=hash||scope.TenantId!=m.TenantId||scope.ModelId!=m.ModelId||scope.RevisionId!=m.RevisionId||string.IsNullOrWhiteSpace(scope.RunId))
            return Fail(ComponentOutcome.Unsupported,"components.scope");
        try
        {
            document.ValidateMeasurements(measurements);
            return new Worker(document,measurements,scope,hash,maximumAssignments,ct).Run();
        }
        catch(Unsupported ex){return Fail(ComponentOutcome.Unsupported,ex.Message);}
        catch(ContractException ex){return Fail(ComponentOutcome.Unsupported,ex.Code);}
    }

    private sealed class Worker(UdirDocument document,MeasurementSemantics measurements,VerificationScope scope,string hash,int budget,CancellationToken ct)
    {
        private readonly Model m=document.Model;
        private readonly List<Variable> variables=[];
        private readonly Dictionary<string,Node> symbols=new(StringComparer.Ordinal);
        private readonly Dictionary<string,Node> nodes=new(StringComparer.Ordinal);
        private static readonly ImmutableDictionary<int,BigInteger> Empty=ImmutableDictionary<int,BigInteger>.Empty;
        private static Affine Constant(BigInteger n)=>new(n,Empty);
        private static BigInteger Number(string? s)
        {
            if(!BigInteger.TryParse(s,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var n)||BigInteger.Abs(n)>MaxMagnitude)
                throw new Unsupported("components.numericProfile");
            return n;
        }
        private static bool Holds(Node n,IReadOnlyDictionary<int,BigInteger> x)
        {
            var v=n.Form.Evaluate(x);
            return n.Relation switch
            {Operation.Equal=>v.IsZero,Operation.LessOrEqual=>v.Sign<=0,Operation.GreaterOrEqual=>v.Sign>=0,null=>v==1,_=>throw new Unsupported("components.predicate")};
        }
        private void Check(Affine f)
        {
            // Absolute activity bound guarantees all supported original expression evaluations
            // are total and well within S04's exact numeric representation.
            var magnitude=BigInteger.Abs(f.Constant);
            foreach(var (i,c) in f.Terms)magnitude+=BigInteger.Abs(c)*BigInteger.Max(BigInteger.Abs(variables[i].Lo),BigInteger.Abs(variables[i].Hi));
            if(magnitude>MaxMagnitude||f.Terms.Values.Any(c=>BigInteger.Abs(c)>MaxMagnitude))
                throw new Unsupported("components.activity");
        }
        private static Affine Add(Affine a,Affine b,int sign)
        {
            var t=a.Terms.ToBuilder();
            foreach(var (i,c) in b.Terms)
            {
                var v=t.GetValueOrDefault(i)+sign*c;
                if(v.IsZero)t.Remove(i);else t[i]=v;
            }
            return new(a.Constant+sign*b.Constant,t.ToImmutable());
        }
        private static Affine Scale(Affine a,BigInteger k)=>k.IsZero?Constant(0):
            new(a.Constant*k,a.Terms.ToImmutableDictionary(t=>t.Key,t=>t.Value*k));

        internal SeparationResult Run()
        {
            if(document.MinorVersion!=0||document.Extensions is not null||!m.Scenarios.IsEmpty||
                m.Requires.Any(f=>f!="integer.linear")||m.Constraints.Any(r=>!r.Hard)||
                m.Objectives.Length!=1||m.Objectives[0].Weight!="1"||m.Objectives[0].Tolerance!="0"||
                m.Expressions.Length>MaxNodes||m.Symbols.Length>MaxNodes||m.Constraints.Length>MaxNodes)
                throw new Unsupported("components.profile");
            var types=m.Types.ToDictionary(t=>t.Id,StringComparer.Ordinal);
            if(types.Values.Any(t=>t.Kind is not (ScalarKind.Integer or ScalarKind.Boolean)||t.Unit is not null||t.Currency is not null))
                throw new Unsupported("components.type");
            foreach(var s in m.Symbols.OrderBy(s=>s.Id,StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var kind=types[s.TypeId].Kind;
                if(s.Role==SymbolRole.Variable)
                {
                    if(variables.Count>=MaxVariables)throw new Unsupported("components.variables");
                    var lo=kind==ScalarKind.Boolean?BigInteger.Zero:Number(s.Lower);
                    var hi=kind==ScalarKind.Boolean?BigInteger.One:Number(s.Upper);
                    int i=variables.Count;
                    variables.Add(new(s.Id,kind,lo,hi));
                    symbols.Add(s.Id,new(new(0,Empty.Add(i,1)),ImmutableHashSet.Create(i),null));
                }
                else
                {
                    // v1 supports only fixed numeric facts in the original domain, not
                    // host-selected snapshots, mutable parameters, or implicit resource semantics.
                    if(kind==ScalarKind.Boolean||s.Lower is null||s.Lower!=s.Upper)
                        throw new Unsupported("components.unfrozenFact");
                    symbols.Add(s.Id,new(Constant(Number(s.Lower)),[],null));
                }
            }
            var pending=m.Expressions.ToDictionary(e=>e.Id,e=>e.Arguments.Length,StringComparer.Ordinal);
            var users=pending.Keys.ToDictionary(k=>k,_=>new List<string>(),StringComparer.Ordinal);
            var source=m.Expressions.ToDictionary(e=>e.Id,StringComparer.Ordinal);
            foreach(var e in m.Expressions)foreach(var a in e.Arguments)users[a].Add(e.Id);
            var ready=new Queue<string>(pending.Where(p=>p.Value==0).Select(p=>p.Key));
            while(ready.TryDequeue(out var id))
            {
                ct.ThrowIfCancellationRequested();
                var e=source[id];var kind=types[e.TypeId].Kind;
                Node n;
                if(e.Op==Operation.Symbol)n=symbols[e.SymbolId!];
                else if(e.Op==Operation.Literal)n=new(Constant(kind==ScalarKind.Boolean?(e.Literal=="true"?1:0):Number(e.Literal)),[],null);
                else
                {
                    var a=nodes[e.Arguments[0]];
                    var b=e.Arguments.Length==2?nodes[e.Arguments[1]]:null;
                    // Comparisons of nested predicates are outside the profile; never
                    // reinterpret a Boolean predicate as its affine residual.
                    if(a.Relation is not null||b?.Relation is not null)throw new Unsupported("components.nestedPredicate");
                    var reads=b is null?a.Reads:a.Reads.Union(b.Reads);
                    var form=e.Op switch
                    {
                        Operation.Negate=>Scale(a.Form,-1),
                        Operation.Add=>Add(a.Form,b!.Form,1),
                        Operation.Subtract=>Add(a.Form,b!.Form,-1),
                        Operation.Multiply when a.Reads.IsEmpty=>Scale(b!.Form,a.Form.Constant),
                        Operation.Multiply when b!.Reads.IsEmpty=>Scale(a.Form,b.Form.Constant),
                        Operation.Equal or Operation.LessOrEqual or Operation.GreaterOrEqual=>Add(a.Form,b!.Form,-1),
                        _=>throw new Unsupported("components.nonAffine")
                    };
                    n=new(form,reads,kind==ScalarKind.Boolean?e.Op:null);
                }
                Check(n.Form);nodes.Add(id,n);
                foreach(var u in users[id])if(--pending[u]==0)ready.Enqueue(u);
            }
            if(nodes.Count!=source.Count)throw new Unsupported("components.dag");
            var rows=m.Constraints.Select(c=>new Row(c.Id,nodes[c.PredicateId])).OrderBy(r=>r.Id,StringComparer.Ordinal).ToArray();
            var goal=m.Objectives[0];var objective=nodes[goal.ExpressionId].Form;
            int[] parent=Enumerable.Range(0,variables.Count).ToArray();
            int Root(int i){while(parent[i]!=i){parent[i]=parent[parent[i]];i=parent[i];}return i;}
            foreach(var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                if(row.Value.Reads.IsEmpty)continue;
                // Use all source incidences, including cancelled/zero terms: conservative.
                var first=row.Value.Reads.Min();
                foreach(int i in row.Value.Reads){int a=Root(first),b=Root(i);if(a!=b)parent[Math.Max(a,b)]=Math.Min(a,b);}
            }
            var groups=Enumerable.Range(0,variables.Count).GroupBy(Root).OrderBy(g=>g.Key).Select(g=>g.ToArray()).ToArray();
            var rowGroups=rows.Where(r=>!r.Value.Reads.IsEmpty).ToLookup(r=>Root(r.Value.Reads.Min()));
            var constant=rows.Where(r=>r.Value.Reads.IsEmpty).ToArray();
            var scopes=groups.Select(g=>new ComponentScope(g.Select(i=>variables[i].Id).ToImmutableArray(),
                rowGroups[Root(g[0])].Select(r=>r.Id).ToImmutableArray())).ToImmutableArray();
            var constantIds=constant.Select(r=>r.Id).ToImmutableArray();
            SeparationResult Result(ComponentOutcome o,string? diagnostic,int checkedCount=0,string? cost=null,
                ImmutableDictionary<string,string>? witness=null,VerificationReport? report=null)=>new(hash,o,diagnostic,scopes,constantIds,checkedCount,cost,witness,report);
            var assignment=new Dictionary<int,BigInteger>();
            if(constant.Any(r=>!Holds(r.Value,assignment)))return Result(ComponentOutcome.Infeasible,"components.constantContradiction");
            // Preflight exact sum of Cartesian sizes and conservative work bound.
            var counts=new List<int>();int total=0;long work=0;
            foreach(var g in groups)
            {
                BigInteger count=1;
                foreach(var i in g)count*=variables[i].Hi-variables[i].Lo+1;
                if(count>budget-total)return Result(ComponentOutcome.BudgetExceeded,"components.assignmentBudget");
                int size=(int)count;total+=size;counts.Add(size);
                work+=(long)size*(g.Length+1+rowGroups[Root(g[0])].Sum(r=>1+r.Value.Form.Terms.Count));
                if(work>MaxSolveWork)return Result(ComponentOutcome.BudgetExceeded,"components.workBudget");
            }
            int checkedCount=0;
            for(int k=0;k<groups.Length;k++)
            {
                var g=groups[k];var localRows=rowGroups[Root(g[0])].ToArray();
                BigInteger? best=null;Dictionary<int,BigInteger>? bestValues=null;
                for(int code=0;code<counts[k];code++)
                {
                    ct.ThrowIfCancellationRequested();
                    var x=new Dictionary<int,BigInteger>();BigInteger index=code;
                    foreach(int i in g)
                    {var v=variables[i];var width=v.Hi-v.Lo+1;x.Add(i,v.Lo+index%width);index/=width;}
                    checkedCount++;
                    if(!localRows.All(r=>Holds(r.Value,x)))continue;
                    BigInteger cost=0;
                    foreach(int i in g)cost+=objective.Terms.GetValueOrDefault(i)*x[i];
                    if(best is null||(goal.Direction==Direction.Minimize?cost<best:cost>best))
                    {best=cost;bestValues=x;}
                }
                if(bestValues is null)return Result(ComponentOutcome.Infeasible,"components.emptyComponent",checkedCount);
                foreach(var (i,value) in bestValues)assignment.Add(i,value);
            }
            var values=assignment.ToImmutableDictionary(p=>variables[p.Key].Id,p=>variables[p.Key].Kind==ScalarKind.Boolean?
                (p.Value.IsZero?"false":"true"):p.Value.ToString(CultureInfo.InvariantCulture),StringComparer.Ordinal);
            var objectiveValue=objective.Evaluate(assignment).ToString(CultureInfo.InvariantCulture);
            var candidate=new SolverResult(scope.TenantId,scope.ModelId,scope.RevisionId,scope.RunId,new("exact-components","1","managed"),
                MathematicalStatus.Feasible,Termination.Completed,values,[new(goal.Id,objectiveValue,null,"0","0")],null,new(null,null,null));
            var verification=OriginalVerifier.Check(document,measurements,scope,candidate,ct:ct);
            if(!verification.Accepted)return Result(ComponentOutcome.VerificationFailed,"components.originalVerification",checkedCount,report:verification);
            return Result(ComponentOutcome.VerifiedOptimum,null,checkedCount,objectiveValue,values,verification);
        }
    }
}
