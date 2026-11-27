using System.Collections.Immutable;
using WowDi.Runtime.Governance;

namespace WowDi.Enterprise.Isolation;

public enum ExtensionCapability { ReadFrozenInput, EphemeralScratch }
public sealed record ExtensionIdentity(string Id,string Version,string PackageSha256);
public sealed record ExtensionGrant(string TenantId,string ModelId,ExtensionIdentity Extension,
    ImmutableArray<ExtensionCapability> Capabilities,int MaximumThreads,long MaximumBytes,TimeSpan MaximumDeadline);
public sealed record ExtensionInvocation(ExtensionIdentity Extension,ImmutableArray<ExtensionCapability> RequestedCapabilities);

// Host-owned approval source. EXT01 versioned manifests and E01/S12 authenticated job admission remain separate.
public interface IExtensionApprovalDirectory
{
    ValueTask<ExtensionGrant?> FindAsync(string tenantId,string modelId,ExtensionIdentity extension,CancellationToken ct);
}
public sealed class ExtensionRejectedException(string code):Exception(code)
{
    public string Code{get;}=code;
}
public sealed class ExtensionAdmission(IExtensionApprovalDirectory approvals)
{
    public async ValueTask AuthorizeAsync(WorkerLease lease,ExtensionInvocation invocation,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(invocation);
        if(!ValidIdentity(invocation.Extension)||!ValidCaps(invocation.RequestedCapabilities)||
            !ValidScope(lease.Request.Key.TenantId)||!ValidScope(lease.Request.Key.ModelId)||
            !ValidScope(lease.Request.Key.RevisionId)||!ValidScope(lease.Request.Key.RunId)||
            !Digest(lease.Request.FrozenInputSha256)||lease.Epoch==Guid.Empty||lease.Fence<=0||
            lease.Request.Budget.Threads<=0||lease.Request.Budget.MemoryBytes<=0||lease.Request.Budget.Deadline<=TimeSpan.Zero)
            throw new ExtensionRejectedException("extension.request");
        var key=lease.Request.Key;
        var grant=await approvals.FindAsync(key.TenantId,key.ModelId,invocation.Extension,ct);
        if(grant is null||grant.TenantId!=key.TenantId||grant.ModelId!=key.ModelId||
            grant.Extension!=invocation.Extension||!ValidCaps(grant.Capabilities)||
            invocation.RequestedCapabilities.Any(c=>!grant.Capabilities.Contains(c))||
            lease.Request.Budget.Threads>grant.MaximumThreads||lease.Request.Budget.MemoryBytes>grant.MaximumBytes||
            lease.Request.Budget.Deadline>grant.MaximumDeadline)
            throw new ExtensionRejectedException("extension.denied");
    }
    internal static bool Digest(string? s)=>s is {Length:64}&&s.All(char.IsAsciiHexDigitLower);
    private static bool ValidScope(string? s)=>!string.IsNullOrWhiteSpace(s)&&s.Length<=256&&s==s.Trim()&&!s.Any(char.IsControl);
    internal static bool Token(string? s)=>s is {Length:>0 and <=128}&&s.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '.')&&s is not ("." or "..");
    internal static bool ValidIdentity(ExtensionIdentity? i)=>i is not null&&Token(i.Id)&&Token(i.Version)&&Digest(i.PackageSha256);
    private static bool ValidCaps(ImmutableArray<ExtensionCapability> caps)=>!caps.IsDefault&&caps.All(Enum.IsDefined)&&caps.Distinct().Count()==caps.Length;
}
