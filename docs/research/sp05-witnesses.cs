#:property PublishAot=false

// .NET 10 file-based research fixture. Each assertion is a necessary negative
// control for a claim in SP05. No production solver or compiler is exercised.
var bits = new[] { 0, 1 };
var pairs = (from x in bits from y in bits select (x, y)).ToArray();

void Check(string name, bool condition)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine($"PASS {name}");
}

// Before a shared resource is introduced, minimizing -x-y chooses both.
var oldBest = pairs.MinBy(p => -p.x - p.y);
var capacityFeasible = pairs.Where(p => p.x + p.y <= 1);
Check("H1 crossing capacity invalidates the independent incumbent",
    oldBest.x + oldBest.y == 2 && capacityFeasible.Min(p => -p.x - p.y) == -1);

var restrictedMinimum = bits.Where(x => x >= 1).Min();
Check("H2 relaxation invalidates the cached lower bound",
    bits.Min() < restrictedMinimum);

// Synthetic per-stage durations, in seconds. No measured solver timings.
int[] phaseCosts = [4, 6, 1];
Check("H3 solver fits alone but charged plan exceeds its deadline",
    phaseCosts[1] <= 10 && phaseCosts.Sum() > 10);

var balanced = pairs.Where(p => p.x + p.y == 1).ToArray();
var pinned = balanced.Where(p => p.y == 0).ToArray();
Check("H4 conditional cost and infeasibility cannot certify the full model",
    pinned.Min(p => 10 * p.x + p.y) == 10 && balanced.Min(p => 10 * p.x + p.y) == 1 &&
    pinned.All(p => p.x != 0) && balanced.Any(p => p.x == 0));

var sourceCosts = bits.Select(x => (x, cost: x + x)).ToHashSet();
var droppedPenalty = bits.Select(x => (x, cost: x)).ToHashSet();
var flatSource = bits.Select(x => (x, cost: 0)).ToHashSet();
var lostChoice = flatSource.Where(p => p.x == 0).ToHashSet();
Check("H5 duplicate soft cost and missing feasible projection",
    !sourceCosts.SetEquals(droppedPenalty) && !flatSource.SetEquals(lostChoice) &&
    flatSource.Min(p => p.cost) == lostChoice.Min(p => p.cost));

var alternatives = bits.Select(x => (x, cost: x)).ToArray();
var minimizeWinner = alternatives.MinBy(p => p.cost);
var maximizeWinner = alternatives.MaxBy(p => p.cost);
Check("H5 reversing objective sense preserves values but changes the optimizer",
    minimizeWinner.x == 0 && maximizeWinner.x == 1);
