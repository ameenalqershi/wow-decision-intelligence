using System.Text.RegularExpressions;

internal sealed partial class Audit
{
    private void CheckStudioGates(Dictionary<string, Work> rows, Dictionary<string,int> map,
        Dictionary<string,Trace> suites, Dictionary<string,HashSet<string>> upstream, HashSet<string> apiIds)
    {
        var gates = Read<StudioGate[]>("planning/studio-integration-gates.json");
        Require(gates.Length == 24 && gates.Select(g => (g.Consumer,g.Provider)).Distinct().Count() == 24,
            "Studio gate inventory must contain 24 unique relationships");
        foreach (var g in gates)
        {
            Require(rows.ContainsKey(g.Consumer) && rows.ContainsKey(g.Provider) &&
                map[g.Consumer] == g.ConsumerIssue && map[g.Provider] == g.ProviderIssue, "Studio gate identity mismatch");
            Require(!string.IsNullOrWhiteSpace(g.Kind) && !string.IsNullOrWhiteSpace(g.Reason), "Studio gate missing rationale");
            Require(g.Consumer != g.Provider && !upstream[g.Provider].Contains(g.Consumer), "Studio gate cycle");
            var direct = rows[g.Consumer].Deps.Contains(g.Provider);
            var inherited = upstream[g.Consumer].Contains(g.Provider);
            Require(g.Disposition switch
            {
                "hard" => direct,
                "transitive" => !direct && inherited,
                "capability" => !inherited,
                _ => false
            }, "Studio gate disposition mismatch");
        }
        // Check conditional relationships collectively without turning them into claim prerequisites.
        var augmented = rows.ToDictionary(p => p.Key, p => p.Value.Deps.Concat(gates.Where(g => g.Consumer == p.Key).Select(g => g.Provider)).Distinct(StringComparer.Ordinal).ToArray());
        _ = Closure(augmented);
        var ports = Read<SpecializedPort[]>("planning/specialized-ports.json");
        var expected = new Dictionary<string,string>
        {
            ["API-SIMULATION"]="UI14", ["API-ALLOCATION"]="UI16", ["API-EXPLAIN"]="UI18",
            ["API-BENCHMARK"]="UI19", ["API-MAPPING"]="UI20", ["API-ADMIN"]="UI21", ["API-SETTINGS"]="UI23"
        };
        Require(ports.Length == expected.Count && ports.Select(p => p.Port).ToHashSet().SetEquals(expected.Keys), "Specialized port inventory mismatch");
        foreach (var p in ports)
        {
            Require(apiIds.Contains(p.Port) && p.Consumer == expected[p.Port] &&
                p.Approval == "logical-design-baseline" && p.ProviderAcceptance == "required",
                "Specialized port scope/approval mismatch");
            Require(p.Owners.Length > 0 && p.Owners.Distinct().Count() == p.Owners.Length &&
                p.Owners.All(rows.ContainsKey), "Specialized port owner mismatch");
            Existing(p.Specification);
            foreach (var key in p.Owners.Append(p.Consumer))
                Require(suites[key].Apis.Contains(p.Port) && suites[key].Specifications.Contains(p.Specification),
                    "Specialized port missing trace");
            var spec = File.ReadAllText(Path.Combine(root,p.Specification));
            Require(spec.Contains($"| {p.Port} / {p.Consumer} |", StringComparison.Ordinal), "Specialized port missing design row");
        }
        Console.WriteLine($"Studio review: {gates.Length} relationships ({gates.Count(g=>g.Disposition=="hard")} hard, {gates.Count(g=>g.Disposition=="transitive")} transitive, {gates.Count(g=>g.Disposition=="capability")} capability); {ports.Length} specialized logical ports");
    }
}
internal sealed record StudioGate(string Consumer,string Provider,int ConsumerIssue,int ProviderIssue,string Kind,string Disposition,string Reason);
internal sealed record SpecializedPort(string Port,string Consumer,string[] Owners,string Specification,string Approval,string ProviderAcceptance);
