using ComponentStudy;
using System.Diagnostics;
using System.Numerics;

namespace AlgorithmDiscoveryLab;

internal record Answer(Optimum? Best, int Examined, int Components, int Hits, bool Transported, long GateTicks);

internal interface ICandidate
{
    Answer Solve(Model model);
}

internal sealed class Components(bool reuse) : ICandidate
{
    private readonly ExactStudy cache = new();
    public Answer Solve(Model model)
    {
        var run = (reuse ? cache : new ExactStudy()).Solve(model);
        return new(run.Best, run.Assignments, run.Components, run.Reused, false, 0);
    }
}

// Target grammar: exactly one unary integer price per option, no other costs.
internal sealed class FullSearch(bool hint) : ICandidate
{
    private Optimum? previous;
    public Answer Solve(Model model)
    {
        var cost = Corpus.Prices(model);
        int[] lower = new int[cost.Length + 1];
        for (int i = cost.Length - 1; i >= 0; i--)
            lower[i] = lower[i+1] + Math.Min(0,cost[i]);
        Optimum? best = null;
        if (hint && previous is {} old && Oracle.Evaluate(model,old.Mask) is int initial)
            best = new(old.Mask,initial);
        int examined = 0;
        void Visit(int i, int mask, int sum)
        {
            if (best is {} incumbent && sum + lower[i] >= incumbent.Value) return;
            if (i == cost.Length)
            {
                examined++;
                foreach (var row in model.Capacities)
                {
                    int count = BitOperations.PopCount((uint)(mask & row.Scope));
                    if (count < row.Lower || count > row.Upper) return;
                }
                best = new(mask,sum);
                return;
            }
            Visit(i+1,mask,sum);
            Visit(i+1,mask | (1<<i),sum+cost[i]);
        }
        Visit(0,0,0);
        previous = best;
        return new(best,examined,1,0,false,0);
    }
}

internal sealed class Transfer(bool pairs) : ICandidate
{
    private readonly Components fallback = new(true);
    private Model? previous;
    private Optimum? optimum;
    public Answer Solve(Model model)
    {
        long start = Stopwatch.GetTimestamp();
        Optimum? accepted = null;
        if (previous is {} old && optimum is {} witness && model.Size == old.Size &&
            old.Capacities.All(model.Capacities.Contains) && Oracle.Evaluate(model,witness.Mask) is int feasibleCost)
        {
            int[] a = Corpus.Prices(old), b = Corpus.Prices(model);
            var delta = b.Zip(a,(x,y)=>x-y).ToArray();
            int floor = witness.Value;
            int covered = 0;
            foreach (var row in model.Capacities)
            {
                if (!pairs || row.Lower != 1 || row.Upper != 1 ||
                    BitOperations.PopCount((uint)row.Scope) != 2 || (row.Scope & covered) != 0) continue;
                int i = BitOperations.TrailingZeroCount((uint)row.Scope);
                int j = BitOperations.Log2((uint)row.Scope);
                floor += Math.Min(delta[i],delta[j]);
                covered |= row.Scope;
            }
            for (int i = 0; i < delta.Length; i++)
                if ((covered & (1<<i)) == 0) floor += Math.Min(0,delta[i]);
            if (floor == feasibleCost) accepted = new(witness.Mask,feasibleCost);
        }
        long gate = Stopwatch.GetTimestamp()-start;
        var result = accepted is {} hit ? new Answer(hit,0,0,0,true,gate) :
            fallback.Solve(model) with { GateTicks = gate };
        previous = model;
        optimum = result.Best;
        return result;
    }
}
