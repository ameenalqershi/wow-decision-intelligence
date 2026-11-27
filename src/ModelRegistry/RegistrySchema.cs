using Microsoft.Data.Sqlite;
using WowDi.DecisionGraph;

namespace WowDi.ModelRegistry;

internal static class RegistrySchema
{
    private const long ApplicationId = 1464092999;
    internal static void Upgrade(SqliteTransaction tx)
    {
        long Value(string sql)
        {
            using var cmd = SqliteModelRegistry.Command(tx, sql);
            return (long)cmd.ExecuteScalar()!;
        }
        void Run(string sql)
        {
            using var cmd = SqliteModelRegistry.Command(tx, sql);
            cmd.ExecuteNonQuery();
        }
        var version = Value("PRAGMA user_version");
        var app = Value("PRAGMA application_id");
        if (version == 0 && app == 0 && Value("SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'") == 0)
        {
            Run("""
                CREATE TABLE revisions(tenant TEXT NOT NULL, model_id TEXT NOT NULL, revision_id TEXT NOT NULL,
                    model BLOB NOT NULL, graph BLOB NOT NULL, PRIMARY KEY(tenant,model_id,revision_id));
                CREATE TABLE content(tenant TEXT NOT NULL, hash TEXT NOT NULL, data BLOB NOT NULL, PRIMARY KEY(tenant,hash));
                CREATE TABLE inputs(tenant TEXT NOT NULL, model_id TEXT NOT NULL, revision_id TEXT NOT NULL, hash TEXT NOT NULL,
                    PRIMARY KEY(tenant,model_id,revision_id,hash),
                    FOREIGN KEY(tenant,model_id,revision_id) REFERENCES revisions(tenant,model_id,revision_id),
                    FOREIGN KEY(tenant,hash) REFERENCES content(tenant,hash));
                PRAGMA application_id=1464092999;
                PRAGMA user_version=1;
                """);
            version = 1;
        }
        else if (app != ApplicationId || version is not (1 or 2)) throw RevisionContent.Error("schema");
        if (version == 2) return;

        Run("ALTER TABLE revisions ADD COLUMN model_hash TEXT; ALTER TABLE revisions ADD COLUMN graph_hash TEXT;");
        using var keys = SqliteModelRegistry.Command(tx, "SELECT tenant,model_id,revision_id FROM revisions");
        var revisions = new List<GraphRevision>();
        using (var rows = keys.ExecuteReader())
            while (rows.Read()) revisions.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2)));
        foreach (var key in revisions)
        {
            _ = SqliteModelRegistry.Load(tx, key, checkHashes: false) ?? throw RevisionContent.Error("migration");
            using var query = SqliteModelRegistry.Command(tx, "SELECT model,graph FROM revisions WHERE tenant=$t AND model_id=$m AND revision_id=$r", key);
            using var update = SqliteModelRegistry.Command(tx, """
                UPDATE revisions SET model_hash=$mh, graph_hash=$gh WHERE tenant=$t AND model_id=$m AND revision_id=$r
                """, key);
            using (var data = query.ExecuteReader())
            {
                if (!data.Read()) throw RevisionContent.Error("migration");
                update.Parameters.AddWithValue("$mh", RevisionContent.Hash((byte[])data[0]));
                update.Parameters.AddWithValue("$gh", RevisionContent.Hash((byte[])data[1]));
            }
            update.ExecuteNonQuery();
        }
        Run("PRAGMA user_version=2");
    }
}
