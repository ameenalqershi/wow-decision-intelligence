using Microsoft.Data.Sqlite;
using WowDi.ModelRegistry;

namespace WowDi.Verification.Evidence;

public sealed record EvidenceKey(string TenantId,string ModelId,string RevisionId,string EvidenceId);

/// <summary>Local workspace reference store; path and tenant namespace are host-authorized.
/// Never expose to untrusted HTTP callers. S12/E02 own server-side atomic publication.
/// This store owns a separate evidence DB, not M11's schema or migration state.</summary>
public sealed class SqliteEvidenceStore
{
    private const int ApplicationId=1464092997; // WDIE
    private const int SchemaVersion=1;
    private readonly string _connectionString;
    private readonly SqliteModelRegistry _registry;

    public SqliteEvidenceStore(string databasePath,SqliteModelRegistry registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(registry);
        _registry=registry;
        _connectionString=new SqliteConnectionStringBuilder
        {
            DataSource=Path.GetFullPath(databasePath),ForeignKeys=true,Pooling=false,DefaultTimeout=15
        }.ToString();
        using var connection=Open();
        using var tx=connection.BeginTransaction(deferred:false);
        using var app=Command(tx,"PRAGMA application_id");
        using var version=Command(tx,"PRAGMA user_version");
        long actualApp=(long)app.ExecuteScalar()!;
        long actualVersion=(long)version.ExecuteScalar()!;
        if(actualApp==0&&actualVersion==0)
        {
            using var check=Command(tx,"SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'");
            if((long)check.ExecuteScalar()! !=0)throw EvidenceRecord.Error("storeIdentity");
            using var create=Command(tx,"""
                CREATE TABLE evidence(
                  tenant TEXT NOT NULL COLLATE BINARY, model_id TEXT NOT NULL COLLATE BINARY,
                  revision_id TEXT NOT NULL COLLATE BINARY, evidence_id TEXT NOT NULL COLLATE BINARY,
                  run_id TEXT NOT NULL COLLATE BINARY, attempt_id TEXT NOT NULL COLLATE BINARY,
                  parent_id TEXT COLLATE BINARY,
                  manifest BLOB NOT NULL, catalog BLOB NOT NULL, capabilities BLOB NOT NULL,
                  solver_result BLOB NOT NULL, assessment BLOB NOT NULL, settings BLOB NOT NULL,
                  PRIMARY KEY(tenant,model_id,revision_id,evidence_id),
                  UNIQUE(tenant,model_id,revision_id,run_id,attempt_id),
                  FOREIGN KEY(tenant,model_id,revision_id,parent_id)
                    REFERENCES evidence(tenant,model_id,revision_id,evidence_id) ON DELETE RESTRICT,
                  CHECK(parent_id IS NULL OR parent_id<>evidence_id),
                  CHECK(length(evidence_id)=64 AND evidence_id NOT GLOB '*[^0-9a-f]*'),
                  CHECK(length(manifest) BETWEEN 1 AND 1048576 AND length(catalog) BETWEEN 1 AND 262144
                    AND length(capabilities) BETWEEN 1 AND 1048576 AND length(solver_result) BETWEEN 1 AND 1048576
                    AND length(assessment) BETWEEN 1 AND 1048576 AND length(settings) BETWEEN 1 AND 65536)
                ) STRICT;
                CREATE TRIGGER evidence_no_update BEFORE UPDATE ON evidence
                BEGIN SELECT RAISE(ABORT,'immutable evidence'); END;
                CREATE TRIGGER evidence_no_delete BEFORE DELETE ON evidence
                BEGIN SELECT RAISE(ABORT,'immutable evidence'); END;
                """);
            create.ExecuteNonQuery();
            using var identity=Command(tx,$"PRAGMA application_id={ApplicationId}; PRAGMA user_version={SchemaVersion};");
            identity.ExecuteNonQuery();
        }
        else if(actualApp!=ApplicationId||actualVersion!=SchemaVersion)
            throw EvidenceRecord.Error("storeIdentity");
        tx.Commit();
    }

    public EvidenceKey Save(string trustedTenant,EvidenceRecord record,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();
        var scope=record.Manifest.Binding.Scope;
        var key=new EvidenceKey(scope.TenantId,scope.ModelId,scope.RevisionId,record.EvidenceId);
        ValidateKey(trustedTenant,key);
        var revision=_registry.Find(trustedTenant,key.ModelId,key.RevisionId)??throw EvidenceRecord.Error("revisionMissing");
        // Recompute against the actual immutable M11 registry, not an object supplied by a client.
        var verified=EvidenceRecord.Replay(revision,trustedTenant,key.EvidenceId,record.ManifestBytes.ToArray(),
            record.CatalogBytes.ToArray(),record.CapabilitiesBytes.ToArray(),record.SolverResultBytes.ToArray(),
            record.AssessmentBytes.ToArray(),record.SettingsBytes.ToArray(),ct);
        using var connection=Open();
        using var tx=connection.BeginTransaction(deferred:false);
        var prior=Load(tx,key,revision,ct);
        if(prior is not null)
        {
            if(!Identical(prior,verified))throw EvidenceRecord.Error("conflict");
            tx.Commit();return key;
        }
        var parent=verified.Manifest.ParentEvidenceId;
        if(parent is not null&&Load(tx,key with{EvidenceId=parent},revision,ct) is null)
            throw EvidenceRecord.Error("parentMissing");
        using var command=Command(tx,"""
            INSERT INTO evidence(tenant,model_id,revision_id,evidence_id,run_id,attempt_id,parent_id,manifest,catalog,capabilities,solver_result,assessment,settings)
            VALUES($t,$m,$r,$e,$run,$attempt,$parent,$manifest,$catalog,$capabilities,$result,$assessment,$settings)
            ON CONFLICT(tenant,model_id,revision_id,run_id,attempt_id) DO NOTHING
            """,key);
        command.Parameters.AddWithValue("$run",scope.RunId);
        command.Parameters.AddWithValue("$attempt",verified.Manifest.Invocation.AttemptId);
        command.Parameters.AddWithValue("$parent",(object?)parent??DBNull.Value);
        command.Parameters.AddWithValue("$manifest",verified.ManifestBytes.ToArray());
        command.Parameters.AddWithValue("$catalog",verified.CatalogBytes.ToArray());
        command.Parameters.AddWithValue("$capabilities",verified.CapabilitiesBytes.ToArray());
        command.Parameters.AddWithValue("$result",verified.SolverResultBytes.ToArray());
        command.Parameters.AddWithValue("$assessment",verified.AssessmentBytes.ToArray());
        command.Parameters.AddWithValue("$settings",verified.SettingsBytes.ToArray());
        if(command.ExecuteNonQuery()!=1)throw EvidenceRecord.Error("attemptConflict");
        ct.ThrowIfCancellationRequested();
        tx.Commit();return key;
    }

