using WowDi.Enterprise.Identity;
using WowDi.Integration.Snapshots;

namespace WowDi.Enterprise.DataSecurity;

/// <summary>
/// SEC01 admission for I02 CSV/JSON streams. Configure the same singleton import budget
/// used by SecureTransferGate for ZIP; callers cannot bypass it by changing format.
/// </summary>
public sealed class SecureSnapshotPreview(DataMovementGate gate,TenantAdmissionLimiter importBudget,MappingPreview preview)
{
    public const int MaximumSourceBytes=4*1024*1024;

    public async ValueTask<SnapshotResult?> PreviewAsync(TenantResource scope,string connectorId,string mappingVersion,
        string streamId,Stream source,SnapshotOptions options,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var actor=await gate.AuthorizeAsync(scope,DataMovementAction.Import,ct);
        if(actor is null)return null;
        if(!importBudget.TryAcquire(actor))throw new DataSecurityException("admission.limit");
        using var buffer=new MemoryStream();
        var chunk=new byte[8192];
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            var read=await source.ReadAsync(chunk,ct);
            if(read==0)break;
            if(read>MaximumSourceBytes-buffer.Length)throw new DataSecurityException("snapshot.size");
            buffer.Write(chunk,0,read);
        }
        // Reauthorize against the live registry before returning any normalized values.
        if(await gate.AuthorizeAsync(scope,DataMovementAction.Import,ct)!=actor)return null;
        var result=await preview.PreviewAsync(scope,connectorId,mappingVersion,streamId,buffer.ToArray(),options,ct);
        return await gate.AuthorizeAsync(scope,DataMovementAction.Import,ct)==actor?result:null;
    }
}
