namespace ComponentStudy;

// Binary variables identified by their bit positions. Every constraint has the form
// lower <= selected_count(scope) <= upper. Each cost activates when all its bits are set.
internal sealed record Capacity(int Scope, int Lower, int Upper);
internal sealed record Cost(int Scope, int Weight);
internal sealed record Model(int Size, Capacity[] Capacities, Cost[] Costs);
internal sealed record Optimum(int Mask, int Value);
internal sealed record StudyResult(Optimum? Best, int Reused, int Components, int Assignments);
