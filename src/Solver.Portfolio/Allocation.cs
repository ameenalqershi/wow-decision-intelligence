using WowDi.Solver.Planning;

namespace WowDi.Solver.Portfolio;

/// <summary>One admitted member slot: native threads, memory and the hard/soft time limits.</summary>
public sealed record AllocationSlot(int Rank,int Threads,long MemoryBytes,TimeSpan HardDeadline,TimeSpan SoftLimit);

/// <summary>Pure, deterministic resource allocation. No timing, no I/O; identical inputs give identical slots.</summary>
public static class Allocation
{
    /// <summary>Thread ceiling for a member after the size rule. Never below its qualified minimum.</summary>
    public static int ThreadCeiling(AdapterProfile p,ModelFeatures features,PortfolioPolicy policy)
    {
        var max=p.Manifest.MaxThreads;
        if(policy.SmallModelVariables>0&&features.Variables<=policy.SmallModelVariables)max=1;
        return Math.Max(p.MinimumThreads,max);
    }

    public static TimeSpan SoftLimit(TimeSpan hard,PortfolioPolicy policy)
    {
        var soft=Math.Min(hard.TotalMilliseconds*policy.SoftLimitFraction,hard.TotalMilliseconds-policy.Grace.TotalMilliseconds);
        if(soft<=0)soft=hard.TotalMilliseconds/2;
        return TimeSpan.FromMilliseconds(Math.Floor(soft));
    }

    /// <summary>Race: admit members in rank order while their minimum threads and memory fit, give leftover threads
    /// round-robin from the best-ranked member up to each ceiling, split leftover memory evenly, and use one common
    /// deadline d = min(wall, budget / Σthreads). A member whose qualified minimum deadline exceeds d is dropped
    /// (lowest rank first) and the allocation is recomputed.</summary>
    public static IReadOnlyList<AllocationSlot> Race(IReadOnlyList<AdapterProfile> ranked,ModelFeatures features,
        PortfolioBudget budget,PortfolioPolicy policy,long availableCoreMs,int freeThreads,long freeMemory)
    {
        var count=0;long threadSum=0,memSum=0;
        while(count<ranked.Count&&threadSum+ranked[count].MinimumThreads<=freeThreads&&
              memSum+ranked[count].MinimumMemoryBytes<=freeMemory)
        {threadSum+=ranked[count].MinimumThreads;memSum+=ranked[count].MinimumMemoryBytes;count++;}
        for(;count>0;count--)
        {
            var admitted=ranked.Take(count).ToArray();
            var threads=admitted.Select(p=>p.MinimumThreads).ToArray();
            var ceilings=admitted.Select(p=>ThreadCeiling(p,features,policy)).ToArray();
            var left=freeThreads-threads.Sum();
            for(bool progress=true;left>0&&progress;)
            {
                progress=false;
                for(int i=0;i<threads.Length&&left>0;i++)
                    if(threads[i]<ceilings[i]){threads[i]++;left--;progress=true;}
            }
            var minMem=admitted.Sum(p=>p.MinimumMemoryBytes);
            var extra=(freeMemory-minMem)/admitted.Length;
            var deadlineMs=Math.Min((long)Math.Floor(budget.WallDeadline.TotalMilliseconds),availableCoreMs/threads.Sum());
            var hard=TimeSpan.FromMilliseconds(deadlineMs);
            if(deadlineMs<1||admitted.Any(p=>p.MinimumDeadline>hard))continue;
            var soft=SoftLimit(hard,policy);
            return admitted.Select((p,i)=>new AllocationSlot(i,threads[i],p.MinimumMemoryBytes+extra,hard,soft)).ToArray();
        }
        return [];
    }

    /// <summary>Sequential stage for member <paramref name="rank"/>: all free threads up to its ceiling, all free memory, and an
    /// equal share of the remaining core budget and wall time among the members still to run (unspent budget rolls over).</summary>
    public static AllocationSlot? Stage(AdapterProfile p,int rank,int remainingMembers,ModelFeatures features,PortfolioPolicy policy,
        long availableCoreMs,int freeThreads,long freeMemory,TimeSpan wallLeft)
    {
        var threads=Math.Min(freeThreads,ThreadCeiling(p,features,policy));
        if(threads<p.MinimumThreads||freeMemory<p.MinimumMemoryBytes||remainingMembers<1)return null;
        var shareMs=Math.Min(availableCoreMs/threads/remainingMembers,(long)Math.Floor(wallLeft.TotalMilliseconds/remainingMembers));
        var hard=TimeSpan.FromMilliseconds(shareMs);
        if(shareMs<1||p.MinimumDeadline>hard)return null;
        return new(rank,threads,freeMemory,hard,SoftLimit(hard,policy));
    }
}
