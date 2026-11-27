using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using WowDi.DecisionGraph;
using WowDi.ModelRegistry;
using WowDi.Solver.Contracts;
using WowDi.Udir;
using WowDi.Verification.Bounds;
using WowDi.Verification.Evidence;
using Xunit;
namespace WowDi.DecisionLifecycle.Tests;

public sealed class LifecycleTests
{
    private static readonly DecisionKey Key=new("tenant-a","factory","r17");
    private static readonly LifecycleActor Creator=new(Key.TenantId,"fixture-issuer","creator");
    private static readonly LifecycleActor Approver=new(Key.TenantId,"fixture-issuer","approver");
    private static readonly LifecycleActor Executor=new(Key.TenantId,"fixture-issuer","executor");
    private static readonly DateTimeOffset Now=DateTimeOffset.UnixEpoch.AddDays(20000);
    private static readonly LifecyclePolicy Disabled=new("policy","1",true,false,false,ImmutableHashSet.Create(StringComparer.Ordinal,"target"));
    private static readonly LifecyclePolicy Enabled=Disabled with{ExternalExecutionEnabled=true,SensitiveExecutionEnabled=true};
    private static readonly ImmutableHashSet<LifecyclePermission> All=Enum.GetValues<LifecyclePermission>().ToImmutableHashSet();
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static TransitionAuthority Authority(EvidenceRecord? e=null,LifecycleActor? actor=null,LifecyclePolicy? policy=null,
        bool current=true,DateTimeOffset? now=null,ImmutableHashSet<LifecyclePermission>? permissions=null)=>
        new(actor??Creator,permissions??All,current,policy??Disabled,e,now??Now);
    private static TransitionRequest Request(DecisionSnapshot s,DecisionState target)=>new(s.State,s.Sequence,target,"cause","correlation");
    private static DecisionSnapshot Step(DecisionSnapshot s,DecisionState target,TransitionAuthority a)=>
        DecisionStateMachine.Apply(s,Request(s,target),a).Next;
    private static EvidenceRecord Evidence(string tenant="tenant-a",string? assignment="8",string? cost="56/3",
        MathematicalStatus status=MathematicalStatus.Optimal,Termination termination=Termination.Completed,bool enumerate=true)
    {
        var d=UdirDocument.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","factory.json")));
        var facts=Encoding.UTF8.GetBytes("""{"available":"8"}""");
        var catalog=Encoding.UTF8.GetBytes("""{"units":[{"symbol":"minute","dimension":"time","scale":"60"}],"currencies":["USD"],"calendars":["shift-a"]}""");
        var source=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","factory.txt"));
        var doc=UdirDocument.Create(d.Model with{TenantId=tenant,Scenarios=[],Requires=["integer.linear"],InputDigests=[Hash(facts)]});
        var revision=new StoredRevision(doc,DecisionGraphSnapshot.Create(new(tenant,"factory","r17"),[],[]),
            ImmutableDictionary<string,ImmutableArray<byte>>.Empty.Add(Hash(facts),ImmutableArray.CreateRange(facts)).Add(Hash(source),ImmutableArray.CreateRange(source)));
        var b=new EvidenceBinding(new(tenant,"factory","r17","run",Hash(doc.Write())),Hash(facts),Hash(catalog));
        var solver=new SolverIdentity("fixture","1","managed");
        var caps=new SolverCapabilities(solver,["integer.linear"],[ScalarKind.Integer],null,null,1,false,true,true);
        var result=new SolverResult(tenant,"factory","r17","run",solver,status,termination,
            assignment is null?null:ImmutableDictionary<string,string>.Empty.Add("x",assignment),
            [new("revenue",cost,null,"0","0")],new(EvidenceSource.SolverAttestation,"fixture"),new(100,75,null));
        var invocation=new RunInvocation("attempt",new string('a',64),"compiler","planner","profile",new string('b',64),
            "fixture","hardware",2,1024*1024*1024,1,128*1024*1024,30000,1,Now.AddSeconds(-1),Now,enumerate,100);
        return EvidenceRecord.Capture(revision,b,invocation,catalog,caps,result,"{}"u8.ToArray());
    }
    private static DecisionSnapshot Solved(EvidenceRecord e)
    {
        var s=DecisionStateMachine.Create(Key,Now);
        foreach(var t in new[]{DecisionState.Validated,DecisionState.Compiled,DecisionState.Queued,DecisionState.Solving,DecisionState.Solved})
            s=Step(s,t,Authority(e));
        return s;
    }
    private static DecisionSnapshot Proposed(EvidenceRecord e,LifecyclePolicy policy)
    {
        var verified=Step(Solved(e),DecisionState.Verified,Authority(e));
        return DecisionStateMachine.Apply(verified,Request(verified,DecisionState.Proposed) with
        {Proposal=new("target",new string('c',64),Now.AddHours(1))},Authority(e,policy:policy)).Next;
    }
    private static DecisionSnapshot Approved(EvidenceRecord e,LifecyclePolicy policy)
    {
        var p=Proposed(e,policy);
        return DecisionStateMachine.Apply(p,Request(p,DecisionState.Approved) with{ProposalDigest=p.Proposal!.Digest},Authority(e,Approver,policy)).Next;
    }
    private static void Denied(string code,Action action)=>Assert.Equal(code,Assert.Throws<LifecycleRejectedException>(action).Code);

