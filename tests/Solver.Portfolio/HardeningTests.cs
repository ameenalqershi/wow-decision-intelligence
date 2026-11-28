using WowDi.Contracts;
using WowDi.Solver.Contracts;
using WowDi.Solver.Planning;
using Xunit;
using static WowDi.Solver.Portfolio.Tests.Fixtures;

namespace WowDi.Solver.Portfolio.Tests;

/// <summary>Targeted tests added after mutation testing (Stryker) exposed weakly checked guards and paths.</summary>
public sealed class HardeningTests
{
    private static Ilp Small()=>Build([3,3],[[1,1]],[4],[3,2],true);
    private static PortfolioBudget B(long core=4000,int threads=4,long mem=1<<20,int wallMs=2000)=>new(core,threads,mem,TimeSpan.FromMilliseconds(wallMs));

    [Theory]
    [InlineData(0,4,100)]
    [InlineData(10,0,100)]
    [InlineData(10,4,0)]
    public void LedgerRejectsNonPositiveCapacities(long core,int threads,long mem)=>
        Assert.Throws<ArgumentOutOfRangeException>(()=>new CoreLedger(core,threads,mem));

    [Fact]
    public void LedgerGuardsArgumentsAndClampsAvailableAfterOverspend()
    {
        var l=new CoreLedger(1,1,1);
        Assert.Equal(1,l.Available);Assert.Equal(1,l.FreeThreads);Assert.Equal(1,l.FreeMemory);
        Assert.Throws<ArgumentOutOfRangeException>(()=>l.TryReserve(0,1,TimeSpan.FromMilliseconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(()=>l.TryReserve(1,0,TimeSpan.FromMilliseconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(()=>l.TryReserve(1,1,TimeSpan.Zero));
        var r=l.TryReserve(1,1,TimeSpan.FromMilliseconds(1))!;
        Assert.Equal(0,l.Available);Assert.Equal(0,l.FreeThreads);Assert.Equal(0,l.FreeMemory);
        Assert.Throws<ArgumentOutOfRangeException>(()=>l.Settle(r,-1,true));
        Assert.Throws<ArgumentNullException>(()=>l.Settle(null!,1,true));
        Assert.Equal((5L,4L),l.Settle(r,5,true));
        Assert.Equal(0,l.Available);                                   // clamped, never negative
        Assert.Equal(1,l.FreeThreads);Assert.Equal(1,l.FreeMemory);
        Assert.Equal(new LedgerTotals(1,1,5,4,1,1,0),l.Totals());
    }

    [Fact]
    public void SoftLimitHonoursFractionGraceAndFallback()
    {
        var p=new PortfolioPolicy(SoftLimitFraction:0.5,MinimumGrace:TimeSpan.FromMilliseconds(100));
        Assert.Equal(TimeSpan.FromMilliseconds(500),Allocation.SoftLimit(TimeSpan.FromSeconds(1),p));
        Assert.Equal(TimeSpan.FromMilliseconds(100),Allocation.SoftLimit(TimeSpan.FromMilliseconds(200),p with{SoftLimitFraction=0.9}));
        Assert.Equal(TimeSpan.FromMilliseconds(25),Allocation.SoftLimit(TimeSpan.FromMilliseconds(50),p));       // grace larger than hard -> half
        var small=new PortfolioPolicy(MemberGrace:new Dictionary<string,TimeSpan>{["x"]=TimeSpan.FromMilliseconds(10)});
        Assert.Equal(Allocation.SoftLimit(TimeSpan.FromSeconds(1),small),Allocation.SoftLimit(TimeSpan.FromSeconds(1),small,"x")); // smaller than default grace: ignored
        var f=new ModelFeatures(64,64,1,1,0);
        var prof=Profile("c",SolverFamily.CpSat,maxThreads:8,minThreads:2);
        Assert.Equal(2,Allocation.ThreadCeiling(prof,f,new()));                       // small model, but never below the minimum
        Assert.Equal(8,Allocation.ThreadCeiling(prof,f with{Variables=65},new()));
        Assert.Equal(8,Allocation.ThreadCeiling(prof,f,new(SmallModelVariables:0)));
    }

    [Fact]
    public void RaceAdmitsByMinimumResourcesAndSplitsMemory()
    {
        var f=new ModelFeatures(100,100,1,1,0);
        var a=Profile("a",SolverFamily.CpSat,maxThreads:2,minThreads:2,minMemory:300);
        var b=Profile("b",SolverFamily.Highs,maxThreads:2,minThreads:2,minMemory:300);
        var slots=Allocation.Race([a,b],f,B(),new(),4000,3,1000);                   // 3 free threads: only a fits
        Assert.Single(slots);Assert.Equal(2,slots[0].Threads);
        var two=Allocation.Race([a,b],f,B(),new(),4000,4,1000);
        Assert.Equal([500L,500L],two.Select(s=>s.MemoryBytes));                   // 300 + (1000-600)/2
        Assert.Empty(Allocation.Race([a],f,B(),new(),1,4,1000));                  // budget too small for 1 ms
        Assert.Empty(Allocation.Race([a],f,B(),new(),4000,4,100));                // memory below minimum
    }

    [Fact]
    public async Task RequestValidationRejectsEveryMalformedField()
    {
        var ilp=Small();var m=new SimulatedMember(Profile("a",SolverFamily.CpSat),ilp,Behaviour.HonestOptimal,0,new());
        async Task Bad(PortfolioRequest r)=>await Assert.ThrowsAsync<ContractException>(()=>PortfolioRunner.RunAsync(r,[m]));
        var ok=Request(ilp,B());
        await Bad(ok with{RunId=""});await Bad(ok with{RunId=new string('r',201)});
        await Bad(ok with{Budget=B(threads:0)});await Bad(ok with{Budget=B(mem:0)});
        await Bad(ok with{Budget=new(10,1,1,TimeSpan.Zero)});await Bad(ok with{Budget=new(10,1,1,TimeSpan.FromDays(8))});
        await Bad(ok with{Budget=B(threads:5000)});
        await Bad(ok with{Policy=new(SoftLimitFraction:0)});await Bad(ok with{Policy=new(MinimumGrace:TimeSpan.FromMilliseconds(-1))});
        await Bad(ok with{Policy=new(SmallModelVariables:-1)});await Bad(ok with{Policy=new(AttestedStopGrace:TimeSpan.FromMilliseconds(-1))});
        await Bad(ok with{Policy=new(ProbeFraction:0)});await Bad(ok with{Policy=new(ProbeFraction:1)});
        await Bad(ok with{Policy=new(Stop:(StopRule)9)});await Bad(ok with{Policy=new(Mode:(PortfolioMode)9)});
        await Bad(ok with{Policy=new(MemberGrace:new Dictionary<string,TimeSpan>{["a"]=TimeSpan.FromMilliseconds(-1)})});
        await Bad(ok with{Policy=new(CleanupGrace:TimeSpan.FromMilliseconds(-1))});
        await Bad(ok with{CatalogBytes=Array.Empty<byte>()});await Bad(ok with{CatalogBytes=new byte[262145]});
        var many=Enumerable.Range(0,65).Select(i=>new SimulatedMember(Profile("m"+i,SolverFamily.CpSat),ilp,Behaviour.HonestOptimal,0,new())).ToArray();
        await Assert.ThrowsAsync<ContractException>(()=>PortfolioRunner.RunAsync(ok,many));
        await Assert.ThrowsAsync<ContractException>(()=>PortfolioRunner.RunAsync(ok,[null!]));
        var exactly=await PortfolioRunner.RunAsync(ok with{Policy=new(Enumerate:false)},many.Take(64));            // 64 is allowed
        Assert.Equal(64,exactly.Members.Length);
    }

    [Fact]
    public async Task SequentialStopsAfterProofAndRecordsSkippedMembers()
    {
        var ilp=Small();
        var r=await PortfolioRunner.RunAsync(Request(ilp,B(),new(Mode:PortfolioMode.Sequential)),
            [new SimulatedMember(Profile("a",SolverFamily.CpSat),ilp,Behaviour.HonestOptimal,0,new()),
             new SimulatedMember(Profile("b",SolverFamily.CpSat),ilp,Behaviour.HonestOptimal,0,new())]);
        Assert.Equal(StopReason.Proof,r.StopReason);Assert.Equal(PortfolioStatus.ProvenOptimal,r.Status);
        Assert.Equal("portfolio.stopped",r.Members.Single(x=>x.Solver.SolverId=="b").Code);
        Assert.Equal(MemberPhase.NotScheduled,r.Members.Single(x=>x.Solver.SolverId=="b").Phase);
    }

    [Fact]
    public async Task FalseBoundAndFalseInfeasibilityClaimsAreFlaggedAndExcluded()
    {
        var ilp=Small();
        var r=await PortfolioRunner.RunAsync(Request(ilp,B(),new(Enumerate:false)),
            [new SimulatedMember(Profile("a",SolverFamily.CpSat),ilp,Behaviour.HonestFeasible,0,new()),     // feasible, honest bound 11
             new SimulatedMember(Profile("b",SolverFamily.CpSat),ilp,Behaviour.ClaimInfeasible,0,new()),
             new SimulatedMember(Profile("c",SolverFamily.CpSat),ilp,Behaviour.LiarOptimal,0,new()),
             new SimulatedMember(Profile("d",SolverFamily.CpSat),ilp,Behaviour.HonestOptimal,0,new())]);
        // the liar's attested optimum and bound are refuted by d's verified 11 and excluded from the portfolio bound
        Assert.Contains(r.Alerts,x=>x.Code=="portfolio.falsifiedOptimality"&&x.Solver.SolverId=="c");
        Assert.Contains(r.Alerts,x=>x.Code=="portfolio.falsifiedInfeasibility"&&x.Solver.SolverId=="b"&&x.Witness!.SolverId is "a" or "c" or "d");
        Assert.NotEqual(PortfolioStatus.ProvenInfeasible,r.Status);
        Assert.Equal("11",r.BestBound);Assert.Equal("a",r.BoundSource!.SolverId);
        Assert.True(r.Accepted);Assert.NotNull(r.AbsoluteGap);
    }

    [Fact]
    public async Task AbandonedRecordCarriesAssignmentChargeAndTiming()
    {
        var ilp=Small();
        var r=await PortfolioRunner.RunAsync(Request(ilp,B(wallMs:100),new(CleanupGrace:TimeSpan.FromMilliseconds(20),Enumerate:false)),
            [new SimulatedMember(Profile("a",SolverFamily.CpSat,maxThreads:1),ilp,Behaviour.HangIgnoringCancel,0,new())]);
        var a=r.Members.Single();
        Assert.Equal(MemberPhase.Abandoned,a.Phase);Assert.NotNull(a.Assignment);
        Assert.Equal(a.ReservedCoreMs,a.ChargedCoreMs);Assert.True(a.ReservedCoreMs>0);
        Assert.True(a.FinishedAtMs>=a.StartedAtMs&&a.ElapsedMs>=100);
        Assert.False(r.Accepted);Assert.Equal(PortfolioStatus.Unknown,r.Status);Assert.Equal(1,r.Ledger.QuarantinedThreads);
        Assert.Contains(r.Alerts,x=>x.Code=="portfolio.abandoned");
    }
}
