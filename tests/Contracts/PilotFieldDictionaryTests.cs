using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace WowDi.Contracts.Tests;

public sealed class PilotFieldDictionaryTests
{
    // Synthetic frozen context: revision r17, machine M1, a 12-minute horizon,
    // release at 2 and duration 3. Another operation occupies [6,8).
    // This family exists only inside the tests and is never a public pilot DTO.
    private const string Family = "wowdi.example.sp01";
    private const long Horizon = 12;
    private const long Release = 2;
    private const long Duration = 3;
    private const string Bundle = "SP01 synthetic evidence";
    private static string Digest => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Bundle)));

    public static TheoryData<string, string, string, string> Fields => new()
    {
        { "F-REV", "revisionId", "\"r17\"", "\"latest\"" },
        { "F-MACHINE", "machineId", "\"M1\"", "\"m1\"" },
        { "F-OP", "operationId", "\"A\"", "\"B\"" }, // B is already registered.
        { "F-DURATION", "durationMinutes", "3", "0" },
        { "F-RELEASE", "releaseMinute", "0", "13" },
        { "F-DUE", "dueMinute", "18", "-1" },
        { "F-WEIGHT", "tardinessWeight", "2.25", "-0.5" },
        { "F-START", "startMinute", "3", "4" }, // [3,6) touches; [4,7) overlaps.
        { "F-RUN", "runId", "\"run-7\"", "\"foreign-run\"" },
        { "F-STATUS", "mathematicalStatus", "\"Feasible\"", "\"Optimal\"" },
        { "F-TERM", "termination", "\"Timeout\"", "\"Infeasible\"" },
        { "F-OBJ", "objectiveValue", "12.125", "\"NaN\"" },
        { "F-BOUND", "bestBound", "10", "13" }, // The minimizing incumbent costs 12.
        { "F-EVID", "evidenceDigest", JsonSerializer.Serialize(Digest), JsonSerializer.Serialize(new string('0', 64)) },
        { "F-ELAPSED", "elapsedMs", "56", "-1" },
        { "F-CPU", "cpuMs", "31", "0.5" },
        { "F-MEM", "peakBytes", "8192", "-1" }
    };

    private static JsonElement ReadField(string key, string rawValue)
    {
        var json = $$$"""{"contract":"{{{Family}}}","version":"1.0","payload":{"{{{key}}}":{{{rawValue}}}}}""";
        return EnvelopeReader.Read(Encoding.UTF8.GetBytes(json), Family, 1).Payload.GetProperty(key);
    }

    private static bool KnownText(JsonElement value, params string[] allowed) =>
        value.ValueKind == JsonValueKind.String && allowed.Contains(value.GetString(), StringComparer.Ordinal);

    // An oracle for this fixture's facts. No production lookup, solver or proof is simulated.
    private static bool Admitted(string id, JsonElement value)
    {
        bool unavailable = value.ValueKind == JsonValueKind.Null;
        bool numeric = value.ValueKind == JsonValueKind.Number;
        long integer = 0;
        decimal exact = 0;
        bool whole = numeric && value.TryGetInt64(out integer);
        bool amount = numeric && value.TryGetDecimal(out exact);
        return id switch
        {
            "F-REV" => KnownText(value, "r17"),
            "F-MACHINE" => KnownText(value, "M1"),
            "F-OP" => value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()) && !KnownText(value, "B"),
            "F-DURATION" => whole && integer > 0 && integer <= Horizon,
            "F-RELEASE" => whole && integer >= 0 && integer <= Horizon,
            "F-DUE" => whole && integer >= 0,
            "F-WEIGHT" => amount && exact >= 0,
            "F-START" => whole && integer >= Release && integer <= Horizon - Duration &&
                (integer + Duration <= 6 || integer >= 8),
            "F-RUN" => KnownText(value, "run-7"),
            "F-STATUS" => KnownText(value, "Feasible", "Unknown"), // Feasible witness, no optimality proof.
            "F-TERM" => KnownText(value, "Completed", "Timeout", "Cancelled", "Failed"),
            "F-OBJ" => unavailable || amount,
            "F-BOUND" => unavailable || (amount && exact <= 12), // Compatible scope and evidence assumed.
            "F-EVID" => KnownText(value, Digest),
            "F-ELAPSED" or "F-CPU" or "F-MEM" => unavailable || (whole && integer >= 0),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unmapped pilot field")
        };
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public void AdmitsAndRejectsEachDocumentedRepresentation(string id, string key, string valid, string invalid)
    {
        var value = ReadField(key, valid);
        Assert.True(Admitted(id, value), id);
        Assert.False(Admitted(id, ReadField(key, invalid)), id);
        Assert.False(Admitted(id, ReadField(key, "true")), id);
        bool nullable = id is "F-OBJ" or "F-BOUND" or "F-ELAPSED" or "F-CPU" or "F-MEM";
        Assert.Equal(nullable, Admitted(id, ReadField(key, "null")));
        // A structured client relaying the canonical value must retain its wire semantics.
        Assert.True(JsonElement.DeepEquals(value, ReadField(key, JsonSerializer.Serialize(value))));
    }

    [Theory]
    [InlineData("F-OBJ", "objectiveValue")]
    [InlineData("F-BOUND", "bestBound")]
    [InlineData("F-ELAPSED", "elapsedMs")]
    [InlineData("F-CPU", "cpuMs")]
    [InlineData("F-MEM", "peakBytes")]
    public void MissingObservationAndMeasuredZeroRemainDistinct(string id, string key)
    {
        var absent = ReadField(key, "null");
        var zero = ReadField(key, "0");
        Assert.True(Admitted(id, absent));
        Assert.True(Admitted(id, zero));
        Assert.Equal("null", JsonSerializer.Serialize(absent));
        Assert.Equal("0", JsonSerializer.Serialize(zero));
    }

    [Theory]
    [InlineData("elapsedMs", 56, 1000, "0.056")]
    [InlineData("cpuMs", 31, 1000, "0.031")]
    [InlineData("peakBytes", 8192, 1024, "8")]
    [InlineData("peakBytes", 8193, 1024, "8.0009765625")]
    public void ExactDisplayUnitConversionRecoversOriginal(string key, long raw, int unitsPerDisplayUnit, string expected)
    {
        decimal original = ReadField(key, JsonSerializer.Serialize(raw)).GetInt64();
        decimal display = original / unitsPerDisplayUnit;
        Assert.Equal(expected, display.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(raw, checked((long)(display * unitsPerDisplayUnit)));
    }

    [Fact]
    public void GanttEndIsExclusiveAndReconstructsTheInterval()
    {
        var start = ReadField("startMinute", "3");
        long duration = ReadField("durationMinutes", "3").GetInt64();
        long end = checked(start.GetInt64() + duration);
        Assert.Equal(6, end);
        Assert.True(Admitted("F-START", start));
        Assert.Equal(duration, end - start.GetInt64());
        Assert.False(Admitted("F-START", ReadField("startMinute", "4")));
        Assert.False(Admitted("F-START", ReadField("startMinute", JsonSerializer.Serialize(long.MaxValue))));
    }

    [Fact]
    public void BothDictionaryTablesAndCasesCoverTheExactPilotInventory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "docs/design/pilot-contracts.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string design = Path.Combine(directory.FullName, "docs/design");
        string source = File.ReadAllText(Path.Combine(design, "pilot-contracts.md"));
        string dictionary = File.ReadAllText(Path.Combine(design, "pilot-field-dictionary-v1.md"));

        static string[] Inventory(string markdown) =>
            Regex.Matches(markdown, @"(?m)^\| (F-[A-Z]+) / (\w+) \|")
                .Select(m => m.Groups[1].Value + "/" + m.Groups[2].Value).Order(StringComparer.Ordinal).ToArray();

        var expected = Inventory(source);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, Inventory(dictionary));
        Assert.Equal(expected, Fields.Select(row => row[0] + "/" + row[1]).Order(StringComparer.Ordinal));
        var layers = Regex.Matches(dictionary, @"(?m)^\| (F-[A-Z]+) \| ([^|]+) \| ([^|]+) \| ([^|]+) \| ([^|]+) \|");
        Assert.Equal(expected.Select(pair => pair.Split('/')[0]), layers.Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal));
        var ports = Regex.Matches(source, @"(?m)^\| (API-[A-Z]+) \|").Select(m => m.Groups[1].Value).ToHashSet();
        foreach (Match layer in layers)
        {
            foreach (string port in layer.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries))
                Assert.Contains(port, ports);
            Assert.All(layer.Groups.Cast<Group>().Skip(3), column => Assert.False(string.IsNullOrWhiteSpace(column.Value)));
        }
    }
}
