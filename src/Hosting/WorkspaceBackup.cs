using Microsoft.Data.Sqlite;

namespace WowDi.Hosting;

/// <summary>Copies a consistent SQLite snapshot; only publishes a fully checked independent workspace.</summary>
public static class WorkspaceBackup
{
    public static LocalWorkspace RestoreCopy(LocalWorkspace source, string destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        cancellationToken.ThrowIfCancellationRequested();
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var parent = Path.GetDirectoryName(full) ?? throw LocalWorkspace.Error("path");
        if (!Directory.Exists(parent) || Directory.Exists(full) || File.Exists(full))
            throw LocalWorkspace.Error("existsOrParent");
        using var lease = source.AcquireWrite(cancellationToken);
        LocalWorkspace.CheckEvidenceScope(source.EvidencePath, source.Identity.TenantId);
        var stage = Path.Combine(parent, ".wowdi-restore-" + Guid.NewGuid().ToString("N"));
        LocalWorkspace.CreatePrivateDirectory(stage);
        try
        {
            using (var from = source.OpenDatabase(readOnly: true))
            using (var to = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(stage, "workspace.db"), Pooling = false, ForeignKeys = true
            }.ToString()))
            {
                to.Open();
                from.BackupDatabase(to);
            }
            using (var from = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = source.EvidencePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var to = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(stage, "evidence.db"), Pooling = false }.ToString()))
            {
                from.Open(); to.Open(); from.BackupDatabase(to);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var copy = LocalWorkspace.Open(stage);
            if (copy.Identity != source.Identity) throw LocalWorkspace.Error("backupScope");
            using (var db = copy.OpenDatabase())
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check";
                using (var row = cmd.ExecuteReader())
                    if (!row.Read() || row.GetString(0) != "ok" || row.Read())
                        throw LocalWorkspace.Error("backupIntegrity");
                cmd.CommandText = "PRAGMA foreign_key_check";
                using (var row = cmd.ExecuteReader())
                    if (row.Read()) throw LocalWorkspace.Error("backupIntegrity");
            }
            foreach (var key in copy.ListRevisions())
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = copy.FindRevision(key.ModelId, key.RevisionId) ?? throw LocalWorkspace.Error("revisionMissing");
                foreach (var run in LocalRunArtifacts.ListRunIds(copy, key.ModelId, key.RevisionId))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _ = LocalRunArtifacts.Find(copy, key.ModelId, key.RevisionId, run)
                        ?? throw LocalWorkspace.Error("artifactMissing");
                }
            }
            _ = new LocalSettings(copy).Read();
            new LocalEvidence(copy).ValidateAll(cancellationToken);
            // Independent restored context; frozen tenant/model/revision/run identities remain unchanged.
            using (var db = copy.OpenDatabase())
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "UPDATE workspace_identity SET workspace_id=$id WHERE singleton=1";
                cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                if (cmd.ExecuteNonQuery() != 1) throw LocalWorkspace.Error("backupScope");
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stage, full);
            return LocalWorkspace.Open(full);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }
}
