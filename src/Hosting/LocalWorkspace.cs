using System.Diagnostics;
using Microsoft.Data.Sqlite;
using WowDi.Contracts;
using WowDi.DecisionGraph;
using WowDi.ModelRegistry;
using WowDi.Udir;

namespace WowDi.Hosting;

public sealed record WorkspaceIdentity(string WorkspaceId, string TenantId, int SchemaVersion);

/// <summary>Explicit local-only workspace. The trusted tenant is established at creation, never taken from an import.</summary>
public sealed class LocalWorkspace
{
    public const int SchemaVersion = 1;
    private const string DatabaseName = "workspace.db";
    public static readonly ActivitySource ActivitySource = new("WowDi.Hosting.LocalWorkspace");
    public string DirectoryPath { get; }
    public WorkspaceIdentity Identity { get; }
    private readonly SqliteModelRegistry registry;
    private string DatabasePath => Path.Combine(DirectoryPath, DatabaseName);

    private LocalWorkspace(string path, WorkspaceIdentity identity)
    {
        DirectoryPath = path;
        Identity = identity;
        registry = new SqliteModelRegistry(DatabasePath);
    }

    public static LocalWorkspace Create(string path, string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var full = Normalize(path);
        var parent = Path.GetDirectoryName(full) ?? throw Error("path");
        if (!Directory.Exists(parent) || Directory.Exists(full) || File.Exists(full)) throw Error("existsOrParent");
        var stage = Path.Combine(parent, ".wowdi-create-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            using var activity = ActivitySource.StartActivity("workspace.create");
            var dbPath = Path.Combine(stage, DatabaseName);
            _ = new SqliteModelRegistry(dbPath);
            using (var db = Connect(dbPath))
            using (var tx = db.BeginTransaction(deferred: false))
            {
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    CREATE TABLE workspace_identity (singleton INTEGER PRIMARY KEY CHECK(singleton=1),
                        workspace_id TEXT NOT NULL, tenant_id TEXT NOT NULL, schema_version INTEGER NOT NULL);
                    CREATE TABLE local_artifacts (
                        tenant TEXT NOT NULL, model_id TEXT NOT NULL, revision_id TEXT NOT NULL,
                        run_id TEXT NOT NULL, digest TEXT NOT NULL, bytes BLOB NOT NULL,
                        PRIMARY KEY(tenant,run_id),
                        FOREIGN KEY(tenant,model_id,revision_id) REFERENCES revisions(tenant,model_id,revision_id));
                    INSERT INTO workspace_identity VALUES (1,$id,$tenant,$version);
                    """;
                cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("$tenant", tenantId);
                cmd.Parameters.AddWithValue("$version", SchemaVersion);
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            // Same filesystem; never overwrite an existing directory, even if another creator won the race.
            Directory.Move(stage, full);
            return Open(full);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    public static LocalWorkspace Open(string path)
    {
        var full = Normalize(path);
        using var activity = ActivitySource.StartActivity("workspace.open");
        var dbPath = Path.Combine(full, DatabaseName);
        if (!File.Exists(dbPath)) throw Error("notWorkspace");
        WorkspaceIdentity identity;
        // Validate workspace identity and version before allowing registry initialization/migration.
        using (var db = Connect(dbPath, readOnly: true))
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='workspace_identity'";
            if ((long)cmd.ExecuteScalar()! != 1) throw Error("notWorkspace");
            cmd.CommandText = "SELECT workspace_id,tenant_id,schema_version FROM workspace_identity WHERE singleton=1";
            using var row = cmd.ExecuteReader();
            if (!row.Read() || row.GetInt64(2) != SchemaVersion) throw Error("schema");
            identity = new(row.GetString(0), row.GetString(1), SchemaVersion);
            if (!Guid.TryParseExact(identity.WorkspaceId, "N", out _) || string.IsNullOrWhiteSpace(identity.TenantId))
                throw Error("identity");
        }
        return new(full, identity);
    }

    public GraphRevision SaveRevision(UdirDocument document, DecisionGraphSnapshot graph,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> inputs)
    {
        using var activity = ActivitySource.StartActivity("workspace.save_revision");
        return registry.Save(Identity.TenantId, document, graph, inputs);
    }

    public StoredRevision? FindRevision(string modelId, string revisionId) =>
        registry.Find(Identity.TenantId, modelId, revisionId);

    public IReadOnlyList<GraphRevision> ListRevisions()
    {
        using var db = Connect(DatabasePath, readOnly: true);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT model_id,revision_id FROM revisions WHERE tenant=$tenant ORDER BY model_id,revision_id";
        cmd.Parameters.AddWithValue("$tenant", Identity.TenantId);
        using var rows = cmd.ExecuteReader();
        var result = new List<GraphRevision>();
        while (rows.Read()) result.Add(new(Identity.TenantId, rows.GetString(0), rows.GetString(1)));
        return result.AsReadOnly();
    }

    internal SqliteConnection OpenDatabase(bool readOnly = false) => Connect(DatabasePath, readOnly);
    internal static ContractException Error(string code) => new("workspace." + code, "Invalid or conflicting local workspace " + code + ".");
    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    private static SqliteConnection Connect(string path, bool readOnly = false)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            ForeignKeys = true, Pooling = false
        }.ToString());
        try { db.Open(); return db; }
        catch { db.Dispose(); throw; }
    }
}
