using System.Diagnostics;
using System.Text.Json;
using ComponentStudy;
using AlgorithmDiscoveryLab;

if (args is ["parity-audit"]) { ParityAudit.Run(); return; }
if (args is ["normalized-quota", var splitName]) { NormalizedQuotaStudy.Run(splitName); return; }
if (args is ["cached-quota", var cacheSplit]) { CachedQuotaStudy.Run(cacheSplit); return; }
if (args is ["adaptive-decision", var decisionSplit]) { AdaptiveDecisionStudy.Run(decisionSplit); return; }
if (args is ["propagation-search", var propagationSplit]) { PropagationStudy.Run(propagationSplit); return; }
if (args is ["structural-admission", var structuralSplit]) { StructuralAdmissionStudy.Run(structuralSplit); return; }
if (args is ["compressed-propagation", var compressedSplit]) { CompressedPropagationStudy.Run(compressedSplit); return; }
if (args.Length != 3) throw new ArgumentException("usage: strategy split repetition");
string strategy=args[0], split=args[1];
int repeat=int.Parse(args[2]);
ICandidate Create() => strategy switch
{
    "full" => new FullSearch(false), "warm" => new FullSearch(true),
    "partition" => new Components(false), "reuse" => new Components(true),
    "box" => new Transfer(false), "pair" => new Transfer(true),
    "parity" => new ParityTransfer(), "parity-warm" => new ParityTransfer(true),
    "orientation" => new OrientationSearch(false), "orientation-adaptive" => new OrientationSearch(true),
    _ => throw new ArgumentOutOfRangeException(nameof(strategy))
};
var families = split switch {
    "dev" => new[]{"flat-shift","global-price","bridge","shortage"},
    "holdout" => new[]{"flat-shift","global-price","bridge","shortage","dense","relax","outage"},
    "parity-dev" or "parity-holdout" => new[]{"chain-shift","star-shift","cycle-shift","odd-cycle","mixed","relax-chain","disjoint","isolated","tighten","large-delta"},
    "orientation-dev" or "orientation-holdout" => new[]{"chain-shift","star-shift","cycle-shift","odd-cycle","mixed","relax-chain","disjoint","isolated","tighten","large-delta"},
    _ => throw new ArgumentOutOfRangeException(nameof(split))
};
using var process=Process.GetCurrentProcess();
foreach (var family in families)
for (int i=0;i<8;i++)
{
    int seed=(split switch { "dev"=>41000, "holdout"=>91000, "parity-dev"=>182000, "parity-holdout"=>283000, "orientation-dev"=>384000, _=>485000 })+i;
    var (old,current)=split.StartsWith("parity-",StringComparison.Ordinal) || split.StartsWith("orientation-",StringComparison.Ordinal) ? ParityCorpus.Generate(family,seed,split.EndsWith("-dev",StringComparison.Ordinal)?6+i%3:8+i%5) : Corpus.Generate(family,seed,split=="dev"?3+i%2:4+i%3);
    var candidate=Create();
    foreach (var (revision,model) in new[]{(0,old),(1,current)})
    {
        long cpu=process.TotalProcessorTime.Ticks, start=Stopwatch.GetTimestamp();
        long bytes=GC.GetAllocatedBytesForCurrentThread();
        var result=candidate.Solve(model);
        long solved=Stopwatch.GetTimestamp();
        int? evaluated=result.Best is {} best?Oracle.Evaluate(model,best.Mask):null;
        long end=Stopwatch.GetTimestamp();
        long allocated=GC.GetAllocatedBytesForCurrentThread()-bytes;
        long cpuTicks=process.TotalProcessorTime.Ticks-cpu;
        // Full exhaustive qualification is shared by all arms and timed separately.
        int? expected=Oracle.Minimum(model);
        long audited=Stopwatch.GetTimestamp();
        bool valid=result.Best?.Value==expected && evaluated==expected;
        process.Refresh();
        Console.WriteLine(JsonSerializer.Serialize(new { strategy,split,repeat,family,seed,revision,n=model.Size,
            value=result.Best?.Value,mask=result.Best?.Mask,expected,valid,
            result.Examined,result.Hits,result.Components,result.Transported,result.GateTicks,
            solveTicks=solved-start,verifyTicks=end-solved,auditTicks=audited-end,wallTicks=end-start,
            allocated,cpuTicks,peakProcessBytes=process.PeakWorkingSet64,frequency=Stopwatch.Frequency }));
        if (!valid) Environment.Exit(2);
    }
}
