using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using WowDi.Contracts;
using WowDi.DecisionGraph;
using WowDi.Hosting;
using WowDi.Udir;
using Xunit;

namespace WowDi.Tests;

public sealed class LocalWorkspaceTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("wowdi-local-").FullName;
    private string PathFor(string name) => Path.Combine(root, name);
    private static byte[] Fixture(string ext) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "factory." + ext));
    private static UdirDocument Model => UdirDocument.Read(Fixture("json"));
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static Dictionary<string, ReadOnlyMemory<byte>> Inputs()
    {
        var bytes = Fixture("txt");
        return new() { [Digest(bytes)] = bytes };
    }
    private static DecisionGraphSnapshot Graph(UdirDocument model)
    {
        var stamp = new SourceStamp("fixture", "machine", "v1", DateTimeOffset.UnixEpoch);
        return DecisionGraphSnapshot.Create(new(model.Model.TenantId, model.Model.ModelId, model.Model.RevisionId),
            [new("machine", EntityKind.Resource, stamp), new("task", EntityKind.Activity, stamp)],
            [new("capacity", RelationKind.Capacity, "machine", ["task"], stamp, Capacity: new(8, "minute"))]);
    }
    private LocalWorkspace Create(string name = "a", string tenant = "fixture") => LocalWorkspace.Create(PathFor(name), tenant);
    private static void Save(LocalWorkspace workspace, UdirDocument? model = null)
    {
        model ??= Model;
        workspace.SaveRevision(model, Graph(model), Inputs());
    }
    private static void Sql(LocalWorkspace workspace, string statement)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(workspace.DirectoryPath, "workspace.db"), Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = statement;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void ReopenAndReplayFrozenRevisionPreservesDigest()
    {
        var a = Create();
        Save(a);
        var identity = a.Identity;
        var export = LocalRevisionTransfer.Export(a, "factory", "r17");
        a = LocalWorkspace.Open(PathFor("a"));
        Assert.Equal(identity, a.Identity);
        Assert.Equal(new GraphRevision("fixture", "factory", "r17"), Assert.Single(a.ListRevisions()));
        Assert.Equal(Model.Write(), a.FindRevision("factory", "r17")!.Document.Write());
        var b = Create("b");
        Assert.Equal(new GraphRevision("fixture", "factory", "r17"), LocalRevisionTransfer.Import(b, export));
        Assert.Equal(export.Digest, LocalRevisionTransfer.Export(b, "factory", "r17").Digest);
        Assert.Equal(export.Bytes.ToArray(), LocalRevisionTransfer.Export(b, "factory", "r17").Bytes.ToArray());
        Assert.Equal(export.Digest, LocalRevisionTransfer.Export(a, "factory", "r17").Digest);
        Assert.Equal(new GraphRevision("fixture", "factory", "r17"), LocalRevisionTransfer.Import(b, export));
    }

    [Fact]
    public void RejectsExistingDirectoriesAndUnknownWorkspaceWithoutWriting()
    {
        var existing = Directory.CreateDirectory(PathFor("existing")).FullName;
        File.WriteAllText(Path.Combine(existing, "marker.txt"), "retain");
        Assert.Throws<ContractException>(() => LocalWorkspace.Create(existing, "fixture"));
        Assert.Throws<ContractException>(() => LocalWorkspace.Open(existing));
        Assert.Single(Directory.GetFiles(existing));
        Assert.Equal("retain", File.ReadAllText(Path.Combine(existing, "marker.txt")));
        var a = Create();
        Assert.Throws<ContractException>(() => LocalWorkspace.Create(a.DirectoryPath, "fixture"));
        Assert.Equal(a.Identity, LocalWorkspace.Open(a.DirectoryPath).Identity);
    }

    [Fact]
    public void RejectsFutureWorkspaceSchemaBeforeRegistryCanMigrate()
    {
        var a = Create();
        Sql(a, "UPDATE workspace_identity SET schema_version=99; PRAGMA user_version=1;");
        Assert.Equal("workspace.schema", Assert.Throws<ContractException>(() => LocalWorkspace.Open(a.DirectoryPath)).Code);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(a.DirectoryPath, "workspace.db"), Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    [Fact]
    public void CrossTenantAndTamperedArchiveNeverPublishRevision()
    {
        var a = Create();
        Save(a);
        var export = LocalRevisionTransfer.Export(a, "factory", "r17");
        var b = Create("b", "other");
        Assert.Equal("workspace.scope", Assert.Throws<ContractException>(() => LocalRevisionTransfer.Import(b, export)).Code);
        Assert.Empty(b.ListRevisions());
        var c = Create("c");
        var modified = export.Bytes.ToArray();
        modified[^1] ^= 1;
        Assert.Equal("workspace.archiveDigest", Assert.Throws<ContractException>(() =>
            LocalRevisionTransfer.Import(c, export with { Bytes = ImmutableArray.CreateRange(modified) })).Code);
        Assert.Empty(c.ListRevisions());
    }

    [Fact]
    public void InterruptedImportRollsBackAndPreservesEarlierRevision()
    {
        var a = Create();
        Save(a);
        var b = Create("b");
        var older = UdirDocument.Create(Model.Model with { RevisionId = "r16" });
        Save(b, older);
        var export = LocalRevisionTransfer.Export(a, "factory", "r17");
        Sql(b, "CREATE TRIGGER fail_import BEFORE INSERT ON inputs BEGIN SELECT RAISE(ABORT,'interrupted'); END;");
        Assert.Throws<SqliteException>(() => LocalRevisionTransfer.Import(b, export));
        Assert.Equal("r16", Assert.Single(b.ListRevisions()).RevisionId);
        Assert.Null(b.FindRevision("factory", "r17"));
        Assert.NotNull(b.FindRevision("factory", "r16"));
        Sql(b, "DROP TRIGGER fail_import;");
        LocalRevisionTransfer.Import(b, export);
        Assert.Equal(2, b.ListRevisions().Count);
    }

    [Fact]
    public void RunArtifactIdentityIsImmutableScopedAndTamperChecked()
    {
        var a = Create();
        Save(a);
        Save(a, UdirDocument.Create(Model.Model with { RevisionId = "r18" }));
        // Synthetic storage fixture only; not a solver or verification result.
        byte[] data = [1, 2, 3];
        var stored = LocalRunArtifacts.Store(a, "factory", "r17", "run-1", data);
        data[0] = 0;
        a = LocalWorkspace.Open(a.DirectoryPath);
        var found = Assert.IsType<UnverifiedRunArtifact>(LocalRunArtifacts.Find(a, "factory", "r17", "run-1"));
        Assert.Equal(stored.Digest, found.Digest);
        Assert.Equal(new byte[] { 1, 2, 3 }, found.Bytes.ToArray());
        Assert.Equal("run-1", Assert.Single(LocalRunArtifacts.ListRunIds(a, "factory", "r17")));
        Assert.Null(LocalRunArtifacts.Find(a, "factory", "r18", "run-1"));
        Assert.Equal(stored.Digest, LocalRunArtifacts.Store(a, "factory", "r17", "run-1", new byte[] { 1, 2, 3 }).Digest);
        Assert.Equal("workspace.runConflict", Assert.Throws<ContractException>(() => LocalRunArtifacts.Store(a, "factory", "r18", "run-1", new byte[] { 1, 2, 3 })).Code);
        Assert.Equal("workspace.runConflict", Assert.Throws<ContractException>(() => LocalRunArtifacts.Store(a, "factory", "r17", "run-1", new byte[] { 4 })).Code);
        Sql(a, "UPDATE local_artifacts SET bytes=x'00'");
        Assert.Equal("workspace.artifactDigest", Assert.Throws<ContractException>(() => LocalRunArtifacts.Find(a, "factory", "r17", "run-1")).Code);
    }

    [Fact]
    public void HostUsesExplicitWorkspaceOnlyWhenResolved()
    {
        using var host = LocalHost.Build(new(PathFor("host"), "fixture"));
        Assert.False(Directory.Exists(PathFor("host")));
        var workspace = host.Services.GetRequiredService<LocalWorkspace>();
        Assert.Same(workspace, host.Services.GetRequiredService<LocalWorkspace>());
        Assert.Equal("fixture", workspace.Identity.TenantId);
        using var reopened = LocalHost.Build(new(PathFor("host")));
        Assert.Equal(workspace.Identity, reopened.Services.GetRequiredService<LocalWorkspace>().Identity);
    }
    [Fact]
    public void ArtifactWriteFailureLeavesNoPublishedHistory()
    {
        var a = Create();
        Save(a);
        Sql(a, "CREATE TRIGGER fail_artifact BEFORE INSERT ON local_artifacts BEGIN SELECT RAISE(ABORT, 'interrupted'); END;");
        Assert.Throws<SqliteException>(() => LocalRunArtifacts.Store(a, "factory", "r17", "run-1", new byte[] { 1 }));
        Assert.Empty(LocalRunArtifacts.ListRunIds(a, "factory", "r17"));
        Assert.NotNull(a.FindRevision("factory", "r17"));
        Sql(a, "DROP TRIGGER fail_artifact");
        LocalRunArtifacts.Store(a, "factory", "r17", "run-1", new byte[] { 1 });
        Assert.Single(LocalRunArtifacts.ListRunIds(a, "factory", "r17"));
    }

    [Fact]
    public void BackupRestorePreservesRevisionAndArtifactDigestsButSeparatesWorkspaceIdentity()
    {
        var a=Create(); Save(a);
        var artifact=LocalRunArtifacts.Store(a,"factory","r17","run-1",new byte[]{1,2,3});
        var settings=new LocalSettings(a); settings.Apply(new(a.Identity.WorkspaceId,0,LocalDiagnosticLevel.Debug));
        var b=WorkspaceBackup.RestoreCopy(a,PathFor("restored"));
        Assert.NotEqual(a.Identity.WorkspaceId,b.Identity.WorkspaceId);
        Assert.Equal(a.Identity.TenantId,b.Identity.TenantId);
        Assert.Equal(LocalRevisionTransfer.Export(a,"factory","r17").Digest,LocalRevisionTransfer.Export(b,"factory","r17").Digest);
        Assert.Equal(artifact.Digest,LocalRunArtifacts.Find(b,"factory","r17","run-1")!.Digest);
        Assert.Equal(LocalDiagnosticLevel.Debug,new LocalSettings(b).Read().DiagnosticLevel);
        Assert.Throws<ContractException>(()=>WorkspaceBackup.RestoreCopy(a,b.DirectoryPath));
        using var cancel=new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(()=>WorkspaceBackup.RestoreCopy(a,PathFor("cancelled"),cancel.Token));
        Assert.False(Directory.Exists(PathFor("cancelled")));
        Sql(a,"UPDATE local_artifacts SET bytes=x'00'");
        Assert.Throws<ContractException>(()=>WorkspaceBackup.RestoreCopy(a,PathFor("damaged")));
        Assert.False(Directory.Exists(PathFor("damaged")));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
