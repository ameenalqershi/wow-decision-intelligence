using WowDi.Enterprise.Identity;
using WowDi.Integration.Connectors;

namespace WowDi.Integration.Snapshots;

public sealed record RegisteredMapping(ConnectorStreamIdentity Stream,ReadOnlyMemory<byte> Manifest,TargetSchema Target);
// Host-owned registry; implementation must enforce actor/model/connector scope and return immutable registered versions.
public interface IAuthorizedMappingRegistry
{
    ValueTask<RegisteredMapping?> FindAsync(TenantActor actor,string modelId,string connectorId,string mappingVersion,string streamId,CancellationToken ct);
}

// API-MAPPING application seam. No raw secrets, no commit, no arbitrary manifest or target supplied by callers.
public sealed class MappingPreview(TenantAccess identity,IAuthorizedMappingRegistry registry)
{
    public async ValueTask<SnapshotResult?> PreviewAsync(TenantResource scope,string connectorId,string mappingVersion,
        string streamId,ReadOnlyMemory<byte> source,SnapshotOptions options,CancellationToken ct=default)
    {
        var actor=await identity.AuthorizeAsync(TenantPermission.Write,scope,ct);
        if(actor is null) return null;
        var mapping=await registry.FindAsync(actor,scope.ModelId,connectorId,mappingVersion,streamId,ct);
        if(mapping is null || mapping.Stream!=new ConnectorStreamIdentity(actor.TenantId,connectorId,mappingVersion,streamId)) return null;
        return SnapshotImport.Stage(mapping.Stream,mapping.Manifest,mapping.Target,source,options,ct);
    }
}
