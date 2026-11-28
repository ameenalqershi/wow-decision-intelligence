using System.Collections.Immutable;
using WowDi.Dml.Semantics;
using WowDi.Solver.Contracts;
using WowDi.Solver.Planning;
using WowDi.Verification.Bounds;

namespace WowDi.Solver.Portfolio;

/// <summary>Aggregate portfolio budget. Core-milliseconds are the unit of account for every member:
/// a member reserving T threads for D milliseconds reserves T*D core-milliseconds before it starts.</summary>
public sealed record PortfolioBudget(long CoreMilliseconds,int MaxConcurrentThreads,long MemoryBytes,TimeSpan WallDeadline);

/// <summary>Race: all admitted members concurrently. Sequential: one at a time, unspent budget rolls over.
/// Escalate: the best-ranked member alone for <see cref="PortfolioPolicy.ProbeFraction"/> of the budget; only if the stop rule
/// is not met does the remaining budget go to a race of all members (difficulty-adaptive, no prediction needed).</summary>
public enum PortfolioMode { Race, Sequential, Escalate }

/// <summary>When may the portfolio stop other members early? Stopping never changes evidence: an attested optimum stays a
/// verified feasible point with a solver attestation; only an independent proof yields ProvenOptimal.</summary>
public enum StopRule
{
    /// <summary>Stop only on an independent proof (S05 exhaustive enumeration). Default; no claim alone stops the search.</summary>
    ProofOnly,
    /// <summary>Also stop when members of two different solver families attest optimality with the same verified objective
    /// and neither claim is falsified (independent concordance; still not a proof).</summary>
    Concordant,
    /// <summary>Also stop on a single verified incumbent whose solver attests optimality with a matching bound, after
    /// <see cref="PortfolioPolicy.AttestedStopGrace"/>. Opt-in; cheaper on easy models, weakest evidence for stopping.</summary>
    SingleAttestation
}

/// <summary>Host policy. Nothing here can weaken verification: every candidate is always classified by S05.</summary>
/// <param name="Mode">Race, Sequential or Escalate.</param>
/// <param name="AttestedStopGrace">Cross-check window used by <see cref="StopRule.SingleAttestation"/> (default zero).</param>
/// <param name="SoftLimitFraction">Fraction of the hard deadline handed to the member as its own solver time limit, so it can
/// return its incumbent before the boundary kills it.</param>
/// <param name="MinimumGrace">Minimum gap between the member soft limit and the hard deadline.</param>
/// <param name="SmallModelVariables">Models with at most this many variables run every member single-threaded
/// (initial deterministic heuristic, to be calibrated by X09). Zero disables the rule.</param>
/// <param name="Enumerate">Ask S05 for an exhaustive proof when its bounded enumeration budget allows it.</param>
/// <param name="CleanupGrace">How long a member may take to return after its hard deadline before it is declared abandoned.</param>
/// <param name="Stop">Early-stop rule (default <see cref="StopRule.ProofOnly"/>).</param>
/// <param name="ProbeFraction">Escalate mode: share of core budget and wall time given to the single probe member.</param>
/// <param name="MemberGrace">Per-solver minimum soft-limit margin (keyed by SolverId), from measured engine overruns.</param>
public sealed record PortfolioPolicy(PortfolioMode Mode=PortfolioMode.Race,TimeSpan? AttestedStopGrace=null,
    double SoftLimitFraction=0.85,TimeSpan? MinimumGrace=null,int SmallModelVariables=64,bool Enumerate=true,TimeSpan? CleanupGrace=null,
    StopRule Stop=StopRule.ProofOnly,double ProbeFraction=0.25,IReadOnlyDictionary<string,TimeSpan>? MemberGrace=null)
{
    internal TimeSpan Grace=>MinimumGrace??TimeSpan.FromMilliseconds(250);
    internal TimeSpan Cleanup=>CleanupGrace??TimeSpan.FromSeconds(15);
    internal TimeSpan GraceFor(string solverId)=>MemberGrace is {} g&&g.TryGetValue(solverId,out var v)&&v>Grace?v:Grace;
}

/// <summary>What the host hands to a member for one bounded execution.</summary>
public sealed record MemberAssignment(string RunId,int Threads,long MemoryBytes,TimeSpan HardDeadline,TimeSpan SoftLimit,PlannedExecution Plan);

public enum CpuSource { Measured, ReservationBound, Simulated }

/// <summary>What a member reports back. CPU must come from the execution boundary (cgroup/job object), never from the solver.</summary>
public sealed record MemberExecution(Termination Termination,SolverResult? Result,long ElapsedMs,long? CpuMs,long? PeakBytes,
    bool CleanupConfirmed,string? Diagnostic,CpuSource CpuSource);

/// <summary>An executable portfolio member. New solvers join by registration (profile + implementation),
/// never by changing the portfolio. The profile is a trusted deployment registration (S06).</summary>
public interface IPortfolioMember
{
    AdapterProfile Profile {get;}
    Task<MemberExecution> ExecuteAsync(MemberAssignment assignment,CancellationToken cancellationToken);
}

/// <summary>Frozen inputs for one portfolio decision. Catalog and facts are the exact registered bytes.</summary>
public sealed record PortfolioRequest(string RunId,NormalizedModel Model,ReadOnlyMemory<byte> CatalogBytes,ReadOnlyMemory<byte>? FactsBytes,
    string RuntimeId,NumericRequirement NumericRequirement,PortfolioBudget Budget,PortfolioPolicy Policy);

public enum MemberPhase { Ineligible, NotScheduled, Completed, Abandoned }

public sealed record MemberRecord(SolverIdentity Solver,SolverFamily Family,int Rank,MemberPhase Phase,string Code,
    MemberAssignment? Assignment,Termination? Termination,long ReservedCoreMs,long ChargedCoreMs,long OverspendCoreMs,
    long? CpuMs,CpuSource? CpuSource,long ElapsedMs,long? PeakBytes,bool CleanupConfirmed,string? Diagnostic,
    BoundAssessment? Assessment,string? VerifiedObjective,long StartedAtMs,long FinishedAtMs,int Stage=0);

public enum PortfolioStatus { ProvenOptimal, VerifiedFeasible, ProvenInfeasible, Unknown }

/// <summary>Why the portfolio ended: budget exhausted or all members finished, or which early-stop rule fired.</summary>
public enum StopReason { Completed, Proof, Concordant, SingleAttestation, HostCancelled }

public sealed record PortfolioAlert(string Code,SolverIdentity Solver,SolverIdentity? Witness,string Detail);

public sealed record LedgerTotals(long BudgetCoreMs,long ReservedCoreMs,long ChargedCoreMs,long OverspendCoreMs,int PeakConcurrentThreads,
    long PeakReservedMemoryBytes,long QuarantinedThreads);

/// <summary>Portfolio outcome. Accepted means: a member candidate passed the independent original-model verifier.
/// Optimality is ProvenOptimal only with an S05 exhaustive proof; solver attestations stay attestations.</summary>
public sealed record PortfolioResult(string RunId,ModelFeatures Features,PortfolioStatus Status,bool Accepted,MemberRecord? Selected,
    string? Objective,EvidenceStrength Strength,string? BestBound,EvidenceStrength BoundStrength,SolverIdentity? BoundSource,
    string? AbsoluteGap,int ConcordantAttestations,ImmutableArray<MemberRecord> Members,ImmutableArray<PortfolioAlert> Alerts,
    LedgerTotals Ledger,long WallMs,StopReason StopReason=StopReason.Completed,bool Escalated=false);