    [Fact]
    public void ExhaustiveStateTableMatchesSpecificationAndEveryForbiddenEdgeRejects()
    {
        var expected=new[]{"Draft:Validated","Draft:Cancelled","Validated:Compiled","Validated:Failed","Validated:Cancelled",
            "Compiled:Queued","Compiled:Cancelled","Queued:Solving","Queued:Cancelled","Queued:Failed",
            "Solving:Solved","Solving:Failed","Solving:Cancelled","Solved:Verified","Solved:Failed","Solved:Superseded",
            "Verified:Proposed","Verified:Superseded","Proposed:Approved","Proposed:Rejected","Proposed:Superseded",
            "Approved:Executing","Approved:Superseded","Executing:Executed","Executing:Failed","Executed:Monitoring","Monitoring:Superseded"}.ToHashSet(StringComparer.Ordinal);
        var states=Enum.GetValues<DecisionState>();
        foreach(var from in states)foreach(var to in states)
        {
            var allowed=expected.Contains(from+":"+to);
            Assert.Equal(allowed,DecisionStateMachine.Allows(from,to));
            if(!allowed)
            {
                var s=DecisionStateMachine.Create(Key,Now) with{State=from};
                Denied("lifecycle.transition",()=>DecisionStateMachine.Apply(s,Request(s,to),Authority()));
            }
        }
        Assert.Equal(27,expected.Count);
    }

    [Fact]
    public void SensitiveApprovalAndExecutionBindExactEvidencePolicyTargetAndActors()
    {
        var e=Evidence();var p=Proposed(e,Enabled);
        var request=Request(p,DecisionState.Approved) with{ProposalDigest=p.Proposal!.Digest};
        Denied("lifecycle.separation",()=>DecisionStateMachine.Apply(p,request,Authority(e,Creator,Enabled)));
        Denied("lifecycle.denied",()=>DecisionStateMachine.Apply(p,request,Authority(e,Approver,Enabled,permissions:ImmutableHashSet<LifecyclePermission>.Empty)));
        Denied("lifecycle.proposalDigest",()=>DecisionStateMachine.Apply(p,request with{ProposalDigest=new string('d',64)},Authority(e,Approver,Enabled)));
        Denied("lifecycle.staleProposal",()=>DecisionStateMachine.Apply(p,request,Authority(e,Approver,Enabled with{Version="2"})));
        Denied("lifecycle.staleProposal",()=>DecisionStateMachine.Apply(p,request,Authority(e,Approver,Enabled with{ExternalExecutionEnabled=false})));
        Denied("lifecycle.staleProposal",()=>DecisionStateMachine.Apply(p,request,Authority(e,Approver,Enabled,now:Now.AddHours(1))));
        Denied("lifecycle.stale",()=>DecisionStateMachine.Apply(p,request,Authority(e,Approver,Enabled,current:false)));
        var approved=DecisionStateMachine.Apply(p,request,Authority(e,Approver,Enabled)).Next;
        var execution=Request(approved,DecisionState.Executing) with{ApprovalDigest=approved.Approval!.Digest};
        Denied("lifecycle.approval",()=>DecisionStateMachine.Apply(approved,execution with{ApprovalDigest=new string('d',64)},Authority(e,Executor,Enabled)));
        var started=DecisionStateMachine.Apply(approved,execution,Authority(e,Executor,Enabled));
        Assert.Equal(DecisionState.Executing,started.Next.State);
        Assert.Equal(started.Next.ExecutionId,started.Audit.ExecutionId);
        Assert.Equal(started.Next.Approval!.Digest,started.Audit.ApprovalDigest);
        Assert.Equal(approved.Sequence+1,started.Next.Sequence);
        var unknown=Request(started.Next,DecisionState.Executed) with{Receipt=new(started.Next.ExecutionId!,ExternalDisposition.Unknown,new string('d',64))};
        Denied("lifecycle.ambiguousExecution",()=>DecisionStateMachine.Apply(started.Next,unknown,Authority(e,Executor,Enabled)));
        var confirmed=unknown with{Receipt=unknown.Receipt! with{Disposition=ExternalDisposition.ConfirmedExecuted}};
        var executed=DecisionStateMachine.Apply(started.Next,confirmed,Authority(e,Executor,Enabled)).Next;
        Assert.Equal(DecisionState.Executed,executed.State);
        Assert.Equal(DecisionState.Monitoring,Step(executed,DecisionState.Monitoring,Authority(e,Executor,Enabled)).State);
    }

