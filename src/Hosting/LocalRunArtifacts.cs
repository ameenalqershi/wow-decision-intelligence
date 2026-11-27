using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using WowDi.DecisionGraph;

namespace WowDi.Hosting;

// An opaque recorded run artifact is always unverified at this boundary.
// Independent original-model verification and authoritative evidence publication are separate.
public sealed record UnverifiedRunArtifact(GraphRevision Revision, string RunId, string Digest, ImmutableArray<byte> Bytes);

public static class LocalRunArtifacts
{
    public const int MaximumArtifactBytes = 16 * 1024 * 1024;

    public static UnverifiedRunArtifact Store(LocalWorkspace workspace, string modelId, string revisionId, string runId, ReadOnlyMemory<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (bytes.IsEmpty || bytes.Length > MaximumArtifactBytes) throw LocalWorkspace.Error("artifactSize");
        using var lease = workspace.AcquireWrite();
        var revision = workspace.FindRevision(modelId, revisionId) ?? throw LocalWorkspace.Error("revisionMissing");
        var key = revision.Graph.Revision;
        var owned = bytes.ToArray();
        var digest = LocalRevisionTransfer.Digest(owned);
        using var db = workspace.OpenDatabase();
        using var tx = db.BeginTransaction(deferred: false);
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO local_artifacts(tenant,model_id,revision_id,run_id,digest,bytes)
            VALUES ($t,$m,$r,$run,$digest,$bytes) ON CONFLICT(tenant,run_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$t", key.TenantId);
        cmd.Parameters.AddWithValue("$m", key.ModelId);
        cmd.Parameters.AddWithValue("$r", key.RevisionId);
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$digest", digest);
        cmd.Parameters.AddWithValue("$bytes", owned);
        if (cmd.ExecuteNonQuery() == 0)
        {
            var prior = Read(db, tx, key.TenantId, runId);
            if (prior is null || prior.Revision != key || prior.Digest != digest || !prior.Bytes.AsSpan().SequenceEqual(owned))
                throw LocalWorkspace.Error("runConflict");
        }
        tx.Commit();
        return new(key, runId, digest, ImmutableArray.CreateRange(owned));
    }

    public static UnverifiedRunArtifact? Find(LocalWorkspace workspace, string modelId, string revisionId, string runId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (workspace.FindRevision(modelId, revisionId) is null) return null;
        using var db = workspace.OpenDatabase(readOnly: true);
        using var tx = db.BeginTransaction(deferred: true);
        var result = Read(db, tx, workspace.Identity.TenantId, runId, modelId, revisionId);
        tx.Commit();
        return result;
    }

    public static IReadOnlyList<string> ListRunIds(LocalWorkspace workspace, string modelId, string revisionId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.FindRevision(modelId, revisionId) is null) return Array.Empty<string>();
        using var db = workspace.OpenDatabase(readOnly: true);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT run_id FROM local_artifacts WHERE tenant=$t AND model_id=$m AND revision_id=$r ORDER BY run_id";
        cmd.Parameters.AddWithValue("$t", workspace.Identity.TenantId);
        cmd.Parameters.AddWithValue("$m", modelId);
        cmd.Parameters.AddWithValue("$r", revisionId);
        using var reader = cmd.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids.AsReadOnly();
    }

    private static UnverifiedRunArtifact? Read(SqliteConnection db, SqliteTransaction tx, string tenant, string runId, string? modelId = null, string? revisionId = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT model_id,revision_id,digest,length(bytes),bytes FROM local_artifacts WHERE tenant=$t AND run_id=$run AND ($m IS NULL OR model_id=$m) AND ($r IS NULL OR revision_id=$r)";
        cmd.Parameters.AddWithValue("$t", tenant);
        cmd.Parameters.AddWithValue("$m", (object?)modelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$r", (object?)revisionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$run", runId);
        using var row = cmd.ExecuteReader();
        if (!row.Read()) return null;
        if (row.GetInt64(3) is <= 0 or > MaximumArtifactBytes) throw LocalWorkspace.Error("artifactSize");
        var bytes = (byte[])row[4];
        var digest = row.GetString(2);
        if (LocalRevisionTransfer.Digest(bytes) != digest) throw LocalWorkspace.Error("artifactDigest");
        return new(new(tenant, row.GetString(0), row.GetString(1)), runId, digest, ImmutableArray.CreateRange(bytes));
    }
}
