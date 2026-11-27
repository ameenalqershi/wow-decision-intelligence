using System.Text.Json;
using System.Text.RegularExpressions;

try
{
    var options = args.ToList();
    bool strict = options.Remove("--require-full");
    bool writeDrawing = options.Remove("--write-dependency-graph");
    string? live = null;
    if (options.Count == 2 && options[0] == "--github")
    {
        live = options[1];
        options.Clear();
    }
    if (options.Count != 0)
        throw new InvalidDataException("Usage: Traceability [--require-full] [--write-dependency-graph] [--github paginated-issues.json]");

    var audit = new Audit(Directory.GetCurrentDirectory());
    audit.Validate(strict, live, writeDrawing);
    return 0;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
    or InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

internal sealed partial class Audit(string root)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    };
    private static readonly HashSet<string> Coverage = ["Fully Covered", "Partially Covered", "Missing",
        "Conflicting", "Deferred with Justification", "Research Hypothesis", "Not Applicable with Justification"];

    private T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(root, path)), Json)
        ?? throw new InvalidDataException($"Empty JSON in {path}");

    internal void Validate(bool strict, string? livePath, bool writeDrawing)
    {
        var rows = Read<Work[]>("planning/backlog.json").ToDictionary(r => r.Id, StringComparer.Ordinal);
        var map = Read<Dictionary<string, int>>("planning/issue-map.json");
        var parents = Read<Dictionary<string, string>>("planning/parents.json");
        var suites = Read<Trace[]>("planning/requirements.json").ToDictionary(t => t.Issue);
        var sources = Read<Dictionary<string, string[]>>("planning/source-inventory.json");
        var declared = sources.SelectMany(s => s.Value.Select(id => (Id: id, Source: s.Key)))
            .ToDictionary(x => x.Id, x => x.Source);
        var seen = new HashSet<string>();
        Require(rows.Keys.ToHashSet().SetEquals(map.Keys) && rows.Keys.ToHashSet().SetEquals(suites.Keys),
            "Unmapped issue or issue without requirements");
        Require(map.Values.All(n => n > 0) && map.Values.Distinct().Count() == map.Count, "Invalid or repeated issue number");
        var upstream = Closure(rows.ToDictionary(r => r.Key, r => r.Value.Deps));
        Closure(rows.Keys.ToDictionary(k => k, k => parents.TryGetValue(k, out var p) ? new[] { p } : []));
        Require(parents.Keys.All(rows.ContainsKey), "Unknown child in parent map");

        foreach (var rule in Read<EdgeRule[]>("planning/engineering-dependencies.json"))
        {
            var ancestry = upstream[rule.Issue];
            foreach (var id in rule.Requires) Require(ancestry.Contains(id), $"{rule.Issue} requires {id}: {rule.Reason}");
            foreach (var id in rule.Excludes)
            {
                Require(rows.ContainsKey(id), $"Unknown exclusion {id}");
                Require(!ancestry.Contains(id), $"{rule.Issue} must be independent of {id}: {rule.Reason}");
            }
        }

        var fields = File.ReadAllText(Path.Combine(root, "docs/design/pilot-contracts.md"));
        var fieldIds = FieldPattern().Matches(fields).Select(m => m.Groups[1].Value).ToHashSet();
        var apiIds = ApiPattern().Matches(fields).Select(m => m.Groups[1].Value).ToHashSet();
        foreach (var (id, s) in suites)
        {
            Require(s.Number == map[id] && s.Requirements.Length > 0, $"Empty or mismapped requirement group {id}");
            Require(rows[id].Title.Length > 0 && rows[id].Accept.Length > 0, $"Missing issue acceptance for {id}");
            Require(new[] { s.Priority, s.Release, s.Component, s.Implementation, s.Verification, s.FieldScope, s.ApiScope }
                .All(v => !string.IsNullOrWhiteSpace(v)), $"Missing trace metadata on {id}");
            Require(s.Specifications.Length > 0 && s.Diagrams.Length > 0, $"Missing design reference for {id}");
            foreach (var file in s.Specifications.Concat(s.Diagrams).Concat(s.Evidence)) Existing(file);
            Require(s.Fields.All(fieldIds.Contains) && s.Apis.All(apiIds.Contains), $"Unknown field or API in {id}");
            var cases = s.Tests.ToDictionary(t => t.Id);
            Require(cases.Count > 0 && cases.Values.All(t => !string.IsNullOrWhiteSpace(t.Oracle)), $"Missing test oracle in {id}");
            foreach (var req in s.Requirements)
            {
                Require(seen.Add(req.Id), $"Repeated requirement {req.Id}");
                Require(declared.TryGetValue(req.Id, out var source) && source == req.Source, $"Unknown source requirement {req.Id}");
                Require(req.Title.Length > 0 && req.Description.Length > 0 && req.Acceptance.Length > 0, $"Incomplete requirement {req.Id}");
                Require(req.Classification is "Functional" or "Non-Functional", $"Unknown classification {req.Id}");
                Require(req.Tests.Length > 0 && req.Tests.All(cases.ContainsKey), $"Missing test mapping for {req.Id}");
                Require(Coverage.Contains(req.Coverage), $"Unknown coverage for {req.Id}");
                if (req.Coverage != "Fully Covered")
                    Require(!string.IsNullOrWhiteSpace(req.Reason), $"Missing coverage justification on {req.Id}");
                if (strict) Require(req.Coverage is "Fully Covered" or "Not Applicable with Justification",
                    $"Outstanding coverage: {req.Id} ({req.Coverage})");
            }
        }
        Require(seen.SetEquals(declared.Keys), "Source inventory contains an untraced requirement");
        if (writeDrawing)
            File.WriteAllText(Path.Combine(root, "docs/audit/diagrams/dependencies.dot"), DependencyDrawing.Render(rows.Values, map));
        if (livePath != null) CheckGitHub(livePath, rows, map, parents);
        Console.WriteLine($"{rows.Count} issues, {seen.Count} requirements, {rows.Values.Sum(r => r.Deps.Length)} direct dependencies, {parents.Count} sub-issue relationships");
        foreach (var g in suites.Values.SelectMany(x => x.Requirements).GroupBy(r => r.Coverage).OrderBy(g => g.Key))
            Console.WriteLine($"{g.Key}: {g.Count()}");
    }

    private static Dictionary<string, HashSet<string>> Closure(Dictionary<string, string[]> edges)
    {
        var cache = new Dictionary<string, HashSet<string>>();
        var visiting = new HashSet<string>();
        HashSet<string> Visit(string id)
        {
            if (cache.TryGetValue(id, out var ready)) return ready;
            Require(visiting.Add(id), $"Dependency cycle at {id}");
            Require(edges.TryGetValue(id, out _), $"Unknown dependency {id}");
            var direct = edges[id];
            Require(direct.Distinct().Count() == direct.Length, $"Repeated dependency on {id}");
            var result = new HashSet<string>(direct);
            foreach (var dependency in direct) result.UnionWith(Visit(dependency));
            visiting.Remove(id);
            return cache[id] = result;
        }
        foreach (var key in edges.Keys) Visit(key);
        return cache;
    }

    private void CheckGitHub(string path, Dictionary<string, Work> rows, Dictionary<string, int> map, Dictionary<string, string> parents)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var issues = doc.RootElement.EnumerateArray().SelectMany(page => page.EnumerateArray())
            .Where(i => !i.TryGetProperty("pull_request", out _)).ToDictionary(i => i.GetProperty("number").GetInt32());
        Require(issues.Keys.ToHashSet().SetEquals(map.Values), "GitHub issues differ from the checked-in ledger");
        foreach (var (id, row) in rows)
        {
            var issue = issues[map[id]];
            Require(issue.GetProperty("title").GetString() == $"{id} {row.Title}", $"GitHub title drift #{map[id]}");
            var parentUrl = issue.TryGetProperty("parent_issue_url", out var parent) ? parent.GetString() : null;
            var expectedParent = parents.TryGetValue(id, out var parentId) ? issues[map[parentId]].GetProperty("url").GetString() : null;
            Require(parentUrl == expectedParent, $"GitHub sub-issue parent drift #{map[id]}");
            Require(issue.GetProperty("milestone").GetProperty("title").GetString() == row.Area, $"GitHub milestone drift #{map[id]}");
            var body = issue.GetProperty("body").GetString() ?? "";
            Require(body.Contains(row.Accept, StringComparison.Ordinal), $"GitHub acceptance drift #{map[id]}");
            var section = DependencySection().Match(body);
            Require(section.Success, $"Missing dependency section #{map[id]}");
            var actual = IssueRef().Matches(section.Groups[1].Value).Select(m => (int.Parse(m.Groups[1].Value), m.Groups[2].Value)).ToArray();
            var expected = row.Deps.Select(d => (map[d], d)).ToHashSet();
            Require(actual.Length == expected.Count && expected.SetEquals(actual), $"GitHub dependency drift #{map[id]}");
        }
    }

    private void Existing(string reference)
    {
        var absolute = Path.GetFullPath(reference, root);
        var rel = Path.GetRelativePath(root, absolute);
        Require(!Path.IsPathRooted(reference) && rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar)
            && File.Exists(absolute), $"Missing or invalid reference {reference}");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidDataException(message);
    }

    [GeneratedRegex(@"(?m)^\| (F-[A-Z]+) /")] private static partial Regex FieldPattern();
    [GeneratedRegex(@"(?m)^\| (API-[A-Z]+) \|")] private static partial Regex ApiPattern();
    [GeneratedRegex(@"(?ms)^## Dependencies\s*\n(.*?)(?=^## |\z)")] private static partial Regex DependencySection();
    [GeneratedRegex(@"#(\d+) \(([A-Z]+\d+)\)")] private static partial Regex IssueRef();
}

internal sealed record Work(string Id, string Area, string Title, string[] Deps, string Accept);
internal sealed record EdgeRule(string Issue, string[] Requires, string[] Excludes, string Reason);
internal sealed record Trace(string Issue, int Number, string Priority, string Release, string Component, string Implementation,
    string Verification, string FieldScope, string ApiScope, string[] Specifications, string[] Diagrams, string[] Fields,
    string[] Apis, string[] Evidence, TestCase[] Tests, Requirement[] Requirements);
internal sealed record TestCase(string Id, string Oracle);
internal sealed record Requirement(string Id, string Title, string Description, string Source, string Classification,
    string Acceptance, string[] Tests, string Coverage, string Reason);