    public EvidenceRecord? Find(string trustedTenant,EvidenceKey key,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateKey(trustedTenant,key);
        ct.ThrowIfCancellationRequested();
        // Both M11 and evidence are immutable; no revision deletion in this storage profile.
        // Host backup/restore must preserve both files. No cross-database transaction is claimed.
        var revision=_registry.Find(trustedTenant,key.ModelId,key.RevisionId);
        using var connection=Open();
        using var tx=connection.BeginTransaction(deferred:true);
        if(revision is null)
        {
            using var exists=Command(tx,"SELECT 1 FROM evidence WHERE tenant=$t AND model_id=$m AND revision_id=$r AND evidence_id=$e",key);
            if(exists.ExecuteScalar() is not null)throw EvidenceRecord.Error("revisionMissing");
            tx.Commit();return null;
        }
        var result=Load(tx,key,revision,ct);
        tx.Commit();return result;
    }

    private static EvidenceRecord? Load(SqliteTransaction tx,EvidenceKey key,StoredRevision revision,CancellationToken ct)
    {
        using var command=Command(tx,"""
            SELECT run_id,attempt_id,parent_id,manifest,catalog,capabilities,solver_result,assessment,settings
            FROM evidence WHERE tenant=$t AND model_id=$m AND revision_id=$r AND evidence_id=$e
            """,key);
        using var reader=command.ExecuteReader();
        if(!reader.Read())return null;
        var record=EvidenceRecord.Replay(revision,key.TenantId,key.EvidenceId,(byte[])reader[3],(byte[])reader[4],
            (byte[])reader[5],(byte[])reader[6],(byte[])reader[7],(byte[])reader[8],ct);
        var scope=record.Manifest.Binding.Scope;
        if(scope.ModelId!=key.ModelId||scope.RevisionId!=key.RevisionId||
            scope.RunId!=reader.GetString(0)||record.Manifest.Invocation.AttemptId!=reader.GetString(1)||
            record.Manifest.ParentEvidenceId!=(reader.IsDBNull(2)?null:reader.GetString(2)))
            throw EvidenceRecord.Error("storeScope");
        reader.Close();
        if(record.Manifest.ParentEvidenceId is { } parent)
        {
            using var prior=Command(tx,"SELECT manifest FROM evidence WHERE tenant=$t AND model_id=$m AND revision_id=$r AND evidence_id=$e",key with{EvidenceId=parent});
            if(prior.ExecuteScalar() is not byte[] bytes||EvidenceRecord.Hash(bytes)!=parent)throw EvidenceRecord.Error("parentMissingOrCorrupt");
        }
        return record;
    }

    private SqliteConnection Open()
    {
        var db=new SqliteConnection(_connectionString);
        try{db.Open();return db;}catch{db.Dispose();throw;}
    }
    private static SqliteCommand Command(SqliteTransaction tx,string sql,EvidenceKey? key=null)
    {
        var command=tx.Connection!.CreateCommand();
        command.Transaction=tx;command.CommandText=sql;
        if(key is not null)
        {
            command.Parameters.AddWithValue("$t",key.TenantId);
            command.Parameters.AddWithValue("$m",key.ModelId);
            command.Parameters.AddWithValue("$r",key.RevisionId);
            command.Parameters.AddWithValue("$e",key.EvidenceId);
        }
        return command;
    }
    private static void ValidateKey(string tenant,EvidenceKey key)
    {
        if(!EvidenceRecord.Id(tenant)||tenant!=key.TenantId||!EvidenceRecord.Id(key.ModelId)||
            !EvidenceRecord.Id(key.RevisionId)||!EvidenceRecord.Digest(key.EvidenceId))throw EvidenceRecord.Error("scope");
    }
    private static bool Identical(EvidenceRecord a,EvidenceRecord b)=>
        a.ManifestBytes.AsSpan().SequenceEqual(b.ManifestBytes.AsSpan())&&a.CatalogBytes.AsSpan().SequenceEqual(b.CatalogBytes.AsSpan())&&
        a.CapabilitiesBytes.AsSpan().SequenceEqual(b.CapabilitiesBytes.AsSpan())&&a.SolverResultBytes.AsSpan().SequenceEqual(b.SolverResultBytes.AsSpan())&&
        a.AssessmentBytes.AsSpan().SequenceEqual(b.AssessmentBytes.AsSpan())&&a.SettingsBytes.AsSpan().SequenceEqual(b.SettingsBytes.AsSpan());
}
