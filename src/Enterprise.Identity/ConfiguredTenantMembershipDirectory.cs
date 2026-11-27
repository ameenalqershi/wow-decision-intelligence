using Microsoft.Extensions.Options;

namespace WowDi.Enterprise.Identity;

// Explicit host-side grants; never deserialize from a request or a token.
// A host can replace this provider with a transactional directory under E02.
public sealed class TenantDirectoryOptions
{
    public TenantMembershipGrant[] Memberships { get; set; } = [];
}
public sealed class TenantMembershipGrant
{
    public string Issuer { get; set; } = "";
    public string Subject { get; set; } = "";
    public string TenantId { get; set; } = "";
    public bool Active { get; set; }
    public TenantModelGrant[] Models { get; set; } = [];
}
public sealed class TenantModelGrant
{
    public string ModelId { get; set; } = "";
    public TenantPermission[] Permissions { get; set; } = [];
}

public sealed class ConfiguredTenantMembershipDirectory(IOptionsMonitor<TenantDirectoryOptions> options) : ITenantMembershipDirectory
{
    private TenantMembershipGrant? Find(TenantActor actor)
    {
        // Missing, duplicated or inactive entries are denied, not merged.
        var matches = options.CurrentValue.Memberships.Where(m =>
            m.Issuer == actor.Issuer && m.Subject == actor.Subject && m.TenantId == actor.TenantId).Take(2).ToArray();
        return matches.Length == 1 && matches[0].Active ? matches[0] : null;
    }
    public ValueTask<bool> IsActiveAsync(TenantActor actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Find(actor) is not null);
    }
    public ValueTask<bool> AllowsAsync(TenantActor actor, TenantPermission permission, string modelId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var member = Find(actor);
        if (member is null || !Enum.IsDefined(permission)) return ValueTask.FromResult(false);
        var matches = member.Models.Where(m => m.ModelId == modelId).Take(2).ToArray();
        return ValueTask.FromResult(matches.Length == 1 &&
            matches[0].Permissions.All(p => Enum.IsDefined(p)) &&
            matches[0].Permissions.Contains(permission));
    }
}
