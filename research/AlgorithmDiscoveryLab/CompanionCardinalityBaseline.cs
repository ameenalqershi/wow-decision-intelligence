namespace AlgorithmDiscoveryLab;

/// <summary>Residual minimum over opposite-bit equations and one optional exact quota.</summary>
internal static class CardinalityResidual
{
    internal sealed record Row(bool[] Member, int Count);
    private readonly record struct Choice(long A, long B, int CountA, int CountB);

    internal static long? Minimum(long[] delta, IReadOnlyList<(int A, int B)> edges, Row? quota,
        CancellationToken cancellation = default)
    {
        int n = delta.Length;
        if (quota != null && (quota.Member.Length != n || quota.Count < 0 || quota.Count > n))
            throw new ArgumentException("Invalid quota.");
        // All intermediate partial assignments are disjoint sums. Reserve MaxValue as infinity.
        Int128 magnitude = 0;
        foreach (long value in delta) magnitude += Int128.Abs(value);
        if (magnitude >= long.MaxValue) throw new OverflowException("Residual exceeds the checked Int64 envelope.");
        var graph = new List<int>?[n];
        foreach (var (a, b) in edges)
        {
            if ((uint)a >= n || (uint)b >= n) throw new ArgumentException("Unknown endpoint.");
            (graph[a] ??= []).Add(b);
            (graph[b] ??= []).Add(a);
        }
        var choices = new List<Choice>();
        var queue = new int[n];
        var color = new byte[n]; // 0 unvisited, 1 and 2 are the two classes.
        long uncoupled = 0;
        for (int root = 0; root < n; root++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (color[root] != 0) continue;
            long a = 0, b = 0;
            int ka = 0, kb = 0;
            color[root] = 1;
            queue[0] = root;
            int tail = 1;
            for (int head = 0; head < tail; head++)
            {
                var v = queue[head];
                if (color[v] == 1) { a += delta[v]; if (quota?.Member[v] == true) ka++; }
                else { b += delta[v]; if (quota?.Member[v] == true) kb++; }
                if (graph[v] is not {} neighbors) continue;
                foreach (var u in neighbors)
                {
                    if (color[u] == color[v]) return null;
                    if (color[u] != 0) continue;
                    color[u] = (byte)(3 - color[v]);
                    queue[tail++] = u;
                }
            }
            if (quota == null) uncoupled += Math.Min(a, b);
            else choices.Add(new(a, b, ka, kb));
        }
        if (quota == null) return uncoupled;

        int k = quota.Count;
        long[] prev = new long[k + 1], next = new long[k + 1];
        Array.Fill(prev, long.MaxValue);
        prev[0] = 0;
        foreach (var c in choices)
        {
            cancellation.ThrowIfCancellationRequested();
            Array.Fill(next, long.MaxValue);
            for (int j = 0; j <= k; j++)
            {
                if (prev[j] == long.MaxValue) continue;
                if (c.CountA <= k - j) next[j + c.CountA] = Math.Min(next[j + c.CountA], prev[j] + c.A);
                if (c.CountB <= k - j) next[j + c.CountB] = Math.Min(next[j + c.CountB], prev[j] + c.B);
            }
            (prev, next) = (next, prev);
        }
        return prev[k] == long.MaxValue ? null : prev[k];
    }
}
