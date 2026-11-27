using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using WowDi.Solver.Contracts;
using WowDi.Verification.Bounds;
using WowDi.Verification.Evidence;

namespace WowDi.DecisionLifecycle;

public enum DecisionState { Draft,Validated,Compiled,Queued,Solving,Solved,Verified,Proposed,Approved,Executing,Executed,Monitoring,Rejected,Superseded,Failed,Cancelled }
public enum LifecyclePermission { Write,Propose,Approve,Execute }
public enum ExternalDisposition { ConfirmedExecuted,ConfirmedNotExecuted,Unknown }
public sealed record DecisionKey(string TenantId,string ModelId,string RevisionId);
public sealed record LifecycleActor(string TenantId,string Issuer,string Subject);
public sealed record LifecyclePolicy(string PolicyId,string Version,bool Sensitive,bool ExternalExecutionEnabled,
    bool SensitiveExecutionEnabled,ImmutableHashSet<string> AllowedTargets);
public sealed record ProposalInput(string TargetId,string PayloadSha256,DateTimeOffset ExpiresAt);
public sealed record DecisionProposal(string EvidenceId,string TargetId,string PayloadSha256,string PolicyId,string PolicyVersion,string PolicyDigest,
    bool Sensitive,LifecycleActor Creator,DateTimeOffset ExpiresAt,string Digest);
public sealed record DecisionApproval(string ProposalDigest,LifecycleActor Approver,DateTimeOffset ApprovedAt,DateTimeOffset ExpiresAt,string Digest);
public sealed record ExternalReceipt(string ExecutionId,ExternalDisposition Disposition,string ReceiptSha256);
public sealed record DecisionSnapshot(DecisionKey Key,DecisionState State,long Sequence,string? EvidenceId,
    DecisionProposal? Proposal,DecisionApproval? Approval,string? ExecutionId,string? ReceiptSha256,DateTimeOffset ChangedAt);
public sealed record TransitionRequest(DecisionState ExpectedState,long ExpectedSequence,DecisionState Target,string CausationId,
    string CorrelationId,ProposalInput? Proposal=null,string? ProposalDigest=null,string? ApprovalDigest=null,ExternalReceipt? Receipt=null);
// Host-owned, freshly authorized context. The application must never bind this record from client JSON.
public sealed record TransitionAuthority(LifecycleActor Actor,ImmutableHashSet<LifecyclePermission> Permissions,
    bool IsCurrentRevision,LifecyclePolicy Policy,EvidenceRecord? Evidence,DateTimeOffset Now);
public sealed record DecisionAudit(DecisionKey Key,long Sequence,DecisionState From,DecisionState To,LifecycleActor Actor,
    DateTimeOffset At,string CausationId,string CorrelationId,string? EvidenceId,string? ProposalDigest,string? ApprovalDigest,
    string? ExecutionId,string? ReceiptSha256);
public sealed record DecisionTransition(DecisionSnapshot Next,DecisionAudit Audit);
public sealed class LifecycleRejectedException(string code):Exception(code){public string Code{get;}=code;}

public static class DecisionStateMachine
{
    private static readonly ImmutableDictionary<DecisionState,ImmutableHashSet<DecisionState>> Table=
        new Dictionary<DecisionState,DecisionState[]>
        {
            [DecisionState.Draft]=[DecisionState.Validated,DecisionState.Cancelled],
            [DecisionState.Validated]=[DecisionState.Compiled,DecisionState.Failed,DecisionState.Cancelled],
            [DecisionState.Compiled]=[DecisionState.Queued,DecisionState.Cancelled],
            [DecisionState.Queued]=[DecisionState.Solving,DecisionState.Cancelled,DecisionState.Failed],
            [DecisionState.Solving]=[DecisionState.Solved,DecisionState.Failed,DecisionState.Cancelled],
            [DecisionState.Solved]=[DecisionState.Verified,DecisionState.Failed,DecisionState.Superseded],
            [DecisionState.Verified]=[DecisionState.Proposed,DecisionState.Superseded],
            [DecisionState.Proposed]=[DecisionState.Approved,DecisionState.Rejected,DecisionState.Superseded],
            [DecisionState.Approved]=[DecisionState.Executing,DecisionState.Superseded],
            [DecisionState.Executing]=[DecisionState.Executed,DecisionState.Failed],
            [DecisionState.Executed]=[DecisionState.Monitoring],
            [DecisionState.Monitoring]=[DecisionState.Superseded]
        }.ToImmutableDictionary(p=>p.Key,p=>p.Value.ToImmutableHashSet());

    public static bool Allows(DecisionState from,DecisionState to)=>Table.TryGetValue(from,out var set)&&set.Contains(to);
    public static DecisionSnapshot Create(DecisionKey key,DateTimeOffset utc)
    {
        Require(Valid(key.TenantId)&&Valid(key.ModelId)&&Valid(key.RevisionId)&&utc.Offset==TimeSpan.Zero,"lifecycle.scope");
        return new(key,DecisionState.Draft,0,null,null,null,null,null,utc);
    }

