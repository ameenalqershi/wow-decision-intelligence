using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using WowDi.Contracts;
using WowDi.Udir;
using Xunit;

namespace WowDi.Udir.Tests;

public sealed class UdirTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    private static JsonObject Template() => JsonNode.Parse(File.ReadAllText(Fixture("factory.json")))!.AsObject();
    private static UdirDocument Read(JsonNode node) => UdirDocument.Read(Encoding.UTF8.GetBytes(node.ToJsonString()));
    private static JsonArray Items(JsonObject payload, string key) => payload[key]!.AsArray();

    [Fact]
    public void FactoryRoundTripPreservesFieldsExtensionsAndFrozenSource()
    {
        var input = Template();
        var document = Read(input);
        var bytes = document.Write();
        Assert.True(JsonNode.DeepEquals(input, JsonNode.Parse(bytes)));
        Assert.Equal(bytes, UdirDocument.Read(bytes).Write());
        var m = document.Model;
        var source = File.ReadAllBytes(Fixture("factory.txt"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(source)), Assert.Single(m.Sources).Sha256);
        Assert.Equal(Encoding.UTF8.GetString(source).Length, m.Sources[0].Utf16Length);
        var span = m.Scenarios.Single(s => s.Id == "outage").Span;
        Assert.Equal("outage", Encoding.UTF8.GetString(source).Substring(span.Start, span.Length));
        Assert.Equal("7/3", m.Expressions.Single(e => e.Id == "price").Literal);
        Assert.Equal("3", Assert.Single(m.Scenarios.Single(s => s.Id == "outage").Overrides).Value);
        Assert.Equal("USD", m.Types.Single(t => t.Id == "usd").Currency);
        Assert.Equal("integer.linear", m.Requires[0]);
        Assert.Equal(document.Model, UdirDocument.Create(document.Model).Model);
    }

    [Theory]
    [InlineData("new-major", "contract.major")]
    [InlineData("unknown-profile", "udir.semantics")]
    [InlineData("bad-digest", "udir.digest")]
    [InlineData("duplicate-symbol", "udir.id")]
    [InlineData("wrong-span", "udir.span")]
    [InlineData("bad-origin", "udir.time")]
    [InlineData("missing-currency", "udir.currency")]
    [InlineData("dangling", "udir.reference")]
    [InlineData("wrong-type", "udir.type")]
    [InlineData("cycle", "udir.cycle")]
    [InlineData("wrong-arity", "udir.arity")]
    [InlineData("fraction-integer", "udir.domain")]
    [InlineData("reversed-domain", "udir.domain")]
    [InlineData("bad-predicate", "udir.predicate")]
    [InlineData("hard-penalty", "udir.penalty")]
    [InlineData("missing-soft-penalty", "udir.penalty")]
    [InlineData("duplicate-priority", "udir.objective")]
    [InlineData("stage", "udir.scenario")]
    [InlineData("bad-probability", "udir.probability")]
    [InlineData("override-decision", "udir.override")]
    [InlineData("override-outside-domain", "udir.override")]
    [InlineData("unknown-field", "udir.payload")]
    [InlineData("numeric-enum", "udir.payload")]
    [InlineData("missing-array", "udir.payload")]
    public void RejectsMalformedOrSemanticallyIncompatibleRecords(string mutation, string code)
    {
        var input = Template();
        var p = input["payload"]!.AsObject();
        var symbols = Items(p, "symbols");
        var expr = Items(p, "expressions");
        var scenarios = Items(p, "scenarios");
        switch (mutation)
        {
            case "new-major": input["version"] = "2.0"; break;
            case "unknown-profile": p["semanticsVersion"] = "udir/17"; break;
            case "bad-digest": p["inputDigests"]![0] = "0"; break;
            case "duplicate-symbol": symbols.Add(symbols[0]!.DeepClone()); break;
            case "wrong-span": symbols[0]!["span"]!["start"] = int.MaxValue; break;
            case "bad-origin": p["timeBasis"]!["origin"] = "2026-01-01T00:00:00"; break;
            case "missing-currency": Items(p, "types")[2]!.AsObject().Remove("currency"); break;
            case "dangling": expr[2]!["arguments"]![1] = "missing"; break;
            case "wrong-type": expr[2]!["typeId"] = "minutes"; break;
            case "cycle": expr[4]!["arguments"]![1] = "return"; break;
            case "wrong-arity": expr[4]!["arguments"]!.AsArray().RemoveAt(0); break;
            case "fraction-integer": symbols[0]!["lower"] = "1/2"; break;
            case "reversed-domain": symbols[0]!["lower"] = "13"; break;
            case "bad-predicate": p["constraints"]![0]!["predicateId"] = "q"; break;
            case "hard-penalty": p["constraints"]![0]!["penalty"] = "1"; break;
            case "missing-soft-penalty": p["constraints"]![0]!["hard"] = false; break;
            case "duplicate-priority":
                var clone = p["objectives"]![0]!.DeepClone();
                clone["id"] = "another";
                Items(p, "objectives").Add(clone);
                break;
            case "stage": scenarios[1]!["stage"] = 0; break;
            case "bad-probability": scenarios[1]!["probability"] = "5/4"; break;
            case "override-decision": scenarios[1]!["overrides"]![0]!["symbolId"] = "x"; break;
            case "override-outside-domain": scenarios[1]!["overrides"]![0]!["value"] = "9"; break;
            case "unknown-field": p["newMeaning"] = true; break;
            case "numeric-enum": symbols[0]!["role"] = 1; break;
            case "missing-array": p.Remove("types"); break;
            default: throw new ArgumentException(mutation);
        }
        Assert.Equal(code, Assert.Throws<ContractException>(() => Read(input)).Code);
    }

    [Theory]
    [InlineData("variable")] [InlineData("Variable, Parameter")] [InlineData("1")]
    public void RequiresExactWireEnumTokens(string token)
    {
        var input = Template();
        input["payload"]!["symbols"]![0]!["role"] = token;
        Assert.Equal("udir.payload", Assert.Throws<ContractException>(() => Read(input)).Code);
    }

    [Fact]
    public void DeterministicDomainSoftConstraintAndLexicographicObjectivesRoundTrip()
    {
        var input = Template();
        var p = input["payload"]!.AsObject();
        p["scenarios"] = new JsonArray();
        var soft = p["constraints"]![0]!.DeepClone();
        soft["id"] = "preference";
        soft["hard"] = false;
        soft["penalty"] = "2/3";
        Items(p, "constraints").Add(soft);
        var secondary = p["objectives"]![0]!.DeepClone();
        secondary["id"] = "resourceUse";
        secondary["expressionId"] = "q";
        secondary["direction"] = "Minimize";
        secondary["priority"] = 2;
        secondary["weight"] = "1/2";
        secondary["tolerance"] = "1/10";
        Items(p, "objectives").Add(secondary);
        Assert.True(JsonNode.DeepEquals(input, JsonNode.Parse(Read(input).Write())));
    }

    [Fact]
    public void SharedSubexpressionsAndForwardReferencesAreLegal()
    {
        var input = Template();
        var expressions = Items(input["payload"]!.AsObject(), "expressions");
        var shared = expressions[0]!.DeepClone();
        shared["id"] = "twice";
        shared["op"] = "Add";
        shared["arguments"] = new JsonArray("q", "q");
        shared.AsObject().Remove("symbolId");
        expressions.Insert(0, shared);
        Assert.True(JsonNode.DeepEquals(input, JsonNode.Parse(Read(input).Write())));
    }

    [Theory]
    [InlineData("01")] [InlineData("4/2")] [InlineData("0/3")] [InlineData("1/0")]
    [InlineData("-0")] [InlineData("+1")] [InlineData("2/1")] [InlineData("1/-2")]
    [InlineData("1.5")] [InlineData("1e3")] [InlineData("NaN")]
    public void RejectsLossyOrNonCanonicalRationalStrings(string text)
    {
        var input = Template();
        input["payload"]!["expressions"]![3]!["literal"] = text;
        Assert.Equal("udir.rational", Assert.Throws<ContractException>(() => Read(input)).Code);
    }

    [Fact]
    public void OptionalBoundsUnknownRequirementsAndLargeExactNumbersSurvive()
    {
        var input = Template();
        var p = input["payload"]!.AsObject();
        p["symbols"]![0]!.AsObject().Remove("upper");
        var large = new string('9', 255) + "/2";
        p["expressions"]![3]!["literal"] = large;
        Items(p, "requires").Add("future.capability");
        var output = UdirDocument.Read(Read(input).Write());
        Assert.Null(output.Model.Symbols[0].Upper);
        Assert.Equal(large, output.Model.Expressions[3].Literal);
        Assert.Contains("future.capability", output.Model.Requires);
        p["expressions"]![3]!["literal"] = new string('9', 257);
        Assert.Equal("udir.rational", Assert.Throws<ContractException>(() => Read(input)).Code);
    }
}
