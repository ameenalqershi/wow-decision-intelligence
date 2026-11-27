using System.Text.Json;
using WowDi.Studio.HostQualification;
using Xunit;

public sealed class EvidenceTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "wowdi-evidence-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(folder, "host.json");
    private static readonly LocalFixture Fixture = new("fixture", "test");
    private static DomState Dom(string id) => new("ltr", id, "http://localhost/", true);
    public EvidenceTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private HostEvidence Read() => JsonSerializer.Deserialize<HostEvidence>(File.ReadAllText(FilePath))!;

    [Fact]
    public void LateInteropResponseCannotOverwriteNewerObservation()
    {
        var e = new ProbeEvidence(FilePath);
        e.Record("render");
        var old = e.BeginObservation();
        e.Record("focus:confirm");
        var current = e.BeginObservation();
        Assert.True(e.TrySave(Fixture,current,Dom("confirm")));
        Assert.False(e.TrySave(Fixture,old,Dom("direction")));
        Assert.Equal("confirm",Read().FocusedId);
        Assert.Equal(current.Sequence,Read().ObservationSequence);
    }
    [Fact]
    public void InterleavedEventRejectsMixedEpochAndRetainsLastGoodFile()
    {
        var e = new ProbeEvidence(FilePath);e.Record("render");
        var first=e.BeginObservation();Assert.True(e.TrySave(Fixture,first,Dom("direction")));
        var bytes=File.ReadAllBytes(FilePath);
        var pending=e.BeginObservation();e.Record("focus:confirm");
        Assert.False(e.TrySave(Fixture,pending,Dom("direction")));
        Assert.Equal(bytes,File.ReadAllBytes(FilePath));
        var fresh=e.BeginObservation();Assert.True(e.TrySave(Fixture,fresh,Dom("confirm")));
        Assert.Equal(2,Read().EventCount);Assert.Equal(new long[]{1,2},Read().EventTrace.Select(x=>x.Sequence));
    }
    [Fact]
    public async Task ConcurrentRecordAndPublishHaveMonotonicConsistentTrace()
    {
        var e = new ProbeEvidence(FilePath);
        await Task.WhenAll(Enumerable.Range(0,128).Select(i=>Task.Run(()=>{
            e.Record("event-"+i);
            var stamp=e.BeginObservation();e.TrySave(Fixture,stamp,Dom("direction"));
        })));
        var final=e.BeginObservation();Assert.True(e.TrySave(Fixture,final,Dom("confirm")));
        var read=Read();
        Assert.Equal(128,read.EventCount);Assert.Equal(128,read.Events.Distinct().Count());
        Assert.Equal(Enumerable.Range(1,128).Select(i=>(long)i),read.EventTrace.Select(x=>x.Sequence));
        Assert.Equal(read.Events,read.EventTrace.Select(x=>x.Name));
        Assert.True(read.EventTrace.Zip(read.EventTrace.Skip(1),(a,b)=>a.ElapsedTicks<=b.ElapsedTicks).All(x=>x));
        Assert.Single(Directory.GetFiles(folder));
    }
    [Fact]
    public void AtomicReplacementWithSharedDeleteReaderKeepsCompleteOldAndNewSnapshots()
    {
        var e=new ProbeEvidence(FilePath);e.Record("render");
        Assert.True(e.TrySave(Fixture,e.BeginObservation(),Dom("direction")));
        using var reader=new FileStream(FilePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        e.Record("focus:confirm");Assert.True(e.TrySave(Fixture,e.BeginObservation(),Dom("confirm")));
        Assert.Equal("direction",JsonSerializer.Deserialize<HostEvidence>(reader)!.FocusedId);
        Assert.Equal("confirm",Read().FocusedId);
    }
}