    public static DecisionTransition Apply(DecisionSnapshot current,TransitionRequest request,TransitionAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(current);ArgumentNullException.ThrowIfNull(request);ArgumentNullException.ThrowIfNull(authority);
        var key=current.Key;var actor=authority.Actor;var now=authority.Now;var target=request.Target;var policy=authority.Policy;
        Require(Valid(key.TenantId)&&Valid(key.ModelId)&&Valid(key.RevisionId)&&actor.TenantId==key.TenantId&&Valid(actor.Issuer)&&Valid(actor.Subject),"lifecycle.scope");
        Require(now.Offset==TimeSpan.Zero&&now>=current.ChangedAt&&current.Sequence>=0&&current.Sequence<long.MaxValue&&
            request.ExpectedState==current.State&&request.ExpectedSequence==current.Sequence,"lifecycle.conflict");
        Require(Valid(request.CausationId)&&Valid(request.CorrelationId),"lifecycle.audit");
        Require(Allows(current.State,target),"lifecycle.transition");
        Require(authority.Permissions is not null&&authority.Permissions.Contains(Permission(current.State,target)),"lifecycle.denied");
        Require((request.Proposal is not null)==(target==DecisionState.Proposed)&&
            (request.ProposalDigest is not null)==(target==DecisionState.Approved)&&
            (request.ApprovalDigest is not null)==(target==DecisionState.Executing)&&
            (request.Receipt is not null)==(current.State==DecisionState.Executing),"lifecycle.request");
        if(target==DecisionState.Superseded)Require(!authority.IsCurrentRevision,"lifecycle.notSuperseded");
        else if(target is DecisionState.Validated or DecisionState.Compiled or DecisionState.Queued or DecisionState.Solving
            or DecisionState.Verified or DecisionState.Proposed or DecisionState.Approved or DecisionState.Executing)
            Require(authority.IsCurrentRevision,"lifecycle.stale");

        var evidenceId=current.EvidenceId;var proposal=current.Proposal;var approval=current.Approval;
        var execution=current.ExecutionId;var receipt=current.ReceiptSha256;
        switch(target)
        {
            case DecisionState.Solved:
                evidenceId=Evidence(key,authority.Evidence).EvidenceId;
                break;
            case DecisionState.Verified:
                Verified(key,evidenceId,authority.Evidence);
                break;
            case DecisionState.Proposed:
                Feasible(key,evidenceId,authority.Evidence);
                CheckPolicy(policy);
                var input=request.Proposal!;
                Require(Valid(input.TargetId)&&Digest(input.PayloadSha256)&&input.ExpiresAt.Offset==TimeSpan.Zero&&input.ExpiresAt>now&&
                    policy.AllowedTargets.Contains(input.TargetId,StringComparer.Ordinal),"lifecycle.proposal");
                proposal=new(evidenceId!,input.TargetId,input.PayloadSha256,policy.PolicyId,policy.Version,PolicyHash(policy),policy.Sensitive,actor,input.ExpiresAt,"");
                proposal=proposal with{Digest=ProposalHash(key,proposal)};
                break;
            case DecisionState.Approved:
                Feasible(key,evidenceId,authority.Evidence);
                CheckProposal(key,evidenceId,proposal,policy,now);
                Require(request.ProposalDigest==proposal!.Digest,"lifecycle.proposalDigest");
                Require(!proposal.Sensitive||!SameActor(proposal.Creator,actor),"lifecycle.separation");
                approval=new(proposal.Digest,actor,now,proposal.ExpiresAt,"");
                approval=approval with{Digest=ApprovalHash(key,approval)};
                break;
            case DecisionState.Executing:
                Feasible(key,evidenceId,authority.Evidence);
                CheckProposal(key,evidenceId,proposal,policy,now);
                Require(policy.ExternalExecutionEnabled&&(!proposal!.Sensitive||policy.SensitiveExecutionEnabled),"lifecycle.executionDisabled");
                Require(approval is not null&&approval.ProposalDigest==proposal!.Digest&&approval.Digest==ApprovalHash(key,approval)&&
                    request.ApprovalDigest==approval.Digest&&approval.ExpiresAt==proposal.ExpiresAt&&approval.ExpiresAt>now&&approval.ApprovedAt<=now&&
                    approval.Approver.TenantId==key.TenantId&&(!proposal.Sensitive||!SameActor(proposal.Creator,approval.Approver)),
                    "lifecycle.approval");
                Require(execution is null,"lifecycle.executionExists");
                execution=Hash(new{profile="wowdi.execution/1",key,approval=approval!.Digest});
                break;
            case DecisionState.Executed:
            case DecisionState.Failed when current.State==DecisionState.Executing:
                var outcome=request.Receipt!;
                Require(execution is not null&&outcome.ExecutionId==execution&&Digest(outcome.ReceiptSha256)&&
                    outcome.Disposition==(target==DecisionState.Executed?ExternalDisposition.ConfirmedExecuted:ExternalDisposition.ConfirmedNotExecuted),
                    "lifecycle.ambiguousExecution");
                receipt=outcome.ReceiptSha256;
                break;
        }
        var next=current with{State=target,Sequence=current.Sequence+1,ChangedAt=now,EvidenceId=evidenceId,
            Proposal=proposal,Approval=approval,ExecutionId=execution,ReceiptSha256=receipt};
        return new(next,new(key,next.Sequence,current.State,target,actor,now,request.CausationId,request.CorrelationId,evidenceId,
            proposal?.Digest,approval?.Digest,execution,receipt));
    }

