using System.Text.Json;

namespace WowDi.Studio.HostQualification;

public sealed record LocalFixture(string Revision, string Description);
public sealed record DomState(string Direction, string FocusedId, string DocumentUrl);
public sealed record HostEvidence(string FixtureRevision, string Os, string Runtime, bool Rendered,
    string Direction, string FocusedId, string DocumentUrl, string[] Events);

public sealed class ProbeEvidence(string? evidencePath)
{
    private readonly List<string> events = [];
    public void Record(string name) => events.Add(name);

    public void Save(LocalFixture fixture, DomState dom)
    {
        if (evidencePath is null) return;
        var evidence = new HostEvidence(fixture.Revision, Environment.OSVersion.ToString(),
            Environment.Version.ToString(), true, dom.Direction, dom.FocusedId, dom.DocumentUrl, events.ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        var temporary = evidencePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, evidencePath, overwrite: true);
    }
}
