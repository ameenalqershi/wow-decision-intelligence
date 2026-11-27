using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using WowDi.Solver.Contracts;
using WowDi.ModelRegistry;
using WowDi.Verification.Bounds;
using WowDi.Verification.Evidence;

namespace WowDi.Hosting;

public sealed record LocalEvidenceExport(EvidenceKey Key,string OriginalSha256,ImmutableArray<byte> Bytes);

/// <summary>Trusted host publication boundary, not a CLI/user-supplied solver-result endpoint.</summary>
public sealed class LocalEvidence(LocalWorkspace workspace)
{
    public EvidenceKey Capture(EvidenceBinding binding,RunInvocation invocation,ReadOnlyMemory<byte> catalog,
        SolverCapabilities capabilities,SolverResult result,ReadOnlyMemory<byte> settings,
        string? parentEvidenceId=null,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var scope=binding.Scope;
        if(scope.TenantId!=workspace.Identity.TenantId)throw LocalWorkspace.Error("scope");
        using var lease=workspace.AcquireWrite(ct);
        LocalWorkspace.CheckEvidenceScope(workspace.EvidencePath,workspace.Identity.TenantId);
        var revision=workspace.FindRevision(scope.ModelId,scope.RevisionId)??throw LocalWorkspace.Error("revisionMissing");
        if(catalog.Length is <1 or >262144)throw LocalWorkspace.Error("catalogSize");
        var frozenCatalog=catalog.ToArray();
        ValidateCatalog(revision,binding.CatalogSha256,frozenCatalog);
        var record=EvidenceRecord.Capture(revision,binding,invocation,frozenCatalog,capabilities,result,settings,parentEvidenceId,ct);
        return workspace.EvidenceStore().Save(workspace.Identity.TenantId,record,ct);
    }

    public EvidenceRecord? Find(EvidenceKey key,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if(key.TenantId!=workspace.Identity.TenantId)throw LocalWorkspace.Error("scope");
        LocalWorkspace.CheckEvidenceScope(workspace.EvidencePath,workspace.Identity.TenantId);
        var record=workspace.EvidenceStore().Find(workspace.Identity.TenantId,key,ct);
        if(record is not null) ValidateCatalog(key,record);
        return record;
    }

    public LocalEvidenceExport Export(EvidenceKey key,CancellationToken ct=default)
    {
        var record=Find(key,ct)??throw LocalWorkspace.Error("evidenceMissing");
        var bytes=EvidencePackage.Export(record);
        return new(key,LocalRevisionTransfer.Digest(bytes),ImmutableArray.CreateRange(bytes));
    }

    // Expected identity and digest must come from a trusted ledger, not solely from an imported package.
    // Does not publish imported bytes or turn them into a trusted invocation.
    public EvidenceRecord Replay(LocalEvidenceExport package,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if(package.Key.TenantId!=workspace.Identity.TenantId)throw LocalWorkspace.Error("scope");
        var revision=workspace.FindRevision(package.Key.ModelId,package.Key.RevisionId)??throw LocalWorkspace.Error("revisionMissing");
        if(package.Bytes.IsDefaultOrEmpty||package.Bytes.Length>EvidencePackage.MaximumPackageBytes)throw LocalWorkspace.Error("evidenceSize");
        var record=EvidencePackage.Replay(revision,workspace.Identity.TenantId,package.Key.EvidenceId,
            package.OriginalSha256,package.Bytes.ToArray(),ct);
        ValidateCatalog(revision,record.Manifest.Binding.CatalogSha256,record.CatalogBytes.AsSpan());
        return record;
    }

    private void ValidateCatalog(EvidenceKey key,EvidenceRecord record)
    {
        var revision=workspace.FindRevision(key.ModelId,key.RevisionId)??throw LocalWorkspace.Error("revisionMissing");
        ValidateCatalog(revision,record.Manifest.Binding.CatalogSha256,record.CatalogBytes.AsSpan());
    }

    private static void ValidateCatalog(StoredRevision revision,string digest,ReadOnlySpan<byte> catalog)
    {
        // Local reproducibility is stricter than the generic S10 capture contract:
        // the exact measurement snapshot must already be frozen as a revision input.
        if(!revision.Document.Model.InputDigests.Contains(digest,StringComparer.Ordinal)||
            !revision.Inputs.TryGetValue(digest,out var bytes)||!bytes.AsSpan().SequenceEqual(catalog))
            throw LocalWorkspace.Error("catalogNotFrozen");
    }

    // Internal restore check: replay every evidence row, including rejected/unknown observations.
    internal void ValidateAll(CancellationToken ct)
    {
        LocalWorkspace.CheckEvidenceScope(workspace.EvidencePath,workspace.Identity.TenantId);
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder
        {DataSource=workspace.EvidencePath,Mode=SqliteOpenMode.ReadOnly,Pooling=false,ForeignKeys=true}.ToString());
        db.Open();
        using var cmd=db.CreateCommand();
        cmd.CommandText="PRAGMA integrity_check";
        using(var row=cmd.ExecuteReader())
            if(!row.Read()||row.GetString(0)!="ok"||row.Read())throw LocalWorkspace.Error("backupIntegrity");
        cmd.CommandText="PRAGMA foreign_key_check";
        using(var row=cmd.ExecuteReader())if(row.Read())throw LocalWorkspace.Error("backupIntegrity");
        cmd.CommandText="SELECT tenant,model_id,revision_id,evidence_id FROM evidence ORDER BY tenant,model_id,revision_id,evidence_id";
        using var rows=cmd.ExecuteReader();
        // Snapshot keys from the quiesced staging workspace before replay.
        var keys=new List<EvidenceKey>();
        while(rows.Read())
        {
            ct.ThrowIfCancellationRequested();
            keys.Add(new(rows.GetString(0),rows.GetString(1),rows.GetString(2),rows.GetString(3)));
        }
        rows.Close();
        var store=workspace.EvidenceStore();
        foreach(var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            var record=store.Find(workspace.Identity.TenantId,key,ct)??throw LocalWorkspace.Error("evidenceMissing");
            ValidateCatalog(key,record);
        }
    }
}