    private static LifecyclePermission Permission(DecisionState from,DecisionState to)=>
        to switch
        {
            DecisionState.Approved or DecisionState.Rejected=>LifecyclePermission.Approve,
            DecisionState.Executing or DecisionState.Executed or DecisionState.Monitoring=>LifecyclePermission.Execute,
            DecisionState.Failed when from==DecisionState.Executing=>LifecyclePermission.Execute,
            DecisionState.Superseded or DecisionState.Validated or DecisionState.Compiled=>LifecyclePermission.Write,
            DecisionState.Cancelled or DecisionState.Failed when from is DecisionState.Draft or DecisionState.Validated or DecisionState.Compiled=>LifecyclePermission.Write,
            _=>LifecyclePermission.Propose
        };
    private static EvidenceRecord Evidence(DecisionKey key,EvidenceRecord? evidence)
    {
        Require(evidence is not null,"lifecycle.evidence");
        var scope=evidence!.Manifest.Binding.Scope;
        Require(scope.TenantId==key.TenantId&&scope.ModelId==key.ModelId&&scope.RevisionId==key.RevisionId,"lifecycle.evidenceScope");
        return evidence;
    }
    private static void Verified(DecisionKey key,string? id,EvidenceRecord? evidence)
    {
        var e=Evidence(key,evidence);
        Require(e.EvidenceId==id&&!e.Assessment.Rejected&&e.Assessment.Strength is EvidenceStrength.IndependentlyVerifiedPrimal or EvidenceStrength.ExactEnumeration,
            "lifecycle.unverified");
    }
    private static void Feasible(DecisionKey key,string? id,EvidenceRecord? evidence)
    {
        Verified(key,id,evidence);
        var e=evidence!;
        Require(e.Assessment.Incumbent is not null&&e.Assessment.Status is MathematicalStatus.Feasible or MathematicalStatus.Optimal&&
            e.Assessment.Termination is Termination.Completed or Termination.Timeout,"lifecycle.notFeasible");
    }
    private static void CheckPolicy(LifecyclePolicy p)=>Require(Valid(p.PolicyId)&&Valid(p.Version)&&p.AllowedTargets is not null&&
        p.AllowedTargets.All(Valid),"lifecycle.policy");
    private static void CheckProposal(DecisionKey key,string? id,DecisionProposal? p,LifecyclePolicy policy,DateTimeOffset now)
    {
        CheckPolicy(policy);
        Require(p is not null&&p.EvidenceId==id&&p.Creator.TenantId==key.TenantId&&p.PolicyId==policy.PolicyId&&p.PolicyVersion==policy.Version&&p.PolicyDigest==PolicyHash(policy)&&
            p.Sensitive==policy.Sensitive&&p.ExpiresAt>now&&policy.AllowedTargets.Contains(p.TargetId,StringComparer.Ordinal)&&
            p.Digest==ProposalHash(key,p),"lifecycle.staleProposal");
    }
    private static string PolicyHash(LifecyclePolicy p)=>Hash(new{profile="wowdi.lifecycle.policy/1",p.PolicyId,p.Version,p.Sensitive,p.ExternalExecutionEnabled,p.SensitiveExecutionEnabled,targets=p.AllowedTargets.Order(StringComparer.Ordinal)});
    private static bool SameActor(LifecycleActor a,LifecycleActor b)=>a.TenantId==b.TenantId&&a.Issuer==b.Issuer&&a.Subject==b.Subject;
    private static string ProposalHash(DecisionKey key,DecisionProposal p)=>Hash(new{profile="wowdi.proposal/1",key,proposal=p with{Digest=""}});
    private static string ApprovalHash(DecisionKey key,DecisionApproval a)=>Hash(new{profile="wowdi.approval/1",key,approval=a with{Digest=""}});
    private static string Hash<T>(T value)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static bool Valid(string? s)
    {
        if(string.IsNullOrWhiteSpace(s)||s.Length>256||s!=s.Trim()||s.Any(char.IsControl))return false;
        try{_=new System.Text.UTF8Encoding(false,true).GetByteCount(s);return true;}
        catch(System.Text.EncoderFallbackException){return false;}
    }
    private static bool Digest(string? s)=>s is {Length:64}&&s.All(char.IsAsciiHexDigitLower);
    private static void Require(bool condition,string code){if(!condition)throw new LifecycleRejectedException(code);}
}
