using WowDi.Contracts;
using WowDi.Solver.Planning;
using WowDi.Verification.Bounds;
using Xunit;
using static WowDi.Solver.Portfolio.Tests.Fixtures;

namespace WowDi.Solver.Portfolio.Tests;

public sealed class PortfolioTests
{
    // max 3x0 + 2x1  s.t. x0 + x1 <= 4, x in [0,3]^2  -> optimum 11 at (3,1)
    private static Ilp Small()=>Build([3,3],[[1,1]],[4],[3,2],true);
    private static PortfolioBudget Budget(long core=4000,int threads=4,long mem=1<<20,int wallMs=2000)=>new(core,threads,mem,TimeSpan.FromMilliseconds(wallMs));
    private static Task<PortfolioResult> Run(Ilp ilp,PortfolioBudget b,PortfolioPolicy? p,params IPortfolioMember[] m)=>PortfolioRunner.RunAsync(Request(ilp,b,p),m);

    [Fact]
    public void LedgerNeverOvercommitsAndQuarantinesUncleanMembers()
    {
        var l=new CoreLedger(1000,4,100);
        var a=l.TryReserve(2,50,TimeSpan.FromMilliseconds(300))!;    // 600
        Assert.Null(l.TryReserve(2,50,TimeSpan.FromMilliseconds(201))); // 402 > 400 left
        var b=l.TryReserve(2,50,TimeSpan.FromMilliseconds(200))!;    // 400
        Assert.Null(l.TryReserve(1,1,TimeSpan.FromMilliseconds(1)));  // threads exhausted
        Assert.Equal((650L,50L),l.Settle(a,650,cleanupConfirmed:true)); // overspend is charged and reported
        Assert.Throws<InvalidOperationException>(()=>l.Settle(a,1,true));
        l.Settle(b,100,cleanupConfirmed:false);
        Assert.Equal(2,l.FreeThreads);                                 // quarantined threads stay reserved
        var t=l.Totals();
        Assert.Equal(1000,t.ReservedCoreMs);Assert.Equal(750,t.ChargedCoreMs);Assert.Equal(50,t.OverspendCoreMs);
        Assert.Equal(4,t.PeakConcurrentThreads);Assert.Equal(2,t.QuarantinedThreads);
        Assert.Equal(250,l.Available);
    }

    [Fact]
    public void RaceAllocationSplitsThreadsByRankAndDerivesCommonDeadline()
    {
        var f=new ModelFeatures(100,100,10,1,0);
        var p=new PortfolioPolicy(SmallModelVariables:64);
        var profiles=new[]{Profile("cp",SolverFamily.CpSat,maxThreads:8),Profile("hi",SolverFamily.Highs,maxThreads:1)};
        var slots=Allocation.Race(profiles,f,Budget(6000,4),p,6000,4,1<<20);
        Assert.Equal([3,1],slots.Select(s=>s.Threads));
        Assert.All(slots,s=>Assert.Equal(TimeSpan.FromMilliseconds(1500),s.HardDeadline)); // 6000 / 4 threads
        Assert.All(slots,s=>Assert.True(s.SoftLimit<s.HardDeadline));
        var small=Allocation.Race(profiles,f with{Variables=10},Budget(6000,4),p,6000,4,1<<20);
        Assert.Equal([1,1],small.Select(s=>s.Threads));                                     // small-model rule
        Assert.All(small,s=>Assert.Equal(TimeSpan.FromMilliseconds(2000),s.HardDeadline));   // wall-bound
        var strict=new[]{profiles[0],Profile("slow",SolverFamily.Highs,minDeadline:TimeSpan.FromSeconds(5))};
        Assert.Single(Allocation.Race(strict,f,Budget(6000,4),p,6000,4,1<<20));             // dropped: deadline too short
    }

