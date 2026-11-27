namespace ComponentStudy;

internal static class Oracle
{
    // Deliberately evaluate the original model one variable at a time. There is
    // no shared presolver, component graph, cost evaluator or cache with ExactStudy.
    public static int? Evaluate(Model m, int mask)
    {
        if (mask < 0 || mask >= 1 << m.Size) return null;
        foreach (var c in m.Capacities)
        {
            int selected = 0;
            for (int i = 0; i < m.Size; i++)
                if ((c.Scope & (1 << i)) != 0 && (mask & (1 << i)) != 0) selected++;
            if (selected < c.Lower || selected > c.Upper) return null;
        }
        int value = 0;
        foreach (var term in m.Costs)
        {
            bool active = true;
            for (int i = 0; i < m.Size; i++)
                if ((term.Scope & (1 << i)) != 0 && (mask & (1 << i)) == 0) active = false;
            if (active) value = checked(value + term.Weight);
        }
        return value;
    }

    public static int? Minimum(Model m)
    {
        int? best = null;
        for (int mask = 0; mask < 1 << m.Size; mask++)
            if (Evaluate(m, mask) is int value && (!best.HasValue || value < best)) best = value;
        return best;
    }
}
