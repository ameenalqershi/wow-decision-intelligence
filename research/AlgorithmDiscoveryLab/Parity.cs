using ComponentStudy;
using System.Diagnostics;
using System.Numerics;

namespace AlgorithmDiscoveryLab;

// Pure relaxation: null means the eligible pair equations are inconsistent,
// not that the original model is feasible when a finite bound is returned.
internal static class ParityFloor
{
    public static long? Minimum(ReadOnlySpan<long> delta, Capacity[] rows)
    {
        int n = delta.Length;
        if (n is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(delta));
        int universe = (1 << n) - 1;
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = [];
        foreach (var row in rows)
        {
            if (row.Scope <= 0 || (row.Scope & ~universe) != 0)
                throw new ArgumentException("Invalid row scope");
            if (row.Lower != 1 || row.Upper != 1 || BitOperations.PopCount((uint)row.Scope) != 2)
                continue;
            int a = BitOperations.TrailingZeroCount((uint)row.Scope);
            int b = BitOperations.Log2((uint)row.Scope);
            adj[a].Add(b); adj[b].Add(a);
        }
        var colors = new int[n]; Array.Fill(colors, -1);
        var queue = new int[n];
        long floor = 0;
        for (int root = 0; root < n; root++)
        {
            if (colors[root] != -1) continue;
            int head = 0, tail = 0;
            queue[tail++] = root; colors[root] = 0;
            long zero = 0, one = 0;
            while (head < tail)
            {
                int v = queue[head++];
                if (colors[v] == 0) zero = checked(zero + delta[v]);
                else one = checked(one + delta[v]);
                foreach (int u in adj[v])
                {
                    if (colors[u] == colors[v]) return null;
                    if (colors[u] != -1) continue;
                    colors[u] = 1 - colors[v];
                    queue[tail++] = u;
                }
            }
            floor = checked(floor + Math.Min(zero, one));
        }
        return floor;
    }
}

internal sealed class ParityTransfer(bool warmFallback = false) : ICandidate
{
    private readonly ICandidate fallback = warmFallback ? new FullSearch(true) : new Components(true);
    private Model? previous;
    private Optimum? optimum;
    public Answer Solve(Model model)
    {
        // The historical reference solver and Answer use Int32. Reject inputs
        // outside that safe envelope rather than silently truncating a certificate.
        if (model.Size is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(model));
        int universe = (1 << model.Size) - 1;
        if (model.Capacities.Any(r => r.Scope <= 0 || (r.Scope & ~universe) != 0))
            throw new ArgumentException("Invalid row scope");
        int[] prices = Corpus.Prices(model);
        long positive = 0, negative = 0;
        foreach (int p in prices) { if (p > 0) positive += p; else negative += p; }
        if (positive > int.MaxValue || negative < int.MinValue)
            throw new ArgumentException("Outside Int32 fixture objective envelope");
        // Preserve a private immutable-by-ownership snapshot across revisions.
        model = new Model(model.Size, model.Capacities.ToArray(), model.Costs.ToArray());
        long start = Stopwatch.GetTimestamp();
        Optimum? accepted = null;
        if (previous is {} old && optimum is {} witness && model.Size == old.Size &&
            old.Capacities.All(model.Capacities.Contains) && Oracle.Evaluate(model,witness.Mask) is int feasibleCost)
        {
            int[] prior = Corpus.Prices(old);
            var delta = new long[prices.Length];
            for (int i = 0; i < delta.Length; i++) delta[i] = (long)prices[i] - prior[i];
            if (ParityFloor.Minimum(delta,model.Capacities) is long floor &&
                checked((long)witness.Value + floor) == feasibleCost)
                accepted = new(witness.Mask,feasibleCost);
        }
        long gate = Stopwatch.GetTimestamp()-start;
        var result = accepted is {} hit ? new Answer(hit,0,0,0,true,gate) :
            fallback.Solve(model) with { GateTicks = gate };
        previous = model; optimum = result.Best;
        return result;
    }
}
