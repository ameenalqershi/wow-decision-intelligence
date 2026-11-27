using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WowDi.Contracts;
using WowDi.DecisionGraph;
using WowDi.ModelRegistry;
using WowDi.Udir;
using Xunit;

namespace WowDi.Tests;

public sealed class ModelRegistryTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
    private static byte[] Fixture(string extension) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "factory." + extension));
    private static UdirDocument Model => UdirDocument.Read(Fixture("json"));
    private static readonly SourceStamp Stamp = new("line", "machine-1", "v1", DateTimeOffset.UnixEpoch);
    private static GraphRevision Key(UdirDocument doc) => new(doc.Model.TenantId, doc.Model.ModelId, doc.Model.RevisionId);
    private static DecisionGraphSnapshot Graph(UdirDocument doc, decimal capacity = 8) => DecisionGraphSnapshot.Create(Key(doc),
        [new("machine", EntityKind.Resource, Stamp), new("task", EntityKind.Activity, Stamp)],
        [new("capacity", RelationKind.Capacity, "machine", ["task"], Stamp, Capacity: new(capacity, "minute"))]);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static Dictionary<string, ReadOnlyMemory<byte>> Inputs(byte[]? bytes = null)
    {
        bytes ??= Fixture("txt");
        return new() { [Hash(bytes)] = bytes };
    }
    private void Sql(string sql, params (string, object)[] args)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value);
        cmd.ExecuteNonQuery();
    }
    private long Count(string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }
    public void Dispose() => File.Delete(path);

    [Fact]
    public void ReopenRetainsSnapshotsProvenanceAndTenantIsolation()
    {
        var store = new SqliteModelRegistry(path);
        var source = Fixture("txt");
        var inputs = Inputs(source);
        byte[] binary = [0xff, 0x00];
        inputs.Add(Hash(binary), binary);
        var doc = UdirDocument.Create(Model.Model with { InputDigests = Model.Model.InputDigests.Add(Hash(binary)) });
        store.Save("fixture", doc, Graph(doc), inputs);
        var newer = UdirDocument.Create(doc.Model with { RevisionId = "r18" });
        store.Save("fixture", newer, Graph(newer, 4), inputs);
        var other = UdirDocument.Create(doc.Model with { TenantId = "other" });
        store.Save("other", other, Graph(other, 3), inputs);
        Array.Clear(source);
        Array.Clear(binary);
        store = new(path);
        var old = Assert.IsType<StoredRevision>(store.Find("fixture", "factory", "r17"));
        Assert.Equal(doc.Write(), old.Document.Write());
        Assert.Equal(Fixture("txt"), old.Inputs[doc.Model.Sources[0].Sha256].ToArray());
        Assert.Equal(new byte[] { 255, 0 }, old.Inputs[doc.Model.InputDigests[1]].ToArray());
        var relation = old.Graph.Relationships["capacity"];
        Assert.Equal(Stamp, relation.Source);
        Assert.Equal("task", Assert.Single(relation.Targets));
        Assert.Equal(8m, relation.Capacity!.Amount);
        Assert.Equal(4m, store.Find("fixture", "factory", "r18")!.Graph.Relationships["capacity"].Capacity!.Amount);
        Assert.Equal(3m, store.Find("other", "factory", "r17")!.Graph.Relationships["capacity"].Capacity!.Amount);
        Assert.Null(store.Find("Fixture", "factory", "r17"));
        Assert.Equal(4L, Count("SELECT count(*) FROM content"));
    }

    [Theory]
    [InlineData("missing", "registry.inputs")]
    [InlineData("extra", "registry.inputs")]
    [InlineData("hash", "registry.digest")]
    [InlineData("length", "registry.sourceLength")]
    [InlineData("encoding", "registry.encoding")]
    [InlineData("tenant", "registry.scope")]
    [InlineData("graph", "registry.scope")]
    public void InvalidSubmissionsPublishNothing(string kind, string code)
    {
        var store = new SqliteModelRegistry(path);
        var doc = Model;
        var inputs = Inputs();
        var src = doc.Model.Sources.Single();
        if (kind == "missing") inputs.Clear();
        if (kind == "extra") inputs.Add(Hash([]), Array.Empty<byte>());
        if (kind == "hash") inputs[src.Sha256] = new byte[] { 0 };
        if (kind == "length") doc = UdirDocument.Create(doc.Model with { Sources = [src with { Utf16Length = 1000 }] });
        if (kind == "encoding")
        {
            byte[] invalid = [0xff];
            inputs = Inputs(invalid);
            doc = UdirDocument.Create(doc.Model with { InputDigests = [Hash(invalid)], Sources = [src with { Sha256 = Hash(invalid) }] });
        }
        var graph = Graph(kind == "graph" ? UdirDocument.Create(doc.Model with { RevisionId = "foreign" }) : doc);
        Assert.Equal(code, Assert.Throws<ContractException>(() => store.Save(kind == "tenant" ? "other" : "fixture", doc, graph, inputs)).Code);
        Assert.Equal(0L, Count("SELECT count(*) FROM revisions"));
    }

    [Fact]
    public async Task RetriesAndCompetingWritersPreserveFirstCommit()
    {
        var store = new SqliteModelRegistry(path);
        var doc = Model;
        async Task<string> Write(decimal capacity)
        {
            try { await Task.Run(() => store.Save("fixture", doc, Graph(doc, capacity), Inputs())); return "saved"; }
            catch (ContractException e) { return e.Code; }
        }
        var results = await Task.WhenAll(Write(1), Write(2));
        Assert.Contains("saved", results);
        Assert.Contains("registry.revisionConflict", results);
        var winner = store.Find("fixture", "factory", "r17")!.Graph.Relationships["capacity"].Capacity!.Amount;
        Assert.All(await Task.WhenAll(Write(winner), Write(winner)), x => Assert.Equal("saved", x));
        var different = UdirDocument.Create(doc.Model);
        Assert.Equal("registry.revisionConflict", Assert.Throws<ContractException>(() => store.Save("fixture", different, Graph(doc, winner), Inputs())).Code);
        Assert.Equal(1L, Count("SELECT count(*) FROM revisions"));
    }

    [Fact]
    public void FailureAfterRevisionAndBlobInsertRollsBackAndCanRetry()
    {
        var store = new SqliteModelRegistry(path);
        Sql("CREATE TRIGGER stop BEFORE INSERT ON inputs BEGIN SELECT RAISE(ABORT,'interrupted'); END");
        Assert.Throws<SqliteException>(() => store.Save("fixture", Model, Graph(Model), Inputs()));
        Assert.Equal(0L, Count("SELECT count(*) FROM revisions"));
        Assert.Equal(0L, Count("SELECT count(*) FROM content"));
        Sql("DROP TRIGGER stop");
        store.Save("fixture", Model, Graph(Model), Inputs());
    }

    private void SeedLegacy()
    {
        var model = Model;
        var graph = Graph(model);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { graph.Revision, Entities = graph.Entities.Values.ToArray(),
            Relationships = new[] { new RelationshipInput("capacity", RelationKind.Capacity, "machine", ["task"], Stamp, Capacity: new(8, "minute")) } });
        Sql("""
            CREATE TABLE revisions(tenant TEXT,model_id TEXT,revision_id TEXT,model BLOB,graph BLOB,PRIMARY KEY(tenant,model_id,revision_id));
            CREATE TABLE content(tenant TEXT,hash TEXT,data BLOB,PRIMARY KEY(tenant,hash));
            CREATE TABLE inputs(tenant TEXT,model_id TEXT,revision_id TEXT,hash TEXT,PRIMARY KEY(tenant,model_id,revision_id,hash));
            PRAGMA application_id=1464092999;
            PRAGMA user_version=1;
            INSERT INTO revisions VALUES ('fixture','factory','r17',$model,$graph);
            INSERT INTO content VALUES ('fixture',$hash,$source);
            INSERT INTO inputs VALUES ('fixture','factory','r17',$hash);
            """, ("$model", Fixture("json")), ("$graph", payload), ("$hash", model.Model.InputDigests[0]), ("$source", Fixture("txt")));
    }

    [Fact]
    public void PopulatedMigrationKeepsOriginalBytesAndCanBeReopened()
    {
        SeedLegacy();
        var upgraded = new SqliteModelRegistry(path);
        Assert.Equal(2L, Count("PRAGMA user_version"));
        Assert.Equal(Fixture("json").Length, Count("SELECT length(model) FROM revisions"));
        Assert.Equal(Model.Write(), upgraded.Find("fixture", "factory", "r17")!.Document.Write());
        new SqliteModelRegistry(path).Save("fixture", Model, Graph(Model), Inputs());
    }

    [Fact]
    public void CorruptLegacyContentRollsBackSchemaAndAllowsRepair()
    {
        SeedLegacy();
        Sql("UPDATE content SET data=x'00'");
        Assert.Equal("registry.digest", Assert.Throws<ContractException>(() => new SqliteModelRegistry(path)).Code);
        Assert.Equal(1L, Count("PRAGMA user_version"));
        Assert.Equal(0L, Count("SELECT count(*) FROM pragma_table_info('revisions') WHERE name='model_hash'"));
        Sql("UPDATE content SET data=$data", ("$data", Fixture("txt")));
        Assert.NotNull(new SqliteModelRegistry(path).Find("fixture", "factory", "r17"));
    }

    [Theory]
    [InlineData("UPDATE revisions SET graph=x'00'", "registry.integrity")]
    [InlineData("UPDATE revisions SET model=x'00'", "registry.integrity")]
    [InlineData("UPDATE content SET data=x'00'", "registry.digest")]
    [InlineData("DELETE FROM inputs", "registry.inputs")]
    public void DamagedStorageIsRejectedOnRead(string sql, string code)
    {
        var store = new SqliteModelRegistry(path);
        store.Save("fixture", Model, Graph(Model), Inputs());
        Sql(sql);
        Assert.Equal(code, Assert.Throws<ContractException>(() => store.Find("fixture", "factory", "r17")).Code);
    }

    [Theory]
    [InlineData("PRAGMA application_id=1464092999; PRAGMA user_version=3", 3)]
    [InlineData("CREATE TABLE unrelated(x)", 0)]
    public void RejectsForeignAndFutureSchemas(string sql, long version)
    {
        Sql(sql);
        Assert.Equal("registry.schema", Assert.Throws<ContractException>(() => new SqliteModelRegistry(path)).Code);
        Assert.Equal(version, Count("PRAGMA user_version"));
    }
}
