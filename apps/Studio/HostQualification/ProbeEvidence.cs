using System.Diagnostics;
using System.Text.Json;

namespace WowDi.Studio.HostQualification;

public sealed record LocalFixture(string Revision, string Description);
public sealed record DomState(string Direction, string FocusedId, string DocumentUrl, bool HasFocus);
public sealed record ProbeEvent(long Sequence, string Name, long ElapsedTicks);
public readonly record struct Observation(long Sequence, long EventCount);
public sealed record HostEvidence(string FixtureRevision, string Os, string Runtime, bool Rendered,
    string Direction, string FocusedId, string DocumentUrl, string[] Events,
    long ObservationSequence, long EventCount, bool HasFocus, long StopwatchFrequency, ProbeEvent[] EventTrace);

public sealed class ProbeEvidence(string? evidencePath)
{
    private readonly object sync = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<ProbeEvent> events = [];
    private long issued;
    private long published;

    public void Record(string name)
    {
        lock (sync) events.Add(new(events.Count + 1L, name, clock.ElapsedTicks));
    }
    public Observation BeginObservation()
    {
        lock (sync) return new(++issued, events.Count);
    }
    // Blazor can reenter at the awaited JS call. Discard results from an
    // older render or an event epoch that changed during the DOM query.
    public bool TrySave(LocalFixture fixture, Observation observation, DomState dom)
    {
        lock (sync)
        {
            if (observation.Sequence != issued || observation.Sequence <= published ||
                observation.EventCount != events.Count) return false;
            if (evidencePath is not null)
            {
                var evidence = new HostEvidence(fixture.Revision, Environment.OSVersion.ToString(),
                    Environment.Version.ToString(), true, dom.Direction, dom.FocusedId, dom.DocumentUrl,
                    events.Select(e => e.Name).ToArray(), observation.Sequence, events.Count, dom.HasFocus,
                    Stopwatch.Frequency, events.ToArray());
                Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
                var temporary = evidencePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
                    File.Move(temporary, evidencePath, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            published = observation.Sequence;
            return true;
        }
    }
}
