using System.Data;
using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WowDi.Contracts;
using WowDi.DecisionGraph;
using WowDi.Enterprise.Identity;
using WowDi.Udir;
using Xunit;

namespace WowDi.Enterprise.Persistence.Tests;

public sealed class PostgresTests : IAsyncLifetime
{
    private string _admin = null!;
    private string _name = null!;
    private string _connection = null!;
    private DbContextOptions<EnterpriseDb> _options = null!;
    private readonly List<string> _databases = [];

    public async Task InitializeAsync()
    {
        _admin = Environment.GetEnvironmentVariable("WOWDI_PG")
            ?? throw new InvalidOperationException("WOWDI_PG must identify a disposable PostgreSQL test server with CREATEDB permission.");
        _name = await CreateDatabaseAsync();
        _connection = Connection(_name);
        _options = EnterpriseDb.Configure(_connection);
        await using var db = new EnterpriseDb(_options);
        await db.Database.MigrateAsync();
    }

    private string Connection(string database) => new NpgsqlConnectionStringBuilder(_admin)
        { Database = database, Pooling = false }.ConnectionString;

    private async Task<string> CreateDatabaseAsync()
    {
        var name = "wowdi_e02_" + Guid.NewGuid().ToString("N");
        await using var conn = new NpgsqlConnection(_admin);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
        await cmd.ExecuteNonQueryAsync();
        _databases.Add(name);
        return name;
    }

