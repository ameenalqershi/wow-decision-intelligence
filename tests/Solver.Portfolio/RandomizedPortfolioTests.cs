using System.Globalization;
using System.Text.Json;
using WowDi.Solver.Contracts;
using WowDi.Solver.Planning;
using WowDi.Udir;
using WowDi.Verification.Bounds;
using Xunit;
using Xunit.Abstractions;

namespace WowDi.Solver.Portfolio.Tests;

/// <summary>Seeded, reproducible randomized cases (simulated members, real S05 verifier, independent brute-force oracle).
/// Case count: WOWDI_PORTFOLIO_CASES (default 10000). A failure message carries the seed to replay the exact case.</summary>
public sealed class RandomizedPortfolioTests(ITestOutputHelper output)
{
    private static readonly (Behaviour B,int W)[] Weights=
    [
        (Behaviour.HonestOptimal,30),(Behaviour.HonestFeasible,20),(Behaviour.LiarOptimal,10),(Behaviour.CorruptInfeasible,8),
        (Behaviour.WrongObjective,6),(Behaviour.ForeignIdentity,4),(Behaviour.Throws,3),(Behaviour.FailedExit,3),
        (Behaviour.HangCooperative,2),(Behaviour.HangIgnoringCancel,1),(Behaviour.CleanupUnconfirmed,3),(Behaviour.Overspend,4),
        (Behaviour.ClaimInfeasible,3),(Behaviour.HonestInfeasible,3),(Behaviour.NoResult,2)
    ];
    private static Behaviour Pick(Random r)
    {
        var x=r.Next(Weights.Sum(w=>w.W));
        foreach(var (b,w) in Weights){if(x<w)return b;x-=w;}
        return Behaviour.HonestOptimal;
    }
    private static readonly Behaviour[] Valid=[Behaviour.HonestOptimal,Behaviour.HonestFeasible,Behaviour.LiarOptimal,Behaviour.Overspend];