    [Fact]
    public void SequentialStagesShareRemainingBudgetAndRollOverUnspent()
    {
        var f=new ModelFeatures(100,100,10,1,0);
        var p=Profile("cp",SolverFamily.CpSat,maxThreads:2);
        var first=Allocation.Stage(p,0,2,f,new(),4000,4,1<<20,TimeSpan.FromSeconds(10))!;
        Assert.Equal(2,first.Threads);Assert.Equal(TimeSpan.FromMilliseconds(1000),first.HardDeadline); // 4000/2/2
        var last=Allocation.Stage(p,1,1,f,new(),3800,4,1<<20,TimeSpan.FromSeconds(10))!;
        Assert.Equal(TimeSpan.FromMilliseconds(1900),last.HardDeadline);                                 // unspent rolled over
        Assert.Null(Allocation.Stage(p,1,1,f,new(),1,4,1<<20,TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task FastestMemberIsNotPreferredOverBetterVerifiedObjective()
    {
        var ilp=Small();var probe=new Probe();
        var fast=new SimulatedMember(Profile("a-fast",SolverFamily.CpSat),ilp,Behaviour.HonestFeasible,0,probe);
        var slow=new SimulatedMember(Profile("b-slow",SolverFamily.CpSat),ilp,Behaviour.HonestOptimal,150,probe);
        var r=await Run(ilp,Budget(),new(Enumerate:false),fast,slow);
        Assert.True(r.Accepted);
        Assert.Equal("b-slow",r.Selected!.Solver.SolverId);
        Assert.Equal("11",r.Objective);
        Assert.Equal(PortfolioStatus.VerifiedFeasible,r.Status);          // attestation only, no proof requested
        Assert.Equal(EvidenceStrength.IndependentlyVerifiedPrimal,r.Strength);
    }

    [Fact]
    public async Task ExhaustiveProofSelectsOptimumAndStopsOtherMembers()
    {
        var ilp=Small();var probe=new Probe();
        var prover=new SimulatedMember(Profile("a",SolverFamily.CpSat),ilp,Behaviour.HonestOptimal,0,probe);
        var hanger=new SimulatedMember(Profile("b",SolverFamily.CpSat),ilp,Behaviour.HangCooperative,0,probe);
        var r=await Run(ilp,Budget(wallMs:5000),new(),prover,hanger);
        Assert.Equal(PortfolioStatus.ProvenOptimal,r.Status);
        Assert.Equal(EvidenceStrength.ExactEnumeration,r.Strength);
        Assert.True(hanger.SawCancellation);
        Assert.True(r.WallMs<2500,$"proof did not stop the race: {r.WallMs} ms");
        Assert.Equal(0,probe.Live);
    }

    [Fact]
    public async Task LiarIsFalsifiedByAnotherMembersVerifiedIncumbent()
    {
        var ilp=Small();var probe=new Probe();
        var liar=new SimulatedMember(Profile("a-liar",SolverFamily.CpSat),ilp,Behaviour.LiarOptimal,0,probe);
        var honest=new SimulatedMember(Profile("b",SolverFamily.CpSat),ilp,Behaviour.HonestOptimal,30,probe);
        var r=await Run(ilp,Budget(),new(Enumerate:false),liar,honest);
        Assert.Equal("b",r.Selected!.Solver.SolverId);
        Assert.Contains(r.Alerts,a=>a.Code=="portfolio.falsifiedOptimality"&&a.Solver.SolverId=="a-liar");
        Assert.Equal("11",r.BestBound);                                // the falsified bound is excluded
        Assert.Equal(1,r.ConcordantAttestations);
    }

    [Theory]
    [InlineData(Behaviour.CorruptInfeasible)]
    [InlineData(Behaviour.WrongObjective)]
    [InlineData(Behaviour.ForeignIdentity)]
    public async Task UnverifiableCandidateIsNeverAccepted(Behaviour b)
    {
        var ilp=Small();
        var r=await Run(ilp,Budget(),new(),new SimulatedMember(Profile("only",SolverFamily.CpSat),ilp,b,0,new()));
        Assert.False(r.Accepted);Assert.Null(r.Selected);Assert.Null(r.Objective);
        Assert.Contains(r.Alerts,a=>a.Code=="portfolio.verificationFailed");
    }

    [Fact]
    public async Task FaultsCleanupFailuresAndAbandonmentAreContained()
    {
        var ilp=Small();var probe=new Probe();
        var members=new IPortfolioMember[]
        {
            new SimulatedMember(Profile("a",SolverFamily.CpSat,maxThreads:1),ilp,Behaviour.Throws,0,probe),
            new SimulatedMember(Profile("b",SolverFamily.CpSat,maxThreads:1),ilp,Behaviour.CleanupUnconfirmed,0,probe),
            new SimulatedMember(Profile("c",SolverFamily.CpSat,maxThreads:1),ilp,Behaviour.HangIgnoringCancel,0,probe),
            new SimulatedMember(Profile("d",SolverFamily.CpSat,maxThreads:1),ilp,Behaviour.HonestFeasible,0,probe),
        };
        var r=await Run(ilp,Budget(wallMs:200),new(CleanupGrace:TimeSpan.FromMilliseconds(50),Enumerate:false),members);
        Assert.Equal("d",r.Selected!.Solver.SolverId);
        Assert.StartsWith("portfolio.memberFault",r.Members.Single(m=>m.Solver.SolverId=="a").Code);
        Assert.Contains(r.Alerts,a=>a.Code=="portfolio.cleanupUnconfirmed"&&a.Solver.SolverId=="b");
        Assert.Null(r.Members.Single(m=>m.Solver.SolverId=="b").VerifiedObjective);   // candidate discarded
        Assert.Equal(MemberPhase.Abandoned,r.Members.Single(m=>m.Solver.SolverId=="c").Phase);
        Assert.Equal(3,r.Ledger.QuarantinedThreads);                                   // a (fault), b, c stay quarantined
        Assert.True(r.WallMs<1500);
    }

    [Fact]
    public async Task IneligibleMembersAreRecordedWithPlanningCode()
    {
        var ilp=Small();
        var r=await Run(ilp,Budget(),new(),
            new SimulatedMember(Profile("lp-only",SolverFamily.Highs,features:["rational.linear"]),ilp,Behaviour.HonestOptimal,0,new()),
            new SimulatedMember(Profile("ok",SolverFamily.CpSat),ilp,Behaviour.HonestOptimal,0,new()));
        Assert.Equal("plan.capability",r.Members.Single(m=>m.Solver.SolverId=="lp-only").Code);
        Assert.Equal(MemberPhase.Ineligible,r.Members.Single(m=>m.Solver.SolverId=="lp-only").Phase);
        Assert.Equal("ok",r.Selected!.Solver.SolverId);
    }

    [Fact]
    public async Task InfeasibleModelProvenOnlyByExhaustiveCheckAndFalseClaimsRejected()
    {
        var ilp=Build([2,2],[[-1,-1]],[-5],[1,1],false);   // x0 + x1 >= 5 impossible
        var r=await Run(ilp,Budget(),new(),new SimulatedMember(Profile("a",SolverFamily.CpSat),ilp,Behaviour.HonestInfeasible,0,new()));
        Assert.Equal(PortfolioStatus.ProvenInfeasible,r.Status);Assert.False(r.Accepted);
        var feasible=Small();
        var lie=await Run(feasible,Budget(),new(),new SimulatedMember(Profile("a",SolverFamily.CpSat),feasible,Behaviour.ClaimInfeasible,0,new()));
        Assert.NotEqual(PortfolioStatus.ProvenInfeasible,lie.Status);
        Assert.Contains(lie.Alerts,a=>a.Code=="portfolio.verificationFailed");
    }

    [Fact]
    public async Task OverspendIsChargedAndReportedNeverHidden()
    {
        var ilp=Small();
        var r=await Run(ilp,Budget(),new(Enumerate:false),new SimulatedMember(Profile("a",SolverFamily.CpSat),ilp,Behaviour.Overspend,0,new()));
        Assert.Equal(137,r.Ledger.OverspendCoreMs);
        Assert.Equal(r.Members[0].ReservedCoreMs+137,r.Ledger.ChargedCoreMs);
    }

    [Fact]
    public async Task AttestedOptimumStopsOthersAfterCrossCheckGrace()
    {
        var ilp=Small();
        var hanger=new SimulatedMember(Profile("b",SolverFamily.CpSat),ilp,Behaviour.HangCooperative,0,new());
        var r=await Run(ilp,Budget(wallMs:5000),new(Enumerate:false,AttestedStopGrace:TimeSpan.FromMilliseconds(100)),
            new SimulatedMember(Profile("a",SolverFamily.CpSat),ilp,Behaviour.HonestOptimal,0,new()),hanger);
        Assert.True(hanger.SawCancellation);Assert.True(r.WallMs<2000);
        Assert.Equal("a",r.Selected!.Solver.SolverId);
    }

    [Fact]
    public async Task HostCancellationNeverReturnsAnAcceptedDecision()
    {
        var ilp=Small();using var cts=new CancellationTokenSource(50);
        var r=await PortfolioRunner.RunAsync(Request(ilp,Budget(wallMs:5000),new(Enumerate:false)),
            [new SimulatedMember(Profile("a",SolverFamily.CpSat),ilp,Behaviour.HangCooperative,0,new())],cts.Token);
        Assert.False(r.Accepted);Assert.Contains(r.Alerts,a=>a.Code=="portfolio.cancelled");
    }

    [Fact]
    public async Task InvalidRequestsAndCatalogsAreRejected()
    {
        var ilp=Small();
        await Assert.ThrowsAsync<ContractException>(()=>Run(ilp,Budget(core:0),new()));
        await Assert.ThrowsAsync<ContractException>(()=>Run(ilp,Budget(),new(SoftLimitFraction:1.5)));
        var m=new SimulatedMember(Profile("dup",SolverFamily.CpSat),ilp,Behaviour.HonestOptimal,0,new());
        await Assert.ThrowsAsync<ContractException>(()=>Run(ilp,Budget(),new(),m,m));
    }
}