    public async Task DisposeAsync()
    {
        if (_admin is null) return;
        await using var conn = new NpgsqlConnection(_admin);
        await conn.OpenAsync();
        foreach (var name in _databases)
        {
            await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static byte[] Fixture(string ext) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "factory." + ext));
    private static UdirDocument Doc(string tenant = "fixture", string revision = "r17") =>
        UdirDocument.Create(UdirDocument.Read(Fixture("json")).Model with { TenantId = tenant, RevisionId = revision });
    private static GraphRevision Key(UdirDocument doc) => new(doc.Model.TenantId, doc.Model.ModelId, doc.Model.RevisionId);
    private static DecisionGraphSnapshot Graph(UdirDocument doc, decimal capacity = 8) =>
        DecisionGraphSnapshot.Create(Key(doc),
            [new("machine", EntityKind.Resource, new("line","machine-1","v1",DateTimeOffset.UnixEpoch)),
             new("task", EntityKind.Activity, new("line","machine-1","v1",DateTimeOffset.UnixEpoch))],
            [new("capacity",RelationKind.Capacity,"machine",["task"],new("line","machine-1","v1",DateTimeOffset.UnixEpoch),
                Capacity:new(capacity,"minute"))]);
    private static Dictionary<string,ReadOnlyMemory<byte>> Inputs()
    {
        var bytes = Fixture("txt");
        return new() { [Convert.ToHexStringLower(SHA256.HashData(bytes))] = bytes };
    }
    private async Task<int> ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var conn = new NpgsqlConnection(_connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddRange(parameters);
        return await cmd.ExecuteNonQueryAsync();
    }
    private async Task<long> CountAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task MigrationsAreRepeatableAndNoPendingModelChanges()
    {
        await using var db = new EnterpriseDb(_options);
        Assert.Single(await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        await db.Database.MigrateAsync();
        Assert.Single(await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(4L, await CountAsync("SELECT count(*) FROM information_schema.tables WHERE table_schema='wowdi' AND table_name IN ('revisions','content','inputs','jobs')"));
    }

    [Fact]
    public async Task RevisionsKeepExactBytesAndTenantScopedContent()
    {
        using var a = new Auth("fixture");
        using var b = new Auth("other");
        var storeA = new EnterpriseRepository(_options,a.Access,TimeProvider.System);
        var storeB = new EnterpriseRepository(_options,b.Access,TimeProvider.System);
        var docA = Doc(); var docB = Doc("other"); var next = Doc(revision:"r18");
        var bytes = Fixture("txt");
        var inputs = Inputs();
        byte[] binary = [0xff,0x00,0x01];
        var digest = Convert.ToHexStringLower(SHA256.HashData(binary));
        inputs.Add(digest,binary);
        docA = UdirDocument.Create(docA.Model with { InputDigests = docA.Model.InputDigests.Add(digest) });
        Assert.Equal(Key(docA),await storeA.SaveRevisionAsync(docA,Graph(docA),inputs));
        Assert.Equal(Key(next),await storeA.SaveRevisionAsync(next,Graph(next,4),Inputs()));
        Assert.Equal(Key(docB),await storeB.SaveRevisionAsync(docB,Graph(docB,3),Inputs()));
        Array.Clear(binary);
        Assert.Null(await storeA.FindRevisionAsync(Key(docB)));
        Assert.Null(await storeA.SaveRevisionAsync(docB,Graph(docB),Inputs()));
        Assert.Null(await storeA.FindRevisionAsync(new("fixture","factory","missing")));
        var found = Assert.IsType<EnterpriseRevision>(await new EnterpriseRepository(_options,a.Access,TimeProvider.System).FindRevisionAsync(Key(docA)));
        Assert.Equal(docA.Write(),found.Document.Write());
        Assert.Equal(bytes,found.Inputs[docA.Model.Sources[0].Sha256].ToArray());
        Assert.Equal(new byte[]{255,0,1},found.Inputs[digest].ToArray());
        Assert.Equal(8m,found.Graph.Relationships["capacity"].Capacity!.Amount);
        Assert.Equal(3m,(await storeB.FindRevisionAsync(Key(docB)))!.Graph.Relationships["capacity"].Capacity!.Amount);
        Assert.Equal(3L,await CountAsync("SELECT count(*) FROM wowdi.content"));
        Assert.Equal(3L,await CountAsync("SELECT count(*) FROM wowdi.revisions"));
        Assert.Equal(4L,await CountAsync("SELECT count(*) FROM wowdi.inputs"));
    }

    [Fact]
    public async Task ConcurrentConflictingWritesNeverOverwriteAndIdenticalRetriesConverge()
    {
        using var auth = new Auth("fixture");
        var store = new EnterpriseRepository(_options,auth.Access,TimeProvider.System);
        var doc = Doc();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Write(decimal capacity)
        {
            await start.Task;
            try { await store.SaveRevisionAsync(doc,Graph(doc,capacity),Inputs()); return "saved"; }
            catch (ContractException e) { return e.Code; }
        }
        var tasks = Enumerable.Range(1,12).Select(i => Write(i)).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(tasks);
        Assert.Single(results,x=>x=="saved");
        Assert.Equal(11,results.Count(x=>x=="registry.revisionConflict"));
        var winner = (await store.FindRevisionAsync(Key(doc)))!.Graph.Relationships["capacity"].Capacity!.Amount;
        Assert.All(await Task.WhenAll(Enumerable.Range(0,12).Select(_=>Write(winner))),x=>Assert.Equal("saved",x));
        Assert.Equal(1L,await CountAsync("SELECT count(*) FROM wowdi.revisions"));
        Assert.Equal(1L,await CountAsync("SELECT count(*) FROM wowdi.content"));
    }

    [Fact]
    public async Task FailedWritesRollBackAndDatabaseEnforcesTenantForeignKeys()
    {
        using var auth = new Auth("fixture");
        var store = new EnterpriseRepository(_options,auth.Access,TimeProvider.System);
        var doc = Doc();
        var wrong = Inputs(); wrong[wrong.Keys.Single()] = new byte[]{0};
        Assert.Equal("registry.digest",(await Assert.ThrowsAsync<ContractException>(()=>store.SaveRevisionAsync(doc,Graph(doc),wrong))).Code);
        Assert.Equal("registry.scope",(await Assert.ThrowsAsync<ContractException>(()=>store.SaveRevisionAsync(doc,Graph(Doc("other")),Inputs()))).Code);
        await ExecuteAsync("""
            CREATE FUNCTION wowdi.stop_input() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture failure'; END $$;
            CREATE TRIGGER stop_input BEFORE INSERT ON wowdi.inputs FOR EACH ROW EXECUTE FUNCTION wowdi.stop_input();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(()=>store.SaveRevisionAsync(doc,Graph(doc),Inputs()));
        Assert.Equal(0L,await CountAsync("SELECT count(*) FROM wowdi.revisions"));
        Assert.Equal(0L,await CountAsync("SELECT count(*) FROM wowdi.content"));
        await ExecuteAsync("DROP TRIGGER stop_input ON wowdi.inputs");
        Assert.NotNull(await store.SaveRevisionAsync(doc,Graph(doc),Inputs()));
        var error = await Assert.ThrowsAsync<PostgresException>(()=>ExecuteAsync(
            "INSERT INTO wowdi.inputs(tenant,model_id,revision_id,hash) VALUES ('other','factory','r17',@hash)",
            new NpgsqlParameter("hash",Inputs().Keys.Single())));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,error.SqlState);
        await ExecuteAsync("UPDATE wowdi.content SET data=decode('00','hex') WHERE tenant='fixture'");
        Assert.Equal("registry.digest",(await Assert.ThrowsAsync<ContractException>(()=>store.FindRevisionAsync(Key(doc)))).Code);
    }

    [Fact]
    public async Task JobAdmissionIsIdempotentAndIsolatedAcrossTenants()
    {
        using var a = new Auth("fixture");
        using var b = new Auth("other");
        using var otherActor = new Auth("fixture","another-subject");
        var storeA = new EnterpriseRepository(_options,a.Access,TimeProvider.System);
        var storeB = new EnterpriseRepository(_options,b.Access,TimeProvider.System);
        var third = new EnterpriseRepository(_options,otherActor.Access,TimeProvider.System);
        var docA = Doc(); var docB = Doc("other");
        await storeA.SaveRevisionAsync(docA,Graph(docA),Inputs());
        await storeB.SaveRevisionAsync(docB,Graph(docB),Inputs());
        byte[] body = Encoding.UTF8.GetBytes("frozen-request-fixture");
        var jobs = await Task.WhenAll(Enumerable.Range(0,16).Select(_=>storeA.AdmitJobAsync(Key(docA),"retry-key",body)));
        Assert.All(jobs,x=>Assert.NotNull(x));
        var first = jobs[0]!;
        Assert.All(jobs,x=>Assert.Equal(first.JobId,x!.JobId));
        Assert.Equal("Queued",first.State);
        Assert.Equal("tester",first.Subject);
        Assert.Equal("fixture",first.TenantId);
        Assert.Equal("registry.idempotencyConflict",(await Assert.ThrowsAsync<ContractException>(()=>storeA.AdmitJobAsync(Key(docA),"retry-key",new byte[]{42}))).Code);
        Assert.Equal("registry.idempotencyConflict",(await Assert.ThrowsAsync<ContractException>(()=>third.AdmitJobAsync(Key(docA),"retry-key",body))).Code);
        var second = Assert.IsType<StoredJob>(await storeB.AdmitJobAsync(Key(docB),"retry-key",body));
        Assert.NotEqual(first.JobId,second.JobId);
        Assert.Null(await storeA.AdmitJobAsync(Key(docB),"foreign",body));
        Assert.Null(await storeA.AdmitJobAsync(new("fixture","factory","absent"),"missing",body));
        Assert.Null(await storeA.FindJobAsync(new("other","factory"),second.JobId));
        Assert.Null(await storeA.FindJobAsync(new("fixture","factory"),second.JobId));
        var found = Assert.IsType<StoredJob>(await storeA.FindJobAsync(new("fixture","factory"),first.JobId));
        Assert.Equal(body,found.Request.ToArray());
        Assert.Equal(2L,await CountAsync("SELECT count(*) FROM wowdi.jobs"));
        await ExecuteAsync("UPDATE wowdi.jobs SET request=decode('00','hex') WHERE tenant='fixture'");
        Assert.Equal("registry.integrity",(await Assert.ThrowsAsync<ContractException>(()=>storeA.FindJobAsync(new("fixture","factory"),first.JobId))).Code);
    }

    [Fact]
    public async Task ActualPgDumpAndRestorePreservesRevisionsJobsAndTenantIsolation()
    {
        using var a = new Auth("fixture");
        using var b = new Auth("other");
        var storeA = new EnterpriseRepository(_options,a.Access,TimeProvider.System);
        var storeB = new EnterpriseRepository(_options,b.Access,TimeProvider.System);
        var docA = Doc(); var docB = Doc("other");
        await storeA.SaveRevisionAsync(docA,Graph(docA,8),Inputs());
        await storeB.SaveRevisionAsync(docB,Graph(docB,3),Inputs());
        var job = Assert.IsType<StoredJob>(await storeA.AdmitJobAsync(Key(docA),"backup",new byte[]{1,2,3}));
        var restoredName = await CreateDatabaseAsync();
        var path = Path.Combine(Path.GetTempPath(),"wowdi-e02-"+Guid.NewGuid().ToString("N")+".dump");
        try
        {
            await PgTool("pg_dump",_name,["--format=custom","--no-owner","--no-privileges","--file",path]);
            Assert.True(new FileInfo(path).Length>0);
            await PgTool("pg_restore",restoredName,["--exit-on-error","--no-owner","--no-privileges",path]);
            var restored = EnterpriseDb.Configure(Connection(restoredName));
            await using var db = new EnterpriseDb(restored);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            var restoredA = new EnterpriseRepository(restored,a.Access,TimeProvider.System);
            var restoredB = new EnterpriseRepository(restored,b.Access,TimeProvider.System);
            var revision = Assert.IsType<EnterpriseRevision>(await restoredA.FindRevisionAsync(Key(docA)));
            Assert.Equal(docA.Write(),revision.Document.Write());
            Assert.Equal(8m,revision.Graph.Relationships["capacity"].Capacity!.Amount);
            Assert.Equal(Fixture("txt"),revision.Inputs[docA.Model.Sources[0].Sha256].ToArray());
            Assert.Null(await restoredA.FindRevisionAsync(Key(docB)));
            Assert.Equal(3m,(await restoredB.FindRevisionAsync(Key(docB)))!.Graph.Relationships["capacity"].Capacity!.Amount);
            Assert.Equal(job.Request.ToArray(),(await restoredA.FindJobAsync(new("fixture","factory"),job.JobId))!.Request.ToArray());
            Assert.Equal(job.JobId,(await restoredA.AdmitJobAsync(Key(docA),"backup",new byte[]{1,2,3}))!.JobId);
        }
        finally { File.Delete(path); }
    }

    private async Task PgTool(string tool,string database,string[] args)
    {
        var c = new NpgsqlConnectionStringBuilder(_admin);
        var info = new ProcessStartInfo(tool) { RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false };
        foreach (var arg in new[]{"--host",c.Host!,"--port",c.Port.ToString(),"--username",c.Username!,"--dbname",database,"--no-password"}.Concat(args))
            info.ArgumentList.Add(arg);
        info.Environment["PGPASSWORD"]=c.Password ?? "";
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start PostgreSQL client.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree:true); throw; }
        await output;
        var diagnostic = await error;
        Assert.True(process.ExitCode==0,tool+" failed: "+diagnostic);
    }

    private sealed class Auth : IDisposable
    {
        private readonly ServiceProvider _provider;
        public TenantAccess Access { get; }
        public Auth(string tenant,string subject="tester")
        {
            var services = new ServiceCollection();
            services.AddSingleton<IAuthenticationService>(new Authentication(tenant,subject));
            _provider = services.BuildServiceProvider();
            var ctx = new DefaultHttpContext { RequestServices=_provider };
            Access = new(new Accessor { HttpContext=ctx },new Directory(),new Audit(),new("https://issuer.example.test","api"));
        }
        public void Dispose()=>_provider.Dispose();
    }
    private sealed class Accessor : IHttpContextAccessor { public HttpContext? HttpContext {get;set;} }
    private sealed class Directory : ITenantMembershipDirectory
    {
        public ValueTask<bool> IsActiveAsync(TenantActor actor,CancellationToken ct)=>ValueTask.FromResult(true);
        public ValueTask<bool> AllowsAsync(TenantActor actor,TenantPermission permission,string model,CancellationToken ct)=>
            ValueTask.FromResult(model=="factory" && permission is TenantPermission.View or TenantPermission.Write or TenantPermission.Propose);
    }
    private sealed class Audit : ITenantDenialAudit
    {
        public ValueTask RecordAsync(TenantDenial denial,CancellationToken ct)=>ValueTask.CompletedTask;
    }
    // Only the identity seam is synthetic. Database, migration, constraints, concurrency and backup are real.
    private sealed class Authentication(string tenant,string subject) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext ctx,string? scheme)
        {
            Assert.Equal("Bearer",scheme);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new("iss","https://issuer.example.test"),new("sub",subject),new("tenant_id",tenant)],"Bearer"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal,"Bearer")));
        }
        public Task ChallengeAsync(HttpContext c,string? s,AuthenticationProperties? p)=>throw new NotSupportedException();
        public Task ForbidAsync(HttpContext c,string? s,AuthenticationProperties? p)=>throw new NotSupportedException();
        public Task SignInAsync(HttpContext c,string? s,ClaimsPrincipal p,AuthenticationProperties? a)=>throw new NotSupportedException();
        public Task SignOutAsync(HttpContext c,string? s,AuthenticationProperties? p)=>throw new NotSupportedException();
    }
}
