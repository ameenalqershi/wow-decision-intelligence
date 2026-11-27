using System.Numerics;
using System.Text.Json;

namespace ComponentStudy;

internal sealed class ExactStudy
{
    // The experiment is single-tenant and in memory. The key contains the entire
    // subproblem, including its global bit positions and objective coefficients.
    private readonly Dictionary<string, Optimum?> cache = new(StringComparer.Ordinal);

    public StudyResult Solve(Model model)
    {
        if (model.Size is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(model));
        int universe = (1 << model.Size) - 1;
        int[] scopes = model.Capacities.Select(c => c.Scope)
            .Concat(model.Costs.Select(c => c.Scope)).ToArray();
        if (scopes.Any(s => s <= 0 || (s & ~universe) != 0))
            throw new ArgumentException("Every scope must be nonempty and inside the declared domain.");
        int remaining = universe, merged = 0, total = 0, count = 0, hits = 0, checkedMasks = 0;
        bool feasible = true;
        while (remaining != 0)
        {
            int component = remaining & -remaining;
            int previous;
            do
            {
                previous = component;
                foreach (int scope in scopes)
                    if ((component & scope) != 0) component |= scope;
            } while (previous != component);
            remaining &= ~component;
            count++;
            var caps = model.Capacities.Where(c => (c.Scope & component) != 0)
                .OrderBy(c => c.Scope).ThenBy(c => c.Lower).ThenBy(c => c.Upper).ToArray();
            var costs = model.Costs.Where(c => (c.Scope & component) != 0)
                .OrderBy(c => c.Scope).ThenBy(c => c.Weight).ToArray();
            string key = JsonSerializer.Serialize(new { component, caps, costs });
            if (cache.TryGetValue(key, out Optimum? local))
            {
                hits++;
            }
            else
            {
                int subset = component;
                do
                {
                    checkedMasks++;
                    int candidate = subset;
                    if (caps.All(c => BitOperations.PopCount((uint)(c.Scope & candidate)) is var n &&
                            n >= c.Lower && n <= c.Upper))
                    {
                        int value = costs.Where(c => (c.Scope & candidate) == c.Scope).Sum(c => c.Weight);
                        if (local is null || value < local.Value)
                            local = new Optimum(candidate, value);
                    }
                    subset = (subset - 1) & component;
                } while (subset != component);
                cache.Add(key, local);
            }
            if (local is null) feasible = false;
            else { merged |= local.Mask; total = checked(total + local.Value); }
        }
        return new(feasible ? new(merged, total) : null, hits, count, checkedMasks);
    }
}
