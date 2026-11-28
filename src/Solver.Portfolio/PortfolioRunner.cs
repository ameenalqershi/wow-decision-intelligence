using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using WowDi.Contracts;
using WowDi.Runtime.Governance;
using WowDi.Solver.Contracts;
using WowDi.Solver.Planning;
using WowDi.Udir;
using WowDi.Verification.Bounds;
using WowDi.Verification.Independent;

namespace WowDi.Solver.Portfolio;

/// <summary>Budgeted solver portfolio (S08). Members are admitted by S06 planning, scheduled under one core-millisecond,
/// thread and memory ledger, and every candidate is classified by the independent S05 verifier before it can be selected.
/// Selection never prefers a member for being fast: verified objective first, then evidence strength, then the
/// deterministic rank. Solver attestations are cross-checked against other members' verified incumbents.</summary>
public static class PortfolioRunner
{
    public const int MaximumMembers=64;

    private sealed class Run(IPortfolioMember member,AdapterProfile profile,int rank,PlannedExecution plan)
    {
        internal IPortfolioMember Member=member;internal AdapterProfile Profile=profile;internal int Rank=rank;
        internal PlannedExecution Plan=plan;internal MemberRecord? Record;
        internal readonly CancellationTokenSource Stop=new();
    }

    public static async Task<PortfolioResult> RunAsync(PortfolioRequest request,IEnumerable<IPortfolioMember> members,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(request);ArgumentNullException.ThrowIfNull(members);
        Validate(request);
        var clock=Stopwatch.StartNew();
        var catalog=members.Take(MaximumMembers+1).ToArray();
        if(catalog.Length>MaximumMembers||catalog.Any(m=>m?.Profile?.Manifest is null))throw new ContractException("portfolio.catalog","Invalid member catalog.");
        if(catalog.Select(m=>m.Profile.Manifest.Solver).Distinct().Count()!=catalog.Length)
            throw new ContractException("portfolio.catalog","Duplicate member identity.");
        var b=request.Budget;var policy=request.Policy;
        var facts=request.FactsBytes is {} f?(ReadOnlyMemory<byte>?)new ReadOnlyMemory<byte>(f.ToArray()):null;
        var catalogBytes=request.CatalogBytes.ToArray();

        // 1. Admission: S06 planning per member (capability, numeric encoding, interruption, minimum resources, exact lowering).
        var planningRequest=new PlanningRequest(request.RuntimeId,new WorkerBudget(b.MaxConcurrentThreads,b.MemoryBytes,b.WallDeadline),
            request.NumericRequirement);
        var features=SolverPlanner.Plan(request.Model,planningRequest,[],facts,ct).Features;
        var preferred=features.IntegralVariables==features.Variables?SolverFamily.CpSat:SolverFamily.Highs;
        var ordered=catalog.OrderBy(m=>m.Profile.Family==preferred?0:1)
            .ThenBy(m=>m.Profile.Manifest.Solver.SolverId,StringComparer.Ordinal)
            .ThenBy(m=>m.Profile.Manifest.Solver.Version,StringComparer.Ordinal)
            .ThenBy(m=>m.Profile.Manifest.Solver.RuntimeId,StringComparer.Ordinal).ToArray();
        var runs=new List<Run>();var records=new List<MemberRecord>();
        for(int i=0;i<ordered.Length;i++)
        {
            var m=ordered[i];
            var plan=SolverPlanner.Plan(request.Model,planningRequest,[m.Profile],facts,ct);
            if(plan.Selection is null)
                records.Add(Empty(m.Profile,i,MemberPhase.Ineligible,plan.Assessments.SingleOrDefault()?.Code??"plan.ineligible"));
            else runs.Add(new(m,m.Profile,i,plan.Selection));
        }

        // 2. Verification context shared by every candidate.
        var document=request.Model.Original;
        var modelSha=Sha(document.Write());
        var factsSha=facts is {} fb?Sha(fb.ToArray()):null;
        var catalogSha=Sha(catalogBytes);

        var ledger=new CoreLedger(b.CoreMilliseconds,b.MaxConcurrentThreads,b.MemoryBytes);
        var alerts=new ConcurrentQueue<PortfolioAlert>();
        using var all=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var wallLimit=b.WallDeadline;

        async Task Execute(Run run,AllocationSlot slot)
        {
            var reservation=ledger.TryReserve(slot.Threads,slot.MemoryBytes,slot.HardDeadline);
            if(reservation is null){run.Record=Empty(run.Profile,run.Rank,MemberPhase.NotScheduled,"portfolio.budget");return;}
            var assignment=new MemberAssignment(request.RunId+".m"+run.Rank.ToString(System.Globalization.CultureInfo.InvariantCulture),
                slot.Threads,slot.MemoryBytes,slot.HardDeadline,slot.SoftLimit,run.Plan with{Budget=new(slot.Threads,slot.MemoryBytes,slot.HardDeadline)});
            var started=clock.ElapsedMilliseconds;
            using var member=CancellationTokenSource.CreateLinkedTokenSource(all.Token,run.Stop.Token);
            member.CancelAfter(slot.HardDeadline);
            Task<MemberExecution> task;
            try{task=run.Member.ExecuteAsync(assignment,member.Token);}
            catch(Exception ex) when(ex is not OutOfMemoryException){task=Task.FromException<MemberExecution>(ex);}
            // Watchdog: hard deadline plus cleanup grace. A member that does not return is abandoned and its
            // reservation stays quarantined; it can never contribute a candidate.
            var watchdog=Task.Delay(slot.HardDeadline+policy.Cleanup,CancellationToken.None);
            var finished=await Task.WhenAny(task,watchdog).ConfigureAwait(false);
            var elapsedFallback=clock.ElapsedMilliseconds-started;
            if(finished!=task)
            {
                member.Cancel();
                ledger.Settle(reservation,reservation.CoreMs,cleanupConfirmed:false);
                alerts.Enqueue(new("portfolio.abandoned",run.Profile.Manifest.Solver,null,"Member did not return after its hard deadline and cleanup grace."));
                run.Record=Empty(run.Profile,run.Rank,MemberPhase.Abandoned,"portfolio.abandoned") with
                    {Assignment=assignment,ReservedCoreMs=reservation.CoreMs,ChargedCoreMs=reservation.CoreMs,ElapsedMs=elapsedFallback,
                     StartedAtMs=started,FinishedAtMs=clock.ElapsedMilliseconds};
                return;
            }
            MemberExecution exec;
            try{exec=await task.ConfigureAwait(false);}
            catch(OperationCanceledException){exec=new(member.IsCancellationRequested&&!all.IsCancellationRequested&&!run.Stop.IsCancellationRequested
                ?Termination.Timeout:Termination.Cancelled,null,elapsedFallback,null,null,false,"portfolio.memberCancelled",CpuSource.ReservationBound);}
            catch(Exception ex) when(ex is not OutOfMemoryException)
            {exec=new(Termination.Failed,null,elapsedFallback,null,null,false,"portfolio.memberFault:"+ex.GetType().Name,CpuSource.ReservationBound);}
            exec??=new(Termination.Failed,null,elapsedFallback,null,null,false,"portfolio.nullExecution",CpuSource.ReservationBound);
            // Charge: boundary measurement when present, otherwise the conservative bound threads x elapsed (cgroup quota).
            var source=exec.CpuMs is null?CpuSource.ReservationBound:exec.CpuSource;
            var actual=exec.CpuMs??checked(slot.Threads*Math.Max(0,exec.ElapsedMs));
            var (charged,over)=ledger.Settle(reservation,actual,exec.CleanupConfirmed);
            if(!exec.CleanupConfirmed)
                alerts.Enqueue(new("portfolio.cleanupUnconfirmed",run.Profile.Manifest.Solver,null,"Resources quarantined; candidate discarded."));
            BoundAssessment? assessment=null;string? objective=null;string? diagnostic=exec.Diagnostic;
            if(exec.Result is {} result)
            {
                if(!exec.CleanupConfirmed)diagnostic??="portfolio.cleanupUnconfirmed";
                else if(exec.Termination!=Termination.Completed)diagnostic??="portfolio.candidateWithoutCompletion";
                else
                {
                    var binding=new EvidenceBinding(new VerificationScope(document.Model.TenantId,document.Model.ModelId,
                        document.Model.RevisionId,assignment.RunId,modelSha),factsSha,catalogSha);
                    assessment=EvidenceClassifier.Assess(document,binding,catalogBytes,facts,new BoundObservation(binding,result),
                        run.Profile.Manifest,policy.Enumerate,ct:CancellationToken.None);
                    if(assessment.Rejected)
                        alerts.Enqueue(new("portfolio.verificationFailed",run.Profile.Manifest.Solver,null,assessment.Diagnostic??"rejected"));
                    else objective=assessment.Incumbent;
                }
            }
            run.Record=new(run.Profile.Manifest.Solver,run.Profile.Family,run.Rank,MemberPhase.Completed,diagnostic??"portfolio.completed",
                assignment,exec.Termination,reservation.CoreMs,charged,over,exec.CpuMs,source,exec.ElapsedMs,exec.PeakBytes,exec.CleanupConfirmed,
                diagnostic,assessment,objective,started,clock.ElapsedMilliseconds);
            // Early stop: an exhaustive proof cannot be improved. An attested optimum may stop others after a cross-check grace.
            if(assessment is {Rejected:false} a)
            {
                if(a.Status is MathematicalStatus.Optimal or MathematicalStatus.Infeasible&&a.Strength==EvidenceStrength.ExactEnumeration)
                    StopOthers(run,TimeSpan.Zero);
                else if(policy.AttestedStopGrace is {} grace&&a.ReportedStatus==MathematicalStatus.Optimal&&a.Incumbent is not null&&a.AbsoluteGap=="0")
                    StopOthers(run,grace);
            }
        }
        void StopOthers(Run winner,TimeSpan grace)
        {
            foreach(var r in runs)if(!ReferenceEquals(r,winner))
            {
                try{if(grace<=TimeSpan.Zero)r.Stop.Cancel();else r.Stop.CancelAfter(grace);}
                catch(ObjectDisposedException){}
            }
        }

        // 3. Schedule.
        try
        {
            if(policy.Mode==PortfolioMode.Race)
            {
                var slots=Allocation.Race(runs.Select(r=>r.Profile).ToArray(),features,b,policy,ledger.Available,ledger.FreeThreads,ledger.FreeMemory);
                for(int i=slots.Count;i<runs.Count;i++)runs[i].Record=Empty(runs[i].Profile,runs[i].Rank,MemberPhase.NotScheduled,"portfolio.capacity");
                await Task.WhenAll(slots.Select(s=>Execute(runs[s.Rank],s))).ConfigureAwait(false);
            }
            else
            {
                for(int i=0;i<runs.Count;i++)
                {
                    var r=runs[i];
                    if(all.IsCancellationRequested||r.Stop.IsCancellationRequested)
                    {r.Record=Empty(r.Profile,r.Rank,MemberPhase.NotScheduled,"portfolio.stopped");continue;}
                    var left=wallLimit-clock.Elapsed;
                    var slot=Allocation.Stage(r.Profile,i,runs.Count-i,features,policy,ledger.Available,ledger.FreeThreads,ledger.FreeMemory,left);
                    if(slot is null){r.Record=Empty(r.Profile,r.Rank,MemberPhase.NotScheduled,"portfolio.budget");continue;}
                    await Execute(r,slot).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            foreach(var r in runs)r.Stop.Dispose();
        }
        records.AddRange(runs.Select(r=>r.Record??Empty(r.Profile,r.Rank,MemberPhase.NotScheduled,"portfolio.unscheduled")));
        records.Sort((x,y)=>x.Rank.CompareTo(y.Rank));
        return Conclude(request,features,records,alerts,ledger,ct.IsCancellationRequested,clock.ElapsedMilliseconds);
    }

    private static PortfolioResult Conclude(PortfolioRequest request,ModelFeatures features,List<MemberRecord> records,
        ConcurrentQueue<PortfolioAlert> alerts,CoreLedger ledger,bool cancelled,long wall)
    {
        var objective=request.Model.Original.Model.Objectives.SingleOrDefault();
        var direction=objective?.Direction??Direction.Minimize;
        bool Better(string a,string c)=>direction==Direction.Minimize?ExactValues.Compare(a,c)<0:ExactValues.Compare(a,c)>0;
        var verified=records.Where(r=>r.VerifiedObjective is not null&&r.Assessment is {Rejected:false}).ToArray();
        // Cross-checks: a verified incumbent strictly better than another member's attested optimum or bound falsifies it.
        var falsified=new HashSet<SolverIdentity>();
        foreach(var a in records.Where(r=>r.Assessment is {Rejected:false}))
        {
            var asmt=a.Assessment!;
            foreach(var w in verified)
            {
                if(w.Solver==a.Solver)continue;
                if(asmt.ReportedStatus==MathematicalStatus.Optimal&&a.VerifiedObjective is {} va&&Better(w.VerifiedObjective!,va))
                {falsified.Add(a.Solver);alerts.Enqueue(new("portfolio.falsifiedOptimality",a.Solver,w.Solver,$"attested {va}, verified {w.VerifiedObjective}"));}
                else if(asmt.Bound is {} bound&&asmt.BoundStrength==EvidenceStrength.SolverAttestation&&Better(w.VerifiedObjective!,bound))
                {falsified.Add(a.Solver);alerts.Enqueue(new("portfolio.falsifiedBound",a.Solver,w.Solver,$"bound {bound}, verified {w.VerifiedObjective}"));}
                if(asmt.ReportedStatus==MathematicalStatus.Infeasible)
                {falsified.Add(a.Solver);alerts.Enqueue(new("portfolio.falsifiedInfeasibility",a.Solver,w.Solver,"verified feasible incumbent exists"));}
            }
        }
        // Selection: verified objective, then evidence strength, then rank. Elapsed time is not a criterion.
        MemberRecord? selected=null;
        foreach(var r in verified.OrderBy(r=>r.Rank))
        {
            if(selected is null||Better(r.VerifiedObjective!,selected.VerifiedObjective!)||
               ExactValues.Compare(r.VerifiedObjective!,selected.VerifiedObjective!)==0&&r.Assessment!.Strength>selected.Assessment!.Strength)
                selected=r;
        }
        // Best bound from non-falsified members; exhaustive proofs dominate attestations.
        string? bestBound=null;var boundStrength=EvidenceStrength.None;SolverIdentity? boundSource=null;
        foreach(var r in records.Where(r=>r.Assessment is {Rejected:false,Bound:not null}&&!falsified.Contains(r.Solver)).OrderBy(r=>r.Rank))
        {
            var a=r.Assessment!;
            var tighter=bestBound is null||(direction==Direction.Minimize?ExactValues.Compare(a.Bound!,bestBound)>0:ExactValues.Compare(a.Bound!,bestBound)<0);
            if(a.BoundStrength>boundStrength||a.BoundStrength==boundStrength&&tighter)
            {bestBound=a.Bound;boundStrength=a.BoundStrength;boundSource=r.Solver;}
        }
        var status=PortfolioStatus.Unknown;
        if(selected is not null)
            status=selected.Assessment!.Status==MathematicalStatus.Optimal&&selected.Assessment.Strength==EvidenceStrength.ExactEnumeration
                ?PortfolioStatus.ProvenOptimal:PortfolioStatus.VerifiedFeasible;
        else if(records.Any(r=>r.Assessment is {Rejected:false,Status:MathematicalStatus.Infeasible,Strength:EvidenceStrength.ExactEnumeration}))
            status=PortfolioStatus.ProvenInfeasible;
        if(cancelled)alerts.Enqueue(new("portfolio.cancelled",new("portfolio","1","host"),null,"Host cancelled the portfolio run."));
        string? gap=null;
        if(selected?.VerifiedObjective is {} obj&&bestBound is not null)
        {
            var d=ExactValues.Compare(obj,bestBound)>=0?Subtract(obj,bestBound):Subtract(bestBound,obj);
            gap=d;
        }
        var concordant=selected is null?0:verified.Count(r=>r.Assessment!.ReportedStatus==MathematicalStatus.Optimal&&!falsified.Contains(r.Solver)&&
            ExactValues.Compare(r.VerifiedObjective!,selected.VerifiedObjective!)==0);
        var strength=selected?.Assessment!.Strength??(status==PortfolioStatus.ProvenInfeasible?EvidenceStrength.ExactEnumeration:EvidenceStrength.None);
        return new(request.RunId,features,cancelled&&status!=PortfolioStatus.ProvenInfeasible?PortfolioStatus.Unknown:status,
            selected is not null&&!cancelled,cancelled?null:selected,cancelled?null:selected?.VerifiedObjective,cancelled?EvidenceStrength.None:strength,
            bestBound,boundStrength,boundSource,cancelled?null:gap,cancelled?0:concordant,records.ToImmutableArray(),
            alerts.DistinctBy(a=>(a.Code,a.Solver,a.Witness)).ToImmutableArray(),ledger.Totals(),wall);
    }

    private static string Subtract(string a,string b)
    {
        static (System.Numerics.BigInteger N,System.Numerics.BigInteger D) P(string s)
        {var p=s.Split('/');return (System.Numerics.BigInteger.Parse(p[0],System.Globalization.CultureInfo.InvariantCulture),
            p.Length==1?System.Numerics.BigInteger.One:System.Numerics.BigInteger.Parse(p[1],System.Globalization.CultureInfo.InvariantCulture));}
        var x=P(a);var y=P(b);
        var n=x.N*y.D-y.N*x.D;var d=x.D*y.D;var g=System.Numerics.BigInteger.GreatestCommonDivisor(n,d);
        if(!g.IsZero){n/=g;d/=g;}
        return d.IsOne?n.ToString(System.Globalization.CultureInfo.InvariantCulture):FormattableString.Invariant($"{n}/{d}");
    }

    private static MemberRecord Empty(AdapterProfile p,int rank,MemberPhase phase,string code)=>
        new(p.Manifest.Solver,p.Family,rank,phase,code,null,null,0,0,0,null,null,0,null,true,null,null,null,0,0);

    private static void Validate(PortfolioRequest r)
    {
        var b=r.Budget;var p=r.Policy;
        if(string.IsNullOrWhiteSpace(r.RunId)||r.RunId.Length>200||r.Model is null||b is null||p is null||
           b.CoreMilliseconds<1||b.MaxConcurrentThreads is <1 or >4096||b.MemoryBytes<1||
           b.WallDeadline<=TimeSpan.Zero||b.WallDeadline>TimeSpan.FromDays(7)||
           !Enum.IsDefined(p.Mode)||!(p.SoftLimitFraction is >0 and <1)||p.Grace<TimeSpan.Zero||p.Cleanup<TimeSpan.Zero||
           p.SmallModelVariables<0||p.AttestedStopGrace<TimeSpan.Zero||r.CatalogBytes.Length is <1 or >262144)
            throw new ContractException("portfolio.request","Invalid portfolio request.");
    }

    private static string Sha(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
