using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WowDi.Enterprise.Identity;
using WowDi.Integration.Snapshots;
using WowDi.Integration.Connectors;
using System.Text.Json;
using Xunit;

namespace WowDi.Enterprise.DataSecurity.Tests;

public sealed class DataSecurityTests
{
    private static MemoryStream Zip(params (string Name, byte[] Bytes, int? Attributes)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (name, bytes, attributes) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                if (attributes.HasValue) entry.ExternalAttributes = attributes.Value;
                using var writer = entry.Open();
                writer.Write(bytes);
            }
        stream.Position = 0;
        return stream;
    }
    private static (string, byte[], int?) File(string name, int length = 1) => (name, new byte[length], null);

    [Fact]
    public async Task ArchiveStagesValidFilesWithoutExtraction()
    {
        using var zip = Zip(File("data/orders.csv", 10), File("manifest.json", 5));
        var result = await BoundedArchive.ReadAsync(zip, new());
        Assert.Equal(2, result.Length);
        Assert.Equal("data/orders.csv", result[0].Name);
        Assert.Equal(10, result[0].Bytes.Length);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("data/../../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("a\\b.txt")]
    [InlineData("data//a.txt")]
    [InlineData("data/CON.txt")]
    [InlineData("data/a.txt.")]
    [InlineData("data/a.txt:stream")]
    [InlineData("data/")]
    public async Task UnsafeArchiveNamesFailClosed(string path)
    {
        using var zip = Zip(File(path));
        var ex = await Assert.ThrowsAsync<DataSecurityException>(() => BoundedArchive.ReadAsync(zip, new()));
        Assert.Equal("archive.path", ex.Code);
        Assert.DoesNotContain(path, ex.Message);
    }

    [Fact]
    public async Task RejectsDuplicateCaseCollisionsPrefixesAndLinks()
    {
        foreach (var entries in new[] {
            new[] { File("a.txt"), File("a.txt") },
            new[] { File("A.txt"), File("a.txt") },
            new[] { File("a"), File("a/b.txt") },
            new[] { ("link", new byte[] { 1 }, (int?)unchecked((int)0xa0000000)) },
            new[] { ("link", new byte[] { 1 }, (int?)0x400) } })
        {
            using var zip = Zip(entries);
            Assert.Equal("archive.path", (await Assert.ThrowsAsync<DataSecurityException>(() =>
                BoundedArchive.ReadAsync(zip, new()))).Code);
        }
    }

    [Fact]
    public async Task RejectsCompressedExpandedEntryAndCountLimits()
    {
        foreach (var limits in new[] {
            new ArchiveLimits(CompressedBytes: 10),
            new ArchiveLimits(ExpandedBytes: 100, EntryBytes: 100),
            new ArchiveLimits(ExpandedBytes: 5000, EntryBytes: 100),
            new ArchiveLimits(Entries: 1) })
        {
            using var zip = Zip(File("a.txt", 2000), File("b.txt", 2000));
            var ex = await Assert.ThrowsAsync<DataSecurityException>(() => BoundedArchive.ReadAsync(zip, limits));
            Assert.Contains(ex.Code, new[] { "archive.size", "archive.entries" });
        }
        using var total = Zip(File("a.txt", 80), File("b.txt", 80));
        Assert.Equal("archive.size", (await Assert.ThrowsAsync<DataSecurityException>(() =>
            BoundedArchive.ReadAsync(total, new(ExpandedBytes: 150, EntryBytes: 100)))).Code);
        using var corrupt = new MemoryStream([1,2,3]);
        Assert.Equal("archive.invalid", (await Assert.ThrowsAsync<DataSecurityException>(() =>
            BoundedArchive.ReadAsync(corrupt, new()))).Code);
    }

    [Fact]
    public async Task ArchiveRejectsSameLengthMemberWithCorruptDeclaredCrc()
    {
        using var original = Zip(File("valid.txt", 17), ("checksum.txt", Encoding.ASCII.GetBytes("123456789"), null));
        var bytes = original.ToArray();
        using (var valid = new MemoryStream(bytes))
        {
            var members = await BoundedArchive.ReadAsync(valid, new());
            Assert.Equal("123456789", Encoding.ASCII.GetString(members[1].Bytes.AsSpan()));
        }
        // ZIP central directory CRC-32 is at offset 16 in each file header.
        // Find the exact header with the known standard CRC-32 test vector.
        var found = false;
        for (var i = 0; i <= bytes.Length - 46; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) != 0x02014b50u ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 16, 4)) != 0xcbf43926u) continue;
            bytes[i + 16] ^= 1;
            found = true;
            break;
        }
        Assert.True(found);
        using var corrupt = new MemoryStream(bytes);
        var ex = await Assert.ThrowsAsync<DataSecurityException>(() => BoundedArchive.ReadAsync(corrupt, new()));
        Assert.Equal("archive.invalid", ex.Code);
    }

    [Fact]
    public async Task ArchiveCancellationNeverReturnsPartialResult()
    {
        using var zip = Zip(File("a.txt", 100));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BoundedArchive.ReadAsync(zip, new(), cts.Token));
    }

    [Fact]
    public async Task DestinationIsExactAndTenantScoped()
    {
        using var a = new AccessFixture("A");
        using var b = new AccessFixture("B");
        var actorA = await a.Actor();
        var actorB = await b.Actor();
        var policy = new DestinationPolicy([new("A", "https://approved.example.test/export")]);
        Assert.True(policy.Allows(actorA, "https://approved.example.test/export"));
        Assert.False(policy.Allows(actorB, "https://approved.example.test/export"));
        foreach (var endpoint in new[] { "https://approved.example.test/other",
            "https://approved.example.test/export?secret=x", "http://approved.example.test/export",
            "https://approved.example.test.evil.test/export", "https://approved.example.test@evil.test/export",
            "https://approved.example.test:444/export", "https://approved.example.test/../export",
            "https://127.0.0.1/export", "https://approved.example.test/%65xport" })
            Assert.False(policy.Allows(actorA, endpoint));
        Assert.Throws<ArgumentException>(() => new DestinationPolicy([new("A","https://127.0.0.1/export")]));
    }

    [Fact]
    public async Task TenantRateLimitIsAtomicAndBounded()
    {
        using var a = new AccessFixture("A");
        using var b = new AccessFixture("B");
        using var c = new AccessFixture("C");
        var actorA = await a.Actor();
        var actorB = await b.Actor();
        var actorC = await c.Actor();
        var clock = new FakeClock();
        var limiter = new TenantAdmissionLimiter(10, TimeSpan.FromSeconds(5), 2, clock);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => limiter.TryAcquire(actorA))));
        Assert.Equal(10, outcomes.Count(x => x));
        Assert.True(limiter.TryAcquire(actorB));
        Assert.False(limiter.TryAcquire(actorC)); // capacity cannot evict a live tenant
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(limiter.TryAcquire(actorC));
        Assert.True(limiter.TryAcquire(actorA));
    }

    [Fact]
    public async Task AdmissionGateDeniesBeforeReadingAndUsesIndependentTenantBudgets()
    {
        using var a = new AccessFixture("A");
        var clock = new FakeClock();
        var gate = new SecureTransferGate(a.Gate(clock),
            new(1, TimeSpan.FromMinutes(1), 4, clock),
            new(1, TimeSpan.FromMinutes(1), 4, clock),
            new DestinationPolicy([new("A", "https://approved.example.test/export")]));
        using var archive = Zip(File("input.json"));
        var resource = new TenantResource("A", "model");
        Assert.Null(await gate.StageArchiveAsync(new("B", "model"), archive, new()));
        Assert.Equal(0, archive.Position);
        a.Policy.Allowed = false;
        Assert.Null(await gate.StageArchiveAsync(resource, archive, new()));
        Assert.Equal(0, archive.Position);
        Assert.False(await gate.AdmitJobAsync(resource));
        Assert.False(await gate.AuthorizeDestinationAsync(resource,"https://approved.example.test/export"));
        a.Policy.Allowed = true;
        Assert.NotNull(await gate.StageArchiveAsync(resource, archive, new()));
        using var second = Zip(File("input.json"));
        Assert.Equal("admission.limit", (await Assert.ThrowsAsync<DataSecurityException>(async () =>
            await gate.StageArchiveAsync(resource, second, new()))).Code);
        Assert.Equal(0, second.Position);
        Assert.True(await gate.AdmitJobAsync(resource));
        Assert.False(await gate.AdmitJobAsync(resource));
        Assert.True(await gate.AuthorizeDestinationAsync(resource,"https://approved.example.test/export"));
        Assert.False(await gate.AuthorizeDestinationAsync(resource,"https://unapproved.example.test/export"));
    }

    private static EvidenceKey Key(string tenant, string evidence = "e1") => new(tenant, "model", "r1", evidence);
    private static StoredEvidence Evidence(EvidenceKey key, DateTimeOffset expires, bool hold = false, int refs = 0)
    {
        byte[] raw = Encoding.UTF8.GetBytes("CUSTOMER-PAYLOAD; password=DO-NOT-LEAK; credential=cred:private");
        return new(key, raw, Convert.ToHexStringLower(SHA256.HashData(raw)), EvidenceMathStatus.Feasible, expires, hold, refs);
    }

    [Fact]
    public async Task ExportChecksScopeDigestExpiryAndAuditBeforeReturningRedactedBytes()
    {
        using var auth = new AccessFixture("A");
        var clock = new FakeClock();
        var store = new Store();
        var a = Key("A"); var b = Key("B");
        store.Add(Evidence(a, clock.GetUtcNow().AddHours(1)));
        store.Add(Evidence(b, clock.GetUtcNow().AddHours(1)));
        var service = new EvidenceMovement(auth.Gate(clock), store, clock);
        Assert.Null(await service.ExportAsync(b));
        Assert.Equal(0, store.Reads);
        Assert.Null(await service.ExportAsync(Key("A", "missing")));
        var result = Assert.IsType<RedactedEvidence>(await service.ExportAsync(a));
        var json = Encoding.UTF8.GetString(result.Bytes.Span);
        Assert.Contains("Feasible", json);
        Assert.Contains("originalSha256", json);
        Assert.DoesNotContain("CUSTOMER-PAYLOAD", json);
        Assert.DoesNotContain("DO-NOT-LEAK", json);
        Assert.DoesNotContain("cred:private", json);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(result.Bytes.Span)), result.Sha256);
        Assert.Single(store.Audits);
        store.FailAudit = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.ExportAsync(a));
        store.FailAudit = false;
        store.Add(Evidence(a, clock.GetUtcNow().AddHours(1)) with { OriginalSha256 = new string('0',64) });
        Assert.Equal("evidence.digest", (await Assert.ThrowsAsync<DataSecurityException>(async () => await service.ExportAsync(a))).Code);
        store.Add(Evidence(a, clock.GetUtcNow().AddHours(1)));
        store.RemoveBeforeExportCommit = true;
        Assert.Null(await service.ExportAsync(a));
        store.RemoveBeforeExportCommit = false;
        store.Add(Evidence(a, clock.GetUtcNow()));
        Assert.Null(await service.ExportAsync(a));
        auth.Directory.Allowed = false;
        Assert.Null(await service.ExportAsync(a));
        Assert.Single(store.Audits);
    }

    [Fact]
    public async Task ExportRechecksRevocationAfterReadAndPreservesRegistryUnicode()
    {
        using var auth = new AccessFixture("مؤسسة");
        var clock = new FakeClock();
        var store = new Store();
        var key = new EvidenceKey("مؤسسة","model","نسخة-١","e1");
        store.Add(Evidence(key,clock.GetUtcNow().AddHours(1)));
        var service = new EvidenceMovement(auth.Gate(clock),store,clock);
        Assert.NotNull(await service.ExportAsync(key));
        Assert.Single(store.Audits);
        store.AfterRead = () => auth.Policy.Allowed = false;
        Assert.Null(await service.ExportAsync(key));
        Assert.Single(store.Audits);
        auth.Policy.Allowed = true;
        Assert.Throws<DataSecurityException>(() => (key with {EvidenceId="bad id"}).Validate());
        Assert.Throws<DataSecurityException>(() => (key with {RevisionId="bad\nrevision"}).Validate());
    }

    [Fact]
    public async Task ExpiredRetentionPreservesAuditReferencesAndOtherTenants()
    {
        using var auth = new AccessFixture("A");
        var clock = new FakeClock();
        var store = new Store();
        var now = clock.GetUtcNow();
        var a = Key("A"); var b = Key("B");
        var referenced = Key("A", "referenced"); var held = Key("A", "held"); var current = Key("A", "current");
        store.Add(Evidence(a, now)); store.Add(Evidence(b, now));
        store.Add(Evidence(referenced, now, refs:1)); store.Add(Evidence(held, now, hold:true)); store.Add(Evidence(current,now.AddDays(1)));
        var service = new EvidenceMovement(auth.Gate(clock), store, clock);
        Assert.Equal(RetentionResult.Missing, await service.ExpireAsync(b));
        Assert.Equal(RetentionResult.Protected, await service.ExpireAsync(referenced));
        Assert.Equal(RetentionResult.Protected, await service.ExpireAsync(held));
        Assert.Equal(RetentionResult.NotExpired, await service.ExpireAsync(current));
        store.FailAudit = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.ExpireAsync(a));
        Assert.True(store.Exists(a));
        Assert.Empty(store.Tombstones);
        store.FailAudit = false;
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () => await service.ExpireAsync(a))));
        Assert.Single(outcomes, x => x == RetentionResult.Removed);
        Assert.Contains(a, store.Tombstones);
        Assert.True(store.Exists(b)); Assert.True(store.Exists(referenced)); Assert.True(store.Exists(held)); Assert.True(store.Exists(current));
        Assert.False(store.Exists(a));
        Assert.Single(store.Audits);
        Assert.Equal("RetentionExpired", store.Audits.Single().Action);
        Assert.Equal(a, store.Audits.Single().Key);
        Assert.Equal("tester", store.Audits.Single().Subject);
    }

    // Synthetic transactional reference store: no claim of database durability.
    [Fact]
    public async Task SnapshotAndZipShareAdmissionBudgetAndStageOnlyAuthorizedBytes()
    {
        using var auth = new AccessFixture("A");
        var clock = new FakeClock();
        var budget = new TenantAdmissionLimiter(1, TimeSpan.FromMinutes(1), 4, clock);
        var target = new TargetSchema("fixture-target","1",[new("id","text",null,true,false)]);
        var manifest = new ConnectorManifest
        {
            ConnectorKind="file", SourceSchema="fixture", SourceSchemaVersion="1", TargetSchema=target.Id,
            TargetSchemaVersion=target.Version, MappingVersion="1.0",
            Fields=[new(){SourceField="id",TargetField="id",SourceType="text",TargetType="text",
                SourceUnit=null,TargetUnit=null,Required=true,AllowNull=false,TransformId="identity",TransformVersion="1.0"}],
            Provenance=new(){SourceKeyFields=["id"],SourceTimestampField="updated"},
            Cursor=new(){Mode="snapshot",MaxBatchRows=10},Retry=new(){MaxAttempts=1,InitialDelayMs=1,MaxDelayMs=1},
            Reconciliation="none",TimePolicy="utc-only",TimeZoneId=null,CurrencyPolicy="none",CredentialReference=null
        };
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{contract=ConnectorManifestReader.ContractId,version="1.0",payload=manifest},
            new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        var registry=new MappingRegistry(new(new("A","connector","1.0","stream"),bytes,target));
        var preview=new SecureSnapshotPreview(auth.Gate(clock),budget,new MappingPreview(auth.Access,registry));
        var zipGate=new SecureTransferGate(auth.Gate(clock),budget,new(1,TimeSpan.FromMinutes(1),4,clock),new DestinationPolicy([]));
        using var source=new MemoryStream(Encoding.UTF8.GetBytes("id,updated\nA,2026-01-01T00:00:00Z\n"));
        var options=new SnapshotOptions(SnapshotFormat.Csv);
        Assert.Null(await preview.PreviewAsync(new("B","model"),"connector","1.0","stream",source,options));
        Assert.Equal(0,source.Position);
        auth.Policy.Allowed=false;
        Assert.Null(await preview.PreviewAsync(new("A","model"),"connector","1.0","stream",source,options));
        Assert.Equal(0,source.Position);
        auth.Policy.Allowed=true;
        var result=await preview.PreviewAsync(new("A","model"),"connector","1.0","stream",source,options);
        Assert.True(result!.Accepted);
        using var zip=Zip(File("input.json"));
        Assert.Equal("admission.limit",(await Assert.ThrowsAsync<DataSecurityException>(async()=>
            await zipGate.StageArchiveAsync(new("A","model"),zip,new()))).Code);
        Assert.Equal(0,zip.Position);
        clock.Advance(TimeSpan.FromMinutes(1));
        using var oversized=new MemoryStream(new byte[SecureSnapshotPreview.MaximumSourceBytes+1]);
        Assert.Equal("snapshot.size",(await Assert.ThrowsAsync<DataSecurityException>(async()=>
            await preview.PreviewAsync(new("A","model"),"connector","1.0","stream",oversized,options))).Code);
        clock.Advance(TimeSpan.FromMinutes(1));
        using var revoked=new RevokingStream(source.ToArray(),()=>auth.Policy.Allowed=false);
        Assert.Null(await preview.PreviewAsync(new("A","model"),"connector","1.0","stream",revoked,options));
    }

    private sealed class RevokingStream(byte[] bytes,Action revoke):MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)
        { revoke(); return base.ReadAsync(buffer,ct); }
    }

    private sealed class MappingRegistry(RegisteredMapping mapping):IAuthorizedMappingRegistry
    {
        public ValueTask<RegisteredMapping?> FindAsync(TenantActor actor,string modelId,string connectorId,string mappingVersion,string streamId,CancellationToken ct)
            =>ValueTask.FromResult<RegisteredMapping?>(mapping);
    }

    private sealed class Store : IEvidenceMovementStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<EvidenceKey, StoredEvidence> _data = new();
        public List<MovementAudit> Audits { get; } = [];
        public HashSet<EvidenceKey> Tombstones { get; } = [];
        public int Reads;
        public bool FailAudit;
        public bool RemoveBeforeExportCommit;
        public Action? AfterRead;
        public void Add(StoredEvidence e) { lock(_gate) _data[e.Key] = e; }
        public bool Exists(EvidenceKey key) { lock(_gate) return _data.ContainsKey(key); }
        public ValueTask<StoredEvidence?> ReadAsync(EvidenceKey key, CancellationToken ct)
        {
            lock(_gate) { Reads++; var result = _data.GetValueOrDefault(key); AfterRead?.Invoke(); return ValueTask.FromResult(result); }
        }
        public ValueTask<bool> CommitExportAuditAsync(EvidenceKey key, string digest, MovementAudit audit, CancellationToken ct)
        {
            lock(_gate) {
                if (FailAudit) throw new InvalidOperationException("Audit unavailable.");
                if (RemoveBeforeExportCommit) _data.Remove(key);
                if (!_data.TryGetValue(key, out var e) || e.OriginalSha256 != digest || e.ExpiresAt <= audit.Timestamp) return ValueTask.FromResult(false);
                Audits.Add(audit); return ValueTask.FromResult(true);
            }
        }
        public ValueTask<RetentionResult> ExpireAsync(EvidenceKey key, MovementAudit audit, CancellationToken ct)
        {
            lock(_gate)
            {
                if (!_data.TryGetValue(key, out var e)) return ValueTask.FromResult(RetentionResult.Missing);
                var decision = EvidenceRetentionPolicy.Evaluate(e, audit.Timestamp);
                if (decision != RetentionResult.Removed) return ValueTask.FromResult(decision);
                if (FailAudit) throw new InvalidOperationException("Audit unavailable.");
                Audits.Add(audit);
                Tombstones.Add(key); _data.Remove(key);
                return ValueTask.FromResult(RetentionResult.Removed);
            }
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero).AddTicks(GetTimestamp());
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks,duration.Ticks);
    }

    private sealed class AccessFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        public Directory Directory { get; } = new();
        public TenantAccess Access { get; }
        public Policy Policy { get; } = new();
        public DataMovementGate Gate(TimeProvider clock) => new(Access, Policy, new Audit(), clock);
        private readonly string _tenant;
        public AccessFixture(string tenant)
        {
            _tenant = tenant;
            var services = new ServiceCollection();
            services.AddSingleton<IAuthenticationService>(new Authentication(tenant));
            _provider = services.BuildServiceProvider();
            var context = new DefaultHttpContext { RequestServices = _provider };
            Access = new TenantAccess(new Accessor { HttpContext = context }, Directory, new Audit(), new("https://issuer.example.test","api"));
        }
        public async Task<TenantActor> Actor() => Assert.IsType<TenantActor>(await Access.AuthorizeAsync(TenantPermission.View,new(_tenant,"model")));
        public void Dispose() => _provider.Dispose();
    }
    private sealed class Accessor : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }
    private sealed class Directory : ITenantMembershipDirectory
    {
        public bool Allowed = true;
        public ValueTask<bool> IsActiveAsync(TenantActor actor,CancellationToken ct) => ValueTask.FromResult(true);
        public ValueTask<bool> AllowsAsync(TenantActor actor,TenantPermission permission,string model,CancellationToken ct) =>
            ValueTask.FromResult(Allowed && model == "model" && permission is TenantPermission.View or TenantPermission.Write or TenantPermission.Execute or TenantPermission.Propose);
    }
    private sealed class Policy : IDataMovementPolicy
    {
        public bool Allowed = true;
        public ValueTask<bool> AllowsAsync(TenantActor actor, string modelId, DataMovementAction action, CancellationToken ct) => ValueTask.FromResult(Allowed);
    }
    private sealed class Audit : ITenantDenialAudit
    {
        public ValueTask RecordAsync(TenantDenial denial,CancellationToken ct) => ValueTask.CompletedTask;
    }
    // Auth seam fixture only: actual JWT/OIDC pipeline qualified in E01.
    private sealed class Authentication(string tenant) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context,string? scheme)
        {
            Assert.Equal("Bearer",scheme);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new("iss","https://issuer.example.test"),new("sub","tester"),new("tenant_id",tenant)],"Bearer"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal,"Bearer")));
        }
        public Task ChallengeAsync(HttpContext c,string? s,AuthenticationProperties? p) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext c,string? s,AuthenticationProperties? p) => throw new NotSupportedException();
        public Task SignInAsync(HttpContext c,string? s,ClaimsPrincipal p,AuthenticationProperties? a) => throw new NotSupportedException();
        public Task SignOutAsync(HttpContext c,string? s,AuthenticationProperties? p) => throw new NotSupportedException();
    }
}
