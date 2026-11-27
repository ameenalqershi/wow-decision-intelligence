using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using WowDi.DecisionGraph;
using WowDi.Udir;

namespace WowDi.ModelRegistry;

public sealed class SqliteModelRegistry
{
    private readonly string connectionString;
    public SqliteModelRegistry(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath), ForeignKeys = true, Pooling = false
        }.ToString();
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        RegistrySchema.Upgrade(tx);
        tx.Commit();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        try { db.Open(); return db; }
        catch { db.Dispose(); throw; }
    }

    internal static SqliteCommand Command(SqliteTransaction tx, string sql, GraphRevision? key = null)
    {
        var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        if (key != null)
        {
            cmd.Parameters.AddWithValue("$t", key.TenantId);
            cmd.Parameters.AddWithValue("$m", key.ModelId);
            cmd.Parameters.AddWithValue("$r", key.RevisionId);
        }
        return cmd;
    }

    private const string Where = "tenant=$t AND model_id=$m AND revision_id=$r";

    public GraphRevision Save(string trustedTenant, UdirDocument model, DecisionGraphSnapshot graph,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> inputs)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(inputs);
        var key = RevisionContent.Key(model);
        if (key.TenantId != trustedTenant || graph.Revision != key) throw RevisionContent.Error("scope");
        var owned = inputs.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
        RevisionContent.Validate(model, owned);
        var modelBytes = model.Write();
        var graphBytes = GraphData.Encode(graph);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var prior = Load(tx, key, checkHashes: true);
        if (prior != null)
        {
            if (!modelBytes.AsSpan().SequenceEqual(prior.Document.Write()) ||
                !graphBytes.AsSpan().SequenceEqual(GraphData.Encode(prior.Graph)))
                throw RevisionContent.Error("revisionConflict");
            tx.Commit();
            return key;
        }
        using var insert = Command(tx, """
            INSERT INTO revisions (tenant,model_id,revision_id,model,graph,model_hash,graph_hash)
            VALUES ($t,$m,$r,$model,$graph,$mh,$gh)
            """, key);
        insert.Parameters.AddWithValue("$model", modelBytes);
        insert.Parameters.AddWithValue("$graph", graphBytes);
        insert.Parameters.AddWithValue("$mh", RevisionContent.Hash(modelBytes));
        insert.Parameters.AddWithValue("$gh", RevisionContent.Hash(graphBytes));
        insert.ExecuteNonQuery();
        foreach (var (hash, bytes) in owned)
        {
            using var blob = Command(tx, """
                INSERT INTO content (tenant,hash,data) VALUES ($t,$hash,$bytes) ON CONFLICT(tenant,hash) DO NOTHING
                """, key);
            blob.Parameters.AddWithValue("$hash", hash);
            blob.Parameters.AddWithValue("$bytes", bytes);
            if (blob.ExecuteNonQuery() == 0)
            {
                using var verify = Command(tx, "SELECT data FROM content WHERE tenant=$t AND hash=$hash", key);
                verify.Parameters.AddWithValue("$hash", hash);
                if (verify.ExecuteScalar() is not byte[] stored || !bytes.AsSpan().SequenceEqual(stored))
                    throw RevisionContent.Error("digest");
            }
            using var link = Command(tx, "INSERT INTO inputs VALUES ($t,$m,$r,$hash)", key);
            link.Parameters.AddWithValue("$hash", hash);
            link.ExecuteNonQuery();
        }
        tx.Commit();
        return key;
    }

    public StoredRevision? Find(string trustedTenant, string modelId, string revisionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedTenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        var found = Load(tx, new(trustedTenant, modelId, revisionId), checkHashes: true);
        tx.Commit();
        return found;
    }

    internal static StoredRevision? Load(SqliteTransaction tx, GraphRevision key, bool checkHashes)
    {
        using var query = Command(tx, "SELECT model,graph,model_hash,graph_hash FROM revisions WHERE " + Where, key);
        byte[] modelBytes, graphBytes;
        using (var row = query.ExecuteReader())
        {
            if (!row.Read()) return null;
            modelBytes = (byte[])row[0];
            graphBytes = (byte[])row[1];
            if (checkHashes && (!Equals(row[2], RevisionContent.Hash(modelBytes)) || !Equals(row[3], RevisionContent.Hash(graphBytes))))
                throw RevisionContent.Error("integrity");
        }
        var model = UdirDocument.Read(modelBytes);
        if (RevisionContent.Key(model) != key) throw RevisionContent.Error("scope");
        var graph = GraphData.Decode(graphBytes, key);
        using var blobs = Command(tx, """
            SELECT i.hash,c.data FROM inputs i LEFT JOIN content c ON c.tenant=i.tenant AND c.hash=i.hash
            WHERE i.tenant=$t AND i.model_id=$m AND i.revision_id=$r
            """, key);
        using var reader = blobs.ExecuteReader();
        var inputs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader[1] is not byte[] data) throw RevisionContent.Error("inputs");
            inputs.Add(reader.GetString(0), data);
        }
        RevisionContent.Validate(model, inputs);
        return new(model, graph, inputs.ToImmutableDictionary(p => p.Key, p => ImmutableArray.Create(p.Value), StringComparer.Ordinal));
    }
}
