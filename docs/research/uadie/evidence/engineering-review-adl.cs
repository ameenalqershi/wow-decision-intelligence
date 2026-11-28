using AlgorithmDiscoveryLab;
using ComponentStudy;
using Google.OrTools.Sat;
using System.Text.Json;

// Direct, independently authored translation. Require a proved finite native optimum
// or infeasibility, then reevaluate each returned witness without the lab's Oracle.
static int? Reference(Model m)
{
    var cp = new CpModel();
    var vars = Enumerable.Range(0,m.Size).Select(i => cp.NewIntVar(0,1,$"x_{i}")).ToArray();
    foreach (var row in m.Capacities)
    {
        var selected = Enumerable.Range(0,m.Size).Where(i => ((row.Scope >> i) & 1) == 1)
            .Select(i => vars[i]);
        var count = LinearExpr.Sum(selected);
        cp.AddLinearConstraint(count,row.Lower,row.Upper);
    }
    cp.Minimize(LinearExpr.WeightedSum(vars,m.Costs.Select(t => (long)t.Weight)));
    var solver = new CpSolver { StringParameters = "num_search_workers:1 random_seed:39237 max_time_in_seconds:2" };
    return solver.Solve(cp) switch {
        CpSolverStatus.Optimal => checked((int)solver.ObjectiveValue),
        CpSolverStatus.Infeasible => null,
        var status => throw new Exception($"Independent solver failed to qualify model: {status}")
    };
}

static bool WitnessAgrees(Model m, Optimum? result, int? expected)
{
    if (result == null) return expected == null;
    if (result.Mask < 0 || result.Mask >= 1 << m.Size || expected != result.Value) return false;
    var selected = Enumerable.Range(0,m.Size).Where(i => (result.Mask / (1 << i)) % 2 == 1).ToArray();
    return selected.Sum(i => m.Costs[i].Weight) == result.Value &&
        m.Capacities.All(c => {
            int count = selected.Count(i => (c.Scope / (1 << i)) % 2 == 1);
            return c.Lower <= count && count <= c.Upper;
        });
}

int models=0, checks=0, infeasible=0, transfers=0;
void CompareSequence(IEnumerable<Model> sequence)
{
    ICandidate[] arms = [new FullSearch(false), new FullSearch(true), new Components(false),new Components(true),new Transfer(false),new Transfer(true)];
    foreach (var model in sequence)
    {
        var expected = Reference(model);
        models++;
        if (expected == null) infeasible++;
        foreach (var arm in arms)
        {
            var result = arm.Solve(model);
            if (!WitnessAgrees(model,result.Best,expected))
                throw new InvalidOperationException(JsonSerializer.Serialize(new { model, result, expected, arm=arm.GetType().Name }));
            checks++;
            if(result.Transported) transfers++;
        }
    }
}

// Cover all published reservoir seeds and family combinations.
foreach(bool holdout in new[]{false,true})
foreach(string family in (holdout ? new[]{"flat-shift","global-price","bridge","shortage","dense","relax","outage"} :
    new[]{"flat-shift","global-price","bridge","shortage"}))
for(int i=0; i<8; i++)
{
    var tuple = Corpus.Generate(family,(holdout ? 91000:41000)+i,holdout ? 4+i%3:3+i%2);
    CompareSequence([tuple.Source, tuple.Revision]);
}
int publishedModels=models;
// Add multi-step histories with nonadjacent, overlapping exactly-one rows, a
// dominated duplicate, row relaxation, signed coefficient changes and restoration.
var rng = new Random(39237);
for(int example=0;example<96;example++)
{
    int n=rng.Next(4,9);
    var positions=Enumerable.Range(0,n).OrderBy(_=>rng.Next()).ToArray();
    int Pair(int i,int j) => (1<<positions[i]) | (1<<positions[j]);
    Capacity[] rows=[new(Pair(0,1),1,1),new(Pair(1,2),1,1),new(Pair(2,3),1,1)];
    var costs=Enumerable.Range(0,n).Select(i=> new Cost(1<<i,rng.Next(-7,9))).ToArray();
    var source = new Model(n,rows,costs);
    var delta = costs.Select(c=>c with {Weight=c.Weight+rng.Next(-3,4)}).ToArray();
    var tight = source with {Capacities=[..rows,new(Pair(0,2),0,rng.Next(1,3)),rows[0]], Costs=delta};
    var relaxed=tight with {Capacities=tight.Capacities.Skip(1).Reverse().ToArray()};
    var impossible=tight with {Capacities=[..tight.Capacities,new(Pair(0,1),0,0)]};
    CompareSequence([source,tight,relaxed,impossible,source]);
}
Console.WriteLine(JsonSerializer.Serialize(new {seed=39237,models,publishedModels,augmentedModels=models-publishedModels,checks,infeasible,transfers,
    comparator="Google OR-Tools CP-SAT 9.15.6755, 1 worker; independent witness evaluator", failures=0}));
