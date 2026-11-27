using System.Collections.Immutable;
using WowDi.Contracts;
using WowDi.Udir;
using Xunit;

namespace WowDi.Solver.Contracts.Tests;

public sealed class SolverContractTests
{
    private static UdirDocument Document() => UdirDocument.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "factory.json")));
    private static readonly SolverIdentity Identity = new("example", "1.0", "linux-x64");
    private static SolverCapabilities Manifest() => new(Identity, ["integer.linear"], [ScalarKind.Integer],
        "100000000000000000000", 1000, 2, false, true, true);
    private static readonly Evidence Attestation = new(EvidenceSource.SolverAttestation, "fixture/log");
    private static SolverResult Report() => new("fixture", "factory", "r17", "run1", Identity, MathematicalStatus.Feasible,
        Termination.Timeout, ImmutableDictionary<string, string>.Empty.Add("x", "3"),
        [new("revenue", "7", new("22/3", Direction.Maximize, Attestation), "0", "1/1000")], null, new(0, null, 8192));

    [Fact]
    public void TimeoutCanRetainIncumbentAndMissingCounters()
    {
        var report = Report();
        report.ValidateAgainst(Document(), Manifest());
        Assert.Equal(Termination.Timeout, report.Termination);
        Assert.Null(report.Resources.CpuMs);
        Assert.Equal(0L, report.Resources.ElapsedMs);
        Assert.Equal("staged.uncertainty", Assert.Single(Manifest().MissingFeatures(Document())));
    }

    [Theory]
    [InlineData(Termination.Cancelled)]
    [InlineData(Termination.Timeout)]
    [InlineData(Termination.Failed)]
    public void UnknownCanHaveNoWitnessOrBound(Termination termination)
    {
        var report = Report() with
        {
            Termination = termination,
            MathematicalStatus = MathematicalStatus.Unknown,
            Incumbent = null,
            Objectives = [new("revenue", null, null, "0", "0")],
            Resources = new(null, null, null)
        };
        report.ValidateAgainst(Document(), Manifest());
    }

    [Fact]
    public void VerifiedCertificateAttributionSurvivesWithoutImplicitPromotion()
    {
        (Report() with
        {
            MathematicalStatus = MathematicalStatus.Optimal,
            Termination = Termination.Completed,
            StatusEvidence = new(EvidenceSource.IndependentCertificate, "fixture/cert"),
            Objectives = [new("revenue", "7", new("7", Direction.Maximize, Attestation), "0", "0")]
        }).ValidateAgainst(Document(), Manifest());
        var row = Report().Objectives[0] with { Bound = null };
        (Report() with { Objectives = [row] }).ValidateAgainst(Document(), Manifest() with { SupportsBounds = false });
    }

    [Fact]
    public void MinimizingUsesLowerBoundAndKeepsExactRationalOrdering()
    {
        var input = Document().Model;
        var model = UdirDocument.Create(input with { Objectives = [input.Objectives[0] with { Direction = Direction.Minimize }] });
        var row = new ObjectiveObservation("revenue", "7/3", new("2", Direction.Minimize, Attestation), "0", "0");
        (Report() with { Objectives = [row] }).ValidateAgainst(model, Manifest());
        var invalid = row with { Bound = new("8/3", Direction.Minimize, Attestation) };
        Assert.Equal("solver.boundOrder", Assert.Throws<ContractException>(() =>
            (Report() with { Objectives = [invalid] }).ValidateAgainst(model, Manifest())).Code);
    }

    [Theory]
    [InlineData("tenant", "scope")]
    [InlineData("revision", "scope")]
    [InlineData("version", "scope")]
    [InlineData("optimal", "proof")]
    [InlineData("infeasible", "incumbent")]
    [InlineData("unbounded", "proof")]
    [InlineData("extra", "assignment")]
    [InlineData("case", "assignment")]
    [InlineData("fraction", "assignment")]
    [InlineData("objectives", "objectives")]
    [InlineData("tolerance", "tolerance")]
    [InlineData("bound", "boundOrder")]
    [InlineData("direction", "bound")]
    [InlineData("rss", "measurement")]
    public void RejectionCases(string mutation, string diagnostic)
    {
        var r = Report();
        var row = r.Objectives[0];
        var bad = mutation switch
        {
            "tenant" => r with { TenantId = "foreign" },
            "revision" => r with { RevisionId = "stale" },
            "version" => r with { Solver = Identity with { Version = "other" } },
            "optimal" => r with { MathematicalStatus = MathematicalStatus.Optimal },
            "infeasible" => r with { MathematicalStatus = MathematicalStatus.Infeasible, StatusEvidence = Attestation },
            "unbounded" => r with { MathematicalStatus = MathematicalStatus.Unbounded },
            "extra" => r with { Incumbent = r.Incumbent!.Add("y", "1") },
            "case" => r with { Incumbent = ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase).Add("X", "3") },
            "fraction" => r with { Incumbent = r.Incumbent!.SetItem("x", "1/2") },
            "objectives" => r with { Objectives = [] },
            "tolerance" => r with { Objectives = [row with { AbsoluteTolerance = "-1/5" }] },
            "bound" => r with { Objectives = [row with { Bound = new("6", Direction.Maximize, Attestation) }] },
            "direction" => r with { Objectives = [row with { Bound = new("6", Direction.Minimize, Attestation) }] },
            _ => r with { Resources = new(null, null, -1) }
        };
        Assert.Equal("solver." + diagnostic, Assert.Throws<ContractException>(() => bad.ValidateAgainst(Document(), Manifest())).Code);
    }

    [Fact]
    public void ManifestsRejectAmbiguousTokensAndInvalidLimits()
    {
        Assert.Throws<ContractException>(() => (Manifest() with { Features = ["x", "x"] }).Validate());
        Assert.Throws<ContractException>(() => (Manifest() with { Domains = [] }).Validate());
        Assert.Throws<ContractException>(() => (Manifest() with { MaxThreads = 0 }).Validate());
        Assert.Throws<ContractException>(() => (Manifest() with { MaxCoefficientMagnitude = "-1" }).Validate());
    }
}
