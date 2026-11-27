using Google.OrTools.Sat;

foreach (bool outage in new[] { false, true })
{
    const int horizon = 10;
    int[] durations = [3, 2], dues = [4, 2], weights = [3, 1];
    var model = new CpModel();
    var starts = new IntVar[2];
    var tardiness = new IntVar[2];
    var intervals = new List<IntervalVar>();
    var earlyConsumption = new List<ILiteral>();
    for (int j = 0; j < 2; j++)
    {
        starts[j] = model.NewIntVar(0, horizon - durations[j], $"start{j}");
        intervals.Add(model.NewFixedSizeIntervalVar(starts[j], durations[j], $"machine{j}"));
        tardiness[j] = model.NewIntVar(0, horizon, $"late{j}");
        model.Add(tardiness[j] >= starts[j] + durations[j] - dues[j]);
        var early = model.NewBoolVar($"usesInitialStock{j}");
        model.Add(starts[j] < 3).OnlyEnforceIf(early);
        model.Add(starts[j] >= 3).OnlyEnforceIf(early.Not());
        earlyConsumption.Add(early);
    }
    if (outage) intervals.Add(model.NewFixedSizeIntervalVar(model.NewConstant(1), 2, "outage"));
    model.AddNoOverlap(intervals);
    model.AddAtMostOne(earlyConsumption);
    model.Minimize(LinearExpr.WeightedSum(tardiness, weights));
    var solver = new CpSolver { StringParameters = "num_search_workers:1 random_seed:1 max_time_in_seconds:10" };
    if (solver.Solve(model) != CpSolverStatus.Optimal)
        throw new InvalidOperationException("Expected an optimum on the small research fixture.");
    int[] plan = starts.Select(x => checked((int)solver.Value(x))).ToArray();

    int? Verify(int a, int b)
    {
        int[] s = [a,b];
        if (s.Where((v,j) => v < 0 || v+durations[j] > horizon).Any()) return null;
        if (a < b+durations[1] && b < a+durations[0]) return null;
        for (int t = 0; t < horizon; t++)
        {
            if (s.Count(v => v <= t) > (t < 3 ? 1 : 2)) return null;
            if (outage && t >= 1 && t < 3 && s.Where((v,j) => v <= t && t < v+durations[j]).Any())
                return null;
        }
        return s.Select((v,j) => weights[j]*Math.Max(0,v+durations[j]-dues[j])).Sum();
    }
    var costs = from a in Enumerable.Range(0,horizon)
                from b in Enumerable.Range(0,horizon)
                let cost = Verify(a,b) where cost.HasValue select cost.Value;
    int expected = costs.Min();
    if (Verify(plan[0],plan[1]) != expected || solver.ObjectiveValue != expected ||
        solver.BestObjectiveBound != expected)
        throw new InvalidOperationException("Schedule, objective or bound disagrees with enumeration.");
    Console.WriteLine($"Outage={outage} startA={plan[0]} startB={plan[1]} verifiedCost={expected} bound={solver.BestObjectiveBound}");
}
