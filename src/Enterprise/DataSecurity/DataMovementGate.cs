using System.Collections.Immutable;
using WowDi.Enterprise.Identity;

namespace WowDi.Enterprise.DataSecurity;

// A host must supply a dedicated policy for data movement; model View/Write alone
// does not imply authority to export, purge or transfer customer data.
public enum DataMovementAction { Import, RedactedExport, Retention, ExternalDestination, JobAdmission }

public interface IDataMovementPolicy
{
    ValueTask<bool> AllowsAsync(TenantActor actor, string modelId, DataMovementAction action, CancellationToken cancellationToken);
}

public sealed class DataMovementGate(TenantAccess identity, IDataMovementPolicy policy, ITenantDenialAudit audit, TimeProvider clock)
{
    public async ValueTask<TenantActor?> AuthorizeAsync(TenantResource resource, DataMovementAction action,
        CancellationToken cancellationToken = default)
    {
        var permission = action switch
        {
            DataMovementAction.Import or DataMovementAction.Retention => TenantPermission.Write,
            DataMovementAction.RedactedExport => TenantPermission.View,
            DataMovementAction.ExternalDestination => TenantPermission.Execute,
            DataMovementAction.JobAdmission => TenantPermission.Propose,
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        var actor = await identity.AuthorizeAsync(permission, resource, cancellationToken);
        if (actor is null) return null;
        if (await policy.AllowsAsync(actor, resource.ModelId, action, cancellationToken)) return actor;
        await audit.RecordAsync(new(actor.TenantId, actor.Subject, action.ToString(), "data-movement", clock.GetUtcNow()), CancellationToken.None);
        return null;
    }
}

// The host must call before reading import bytes or admitting jobs. Use independent
// singleton budgets for import and job admission; a distributed implementation is E08.
public sealed class SecureTransferGate(DataMovementGate authorization, TenantAdmissionLimiter imports,
    TenantAdmissionLimiter jobs, DestinationPolicy destinations)
{
    public async ValueTask<ImmutableArray<ArchiveMember>?> StageArchiveAsync(TenantResource resource, Stream input,
        ArchiveLimits limits, CancellationToken cancellationToken = default)
    {
        var actor = await authorization.AuthorizeAsync(resource, DataMovementAction.Import, cancellationToken);
        if (actor is null) return null;
        if (!imports.TryAcquire(actor)) throw new DataSecurityException("admission.limit");
        return await BoundedArchive.ReadAsync(input, limits, cancellationToken);
    }

    public async ValueTask<bool> AdmitJobAsync(TenantResource resource, CancellationToken cancellationToken = default)
    {
        var actor = await authorization.AuthorizeAsync(resource, DataMovementAction.JobAdmission, cancellationToken);
        return actor is not null && jobs.TryAcquire(actor);
    }

    // Only a policy decision; not an HTTP send or approval to execute a mathematical decision.
    public async ValueTask<bool> AuthorizeDestinationAsync(TenantResource resource, string endpoint,
        CancellationToken cancellationToken = default)
    {
        var actor = await authorization.AuthorizeAsync(resource, DataMovementAction.ExternalDestination, cancellationToken);
        return actor is not null && destinations.Allows(actor, endpoint);
    }
}
