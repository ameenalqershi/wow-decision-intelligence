using ComponentStudy;
using System.Text.Json;

var rows = new List<object>();
int comparisons = 0;

StudyResult Compare(ExactStudy study, Model model)
{
    StudyResult result = study.Solve(model);
    int? reference = Oracle.Minimum(model);
    if (result.Best?.Value != reference ||
        (result.Best is {} best && Oracle.Evaluate(model, best.Mask) != best.Value))
        throw new InvalidOperationException("Differential optimum or independent feasibility check failed.");
    comparisons++;
    return result;
}

void Check(string name, ExactStudy study, Model model, int hits, int components, int? optimum)
{
    var r = Compare(study, model);
    if (r.Reused != hits || r.Components != components || r.Best?.Value != optimum)
        throw new InvalidOperationException("Unexpected structure or optimum: " + name);
    rows.Add(new { name, r.Components, r.Reused, r.Assignments, fullAssignments = 1 << model.Size, optimum });
}

var exact = new ExactStudy();
var original = new Model(4, [new(3,1,1), new(12,1,1)],
    [new(1,1), new(2,3), new(4,1), new(8,5)]);
var changed = original with { Costs = [new(1,7), new(2,3), new(4,1), new(8,5)] };
Check("cold", exact, original, 0, 2, 2);
Check("local-price-change", exact, changed, 1, 2, 4);
Check("reordered", exact, changed with { Costs = changed.Costs.Reverse().ToArray() }, 2, 2, 4);
Check("shared-capacity", exact, changed with { Capacities = [..changed.Capacities,new(6,0,1)] }, 0, 1, 8);
Check("joint-objective", exact, changed with { Costs = [..changed.Costs,new(6,10)] }, 0, 1, 8);
Check("edge-removed", exact, changed, 2, 2, 4);
Check("infeasible", exact, changed with { Capacities = [..changed.Capacities,new(3,0,0)] }, 1, 2, null);

var random = new Random(20261127);
for (int i = 0; i < 128; i++)
{
    int n = random.Next(2,9), limit = 1 << n;
    var caps = Enumerable.Range(0,n).Select(_ =>
        new Capacity(random.Next(1,limit),random.Next(0,2),random.Next(1,n+1))).ToArray();
    var costs = Enumerable.Range(0,n+2).Select(_ => new Cost(random.Next(1,limit),random.Next(-8,9))).ToArray();
    var m = new Model(n,caps,costs);
    var study = new ExactStudy();
    Compare(study,m);
    Compare(study,m with { Costs = [..costs, new(1,-3)] });
    Compare(study,m with { Capacities = caps.Skip(1).ToArray() });
}
Console.WriteLine(JsonSerializer.Serialize(new { seed = 20261127, comparisons, namedCases = rows },
    new JsonSerializerOptions { WriteIndented = true }));