    [Fact]
    public void DefaultSensitivePolicyNeverEnablesExternalExecution()
    {
        var e=Evidence();var a=Approved(e,Disabled);
        var request=Request(a,DecisionState.Executing) with{ApprovalDigest=a.Approval!.Digest};
        Denied("lifecycle.executionDisabled",()=>DecisionStateMachine.Apply(a,request,Authority(e,Executor,Disabled)));
        Denied("lifecycle.staleProposal",()=>DecisionStateMachine.Apply(a,request,Authority(e,Executor,Enabled)));
    }

    [Fact]
    public void InvalidUnknownCancelledAndForeignEvidenceCannotBecomeProposal()
    {
        var invalid=Evidence(assignment:"9",cost:"21");
        Denied("lifecycle.unverified",()=>Step(Solved(invalid),DecisionState.Verified,Authority(invalid)));
        var unknown=Evidence(assignment:null,cost:null,status:MathematicalStatus.Unknown,enumerate:false);
        Denied("lifecycle.unverified",()=>Step(Solved(unknown),DecisionState.Verified,Authority(unknown)));
        var foreign=Evidence("tenant-b");
        Denied("lifecycle.evidenceScope",()=>Solved(foreign));
        var cancelled=Evidence(termination:Termination.Cancelled);
        var s=Step(Solved(cancelled),DecisionState.Verified,Authority(cancelled));
        var r=Request(s,DecisionState.Proposed) with{Proposal=new("target",new string('c',64),Now.AddHours(1))};
        Denied("lifecycle.notFeasible",()=>DecisionStateMachine.Apply(s,r,Authority(cancelled)));
    }

    [Fact]
    public void ExpectedVersionScopeAndSupersessionFailClosed()
    {
        var s=DecisionStateMachine.Create(Key,Now);
        var r=Request(s,DecisionState.Validated);
        Denied("lifecycle.conflict",()=>DecisionStateMachine.Apply(s,r with{ExpectedSequence=1},Authority()));
        Denied("lifecycle.scope",()=>DecisionStateMachine.Apply(s,r,Authority(actor:Creator with{TenantId="tenant-b"})));
        Denied("lifecycle.denied",()=>DecisionStateMachine.Apply(s,r,Authority(permissions:ImmutableHashSet.Create(LifecyclePermission.Approve))));
        var e=Evidence();var p=Proposed(e,Disabled);
        Denied("lifecycle.notSuperseded",()=>Step(p,DecisionState.Superseded,Authority(e)));
        Assert.Equal(DecisionState.Superseded,Step(p,DecisionState.Superseded,Authority(e,current:false)).State);
    }
}
