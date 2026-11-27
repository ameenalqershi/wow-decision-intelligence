using Highs;

const double tolerance = 1e-7;
var model = new HighsModel([1.0,3.0], [0,0], [2,2], [1.5], [4.0],
    [0,2], [0,1], [1.0,1.0], null, 0, HighsMatrixFormat.kRowwise, HighsObjectiveSense.kMinimize);
using var solver = new HighsLpSolver();
if (solver.passLp(model) != HighsStatus.kOk || solver.run() != HighsStatus.kOk ||
    solver.GetModelStatus() != HighsModelStatus.kOptimal)
    throw new InvalidOperationException("Coverage LP did not return an optimum.");
double[] x = solver.getSolution().colvalue;
double cost = x[0] + 3 * x[1];
if (x.Length != 2 || x.Any(v => !double.IsFinite(v) || v < -tolerance || v > 2+tolerance) ||
    x.Sum() < 1.5-tolerance || Math.Abs(cost-1.5) > tolerance)
    throw new InvalidOperationException("Independent check rejected the allocation.");
// For all nonnegative x, cost >= x[0]+x[1] >= 1.5. Feasible equality proves optimality here.
Console.WriteLine(FormattableString.Invariant($"Coverage LP verified: x0={x[0]}, x1={x[1]}, objective={cost}, analytic lower bound=1.5."));