    [Fact]
    public async Task TenThousandSeededCasesHoldEveryPortfolioInvariant()
    {
        var cases=int.TryParse(Environment.GetEnvironmentVariable("WOWDI_PORTFOLIO_CASES"),NumberStyles.None,CultureInfo.InvariantCulture,out var n)?n:10000;
        var stats=new Dictionary<string,long>(StringComparer.Ordinal);
        void Count(string k,long by=1)=>stats[k]=stats.GetValueOrDefault(k)+by;
        var clock=System.Diagnostics.Stopwatch.StartNew();
        for(int seed=1;seed<=cases;seed++)
        {
            var r=new Random(seed);
            var ilp=Fixtures.Random(r,forceInfeasible:r.Next(12)==0);
            var (opt,_,_)=ilp.Oracle();
            var probe=new Probe();
            var count=r.Next(1,5);
            var members=new List<SimulatedMember>();
            for(int k=0;k<count;k++)
            {
                var family=r.Next(2)==0?SolverFamily.CpSat:SolverFamily.Highs;
                var profile=Fixtures.Profile("m"+k,family,maxThreads:r.Next(1,5),
                    features:r.Next(25)==0?["rational.linear"]:null);
                var b=Pick(r);Count("behaviour."+b);
                members.Add(new(profile,ilp,b,r.Next(0,4),probe));
            }
            var threads=r.Next(1,7);
            var budget=new PortfolioBudget(r.Next(40,2000),threads,1<<24,TimeSpan.FromMilliseconds(r.Next(30,150)));
            var policy=new PortfolioPolicy((PortfolioMode)r.Next(3),
                r.Next(3) switch{0=>null,1=>TimeSpan.Zero,_=>TimeSpan.FromMilliseconds(10)},
                Enumerate:r.Next(4)!=0,CleanupGrace:TimeSpan.FromMilliseconds(30),SmallModelVariables:r.Next(2)*64,
                Stop:(StopRule)r.Next(3),ProbeFraction:0.1+0.5*r.NextDouble());
            var result=await PortfolioRunner.RunAsync(Fixtures.Request(ilp,budget,policy,"case"+seed),members);
            string Ctx()=>$"seed={seed} status={result.Status} obj={result.Objective} opt={opt} mode={policy.Mode} "+
                string.Join(",",members.Select(m=>m.Profile.Manifest.Solver.SolverId+":"+m.Behaviour));
            bool Better(string a,long b)=>ilp.Maximize?ExactValues.Compare(a,b.ToString(CultureInfo.InvariantCulture))>0:ExactValues.Compare(a,b.ToString(CultureInfo.InvariantCulture))<0;

            // I1 concurrency cap (observed by the members themselves and by the ledger)
            Assert.True(probe.Peak<=threads&&result.Ledger.PeakConcurrentThreads<=threads,"I1 "+Ctx());
            // I2 admission never over-commits the core budget; only measured overspend can exceed it
            Assert.True(result.Ledger.ChargedCoreMs-result.Ledger.OverspendCoreMs<=budget.CoreMilliseconds,"I2 "+Ctx());
            // I3 an accepted decision is a verified, genuinely attainable value from a well-behaved member
            if(result.Accepted)
            {
                Assert.NotNull(opt);
                Assert.False(Better(result.Objective!,opt!.Value),"I3 better than optimum "+Ctx());
                var sel=members.Single(m=>m.Profile.Manifest.Solver==result.Selected!.Solver);
                Assert.Contains(sel.Behaviour,Valid);
                Assert.True(result.Selected!.CleanupConfirmed,"I3 cleanup "+Ctx());
            }
            if(opt is null)Assert.False(result.Accepted,"I3 infeasible accepted "+Ctx());
            // I4 nothing verified is better than the selection
            foreach(var m in result.Members.Where(m=>m.VerifiedObjective is not null))
                Assert.True(result.Objective is not null&&!(ilp.Maximize?ExactValues.Compare(m.VerifiedObjective!,result.Objective)>0:ExactValues.Compare(m.VerifiedObjective!,result.Objective)<0),"I4 "+Ctx());
            // I5 an honest optimal member that completed verification forces the optimum
            if(result.Members.Any(m=>m.Assessment is {Rejected:false}&&members.Single(s=>s.Profile.Manifest.Solver==m.Solver).Behaviour==Behaviour.HonestOptimal))
                Assert.Equal(opt!.Value.ToString(CultureInfo.InvariantCulture),result.Objective);
            // I6 proofs are true
            if(result.Status==PortfolioStatus.ProvenOptimal)Assert.Equal(opt!.Value.ToString(CultureInfo.InvariantCulture),result.Objective);
            if(result.Status==PortfolioStatus.ProvenInfeasible)Assert.Null(opt);
            // I7 no orphaned executions: only abandoned members may still be running
            Assert.True(probe.Live<=result.Members.Count(m=>m.Phase==MemberPhase.Abandoned),"I7 "+Ctx());
            // I8 a surviving false optimality attestation is always flagged
            foreach(var m in result.Members.Where(m=>m.Assessment is {Rejected:false,ReportedStatus:MathematicalStatus.Optimal}&&m.VerifiedObjective is not null))
                if(result.Objective is not null&&ExactValues.Compare(m.VerifiedObjective!,result.Objective)!=0)
                    Assert.Contains(result.Alerts,a=>a.Code=="portfolio.falsifiedOptimality"&&a.Solver==m.Solver);
            // I9 a falsified bound is never reported as the portfolio bound
            if(result.BoundSource is {} src)Assert.DoesNotContain(result.Alerts,a=>a.Solver==src&&a.Code.StartsWith("portfolio.falsified",StringComparison.Ordinal));

            // I10 stopping is governed by the rule and never manufactures proof
            if(policy.Stop==StopRule.ProofOnly)Assert.True(result.StopReason is StopReason.Completed or StopReason.Proof,"I10 "+Ctx());
            if(policy.Stop==StopRule.Concordant)Assert.NotEqual(StopReason.SingleAttestation,result.StopReason);
            if(result.Status==PortfolioStatus.ProvenOptimal)Assert.Contains(result.Members,m=>m.Assessment is {Rejected:false,Strength:EvidenceStrength.ExactEnumeration});
            if(result.StopReason==StopReason.Concordant)Assert.True(result.Members.Where(m=>m.Assessment is {Rejected:false,ReportedStatus:MathematicalStatus.Optimal}).Select(m=>m.Family).Distinct().Count()>=2,"I10c "+Ctx());
            Count("stop."+result.StopReason);Count("rule."+policy.Stop);if(result.Escalated)Count("escalated");
            Count("status."+result.Status);Count("accepted."+result.Accepted);Count("mode."+policy.Mode);
            Count("members",count);Count("membersStarted",probe.Started);
            Count("alerts",result.Alerts.Length);foreach(var a in result.Alerts)Count("alert."+a.Code);
            Count("overspendCoreMs",result.Ledger.OverspendCoreMs);Count("chargedCoreMs",result.Ledger.ChargedCoreMs);
            if(result.Strength==EvidenceStrength.ExactEnumeration)Count("strength.exact");
        }
        stats["cases"]=cases;stats["elapsedMs"]=clock.ElapsedMilliseconds;
        var json=JsonSerializer.Serialize(stats.OrderBy(k=>k.Key).ToDictionary(k=>k.Key,k=>k.Value),new JsonSerializerOptions{WriteIndented=true});
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"portfolio-randomized-summary.json"),json);
        output.WriteLine(json);
    }
}
