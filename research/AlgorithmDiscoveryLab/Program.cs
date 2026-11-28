using System.Diagnostics;
using System.Text.Json;
using ComponentStudy;
using AlgorithmDiscoveryLab;

if (args.Length != 3) throw new ArgumentException("usage: strategy split repetition");
string strategy=args[0], split=args[1];
int repeat=int.Parse(args[2]);
ICandidate Create() => strategy switch
{
    "full" => new FullSearch(false), "warm" => new FullSearch(true),
    "partition" => new Components(false), "reuse" => new Components(true),
    "box" => new Transfer(false), "pair" => new Transfer(true),
    _ => throw new ArgumentOutOfRangeException(nameof(strategy))
};
var families = split switch {
    "dev" => new[]{"flat-shift","global-price","bridge","shortage"},
    "holdout" => new[]{"flat-shift","global-price","bridge","shortage","dense","relax","outage"},
    _ => throw new ArgumentOutOfRangeException(nameof(split))
};
using var process=Process.GetCurrentProcess();
foreach (var family in families)
for (int i=0;i<8;i++)
{
    int seed=(split=="dev"?41000:91000)+i;
    var (old,current)=Corpus.Generate(family,seed,split=="dev"?3+i%2:4+i%3);
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
