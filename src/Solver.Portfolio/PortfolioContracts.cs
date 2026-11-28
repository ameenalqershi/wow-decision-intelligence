using System.Collections.Immutable;
using WowDi.Dml.Semantics;
using WowDi.Solver.Contracts;
using WowDi.Solver.Planning;
using WowDi.Verification.Bounds;

namespace WowDi.Solver.Portfolio;

/// <summary>Aggregate portfolio budget. Core-milliseconds are the unit of account for every member:
/// a member reserving T threads for D milliseconds reserves T*D core-milliseconds before it starts.</summary>
public sealed record PortfolioBudget(long CoreMilliseconds,int MaxConcurrentThreads,long MemoryBytes,TimeSpan WallDeadline);

public enum PortfolioMode { Race, Sequential }

/// <summary>Host policy. Nothing here can weaken verification: every candidate is always classified by S05.</summary>
/// <param name="Mode">Race runs admitted members concurrently under the thread cap; Sequential runs them one at a time.</param>
/// <param name="AttestedStopGrace">After a verified incumbent arrives whose solver attests optimality with a matching bound,
/// remaining members may continue for this long to obtain a cross-check, then are cancelled. Null disables attested stopping.</param>
/// <param name="SoftLimitFraction">Fraction of the hard deadline handed to the member as its own solver time limit, so it can
/// return its incumbent before the boundary kills it.</param>
/// <param name="MinimumGrace">Minimum gap between the member soft limit and the hard deadline.</param>
/// <param name="SmallModelVariables">Models with at most this many variables run every member single-threaded
/// (initial deterministic heuristic, to be calibrated by X09). Zero disables the rule.</param>
/// <param name="Enumerate">Ask S05 for an exhaustive proof when its bounded enumeration budget allows it.</param>
/// <param name="CleanupGrace">How long a member may take to return after its hard deadline before it is declared abandoned.</param>
public sealed record PortfolioPolicy(PortfolioMode Mode=PortfolioMode.Race,TimeSpan? AttestedStopGrace=null,
    double SoftLimitFraction=0.85,TimeSpan? MinimumGrace=null,int SmallModelVariables=64,bool Enumerate=true,TimeSpan? CleanupGrace=null)
{
    internal TimeSpan Grace=>MinimumGrace??TimeSpan.FromMilliseconds(250);
    internal TimeSpan Cleanup=>CleanupGrace??TimeSpan.FromSeconds(15);
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
    BoundAssessment? Assessment,string? VerifiedObjective,long StartedAtMs,long FinishedAtMs);

public enum PortfolioStatus { ProvenOptimal, VerifiedFeasible, ProvenInfeasible, Unknown }

public sealed record PortfolioAlert(string Code,SolverIdentity Solver,SolverIdentity? Witness,string Detail);

public sealed record LedgerTotals(long BudgetCoreMs,long ReservedCoreMs,long ChargedCoreMs,long OverspendCoreMs,int PeakConcurrentThreads,
    long PeakReservedMemoryBytes,long QuarantinedThreads);

/// <summary>Portfolio outcome. Accepted means: a member candidate passed the independent original-model verifier.
/// Optimality is ProvenOptimal only with an S05 exhaustive proof; solver attestations stay attestations.</summary>
public sealed record PortfolioResult(string RunId,ModelFeatures Features,PortfolioStatus Status,bool Accepted,MemberRecord? Selected,
    string? Objective,EvidenceStrength Strength,string? BestBound,EvidenceStrength BoundStrength,SolverIdentity? BoundSource,
    string? AbsoluteGap,int ConcordantAttestations,ImmutableArray<MemberRecord> Members,ImmutableArray<PortfolioAlert> Alerts,
    LedgerTotals Ledger,long WallMs);
