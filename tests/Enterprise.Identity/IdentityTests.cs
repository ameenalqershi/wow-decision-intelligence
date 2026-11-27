using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Xunit;

namespace WowDi.Enterprise.Identity.Tests;

public sealed class IdentityTests : IAsyncLifetime
{
    const string Issuer = "https://issuer.example.test";
    const string Audience = "wowdi-server";
    const string Model = "same-model";
    readonly RSA _rsa = RSA.Create(2048);
    readonly DirectoryMonitor _directory = new();
    readonly Audit _audit = new();
    readonly ConcurrentDictionary<(string Tenant, string Model), int> _jobs = new();
    WebApplication _app = null!;
    HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IOptionsMonitor<TenantDirectoryOptions>>(_directory);
        builder.Services.AddSingleton<ITenantMembershipDirectory, ConfiguredTenantMembershipDirectory>();
        builder.Services.AddSingleton<ITenantDenialAudit>(_audit);
        builder.Services.AddWowDiTenantIdentity(new(Issuer, Audience));
        builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            // Use actual OIDC discovery parsing and signature verification with a deterministic synthetic HTTPS peer.
            o.BackchannelHttpHandler = new Discovery(_rsa.ExportParameters(false));
        });
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapGet("/models/{tenant}/{model}", async (string tenant, string model, TenantAccess access, HttpContext ctx) =>
        {
            var actor = await access.AuthorizeAsync(TenantPermission.View, new(tenant, model), ctx.RequestAborted);
            return actor is null ? Results.NotFound() : Results.Ok(actor.TenantId == "A" ? "secret-A" : "secret-B");
        });
        _app.MapPost("/jobs/{tenant}/{model}", async (string tenant, string model, TenantAccess access, HttpContext ctx) =>
        {
            var binding = await access.BindJobAsync(new(tenant, model), ctx.RequestAborted);
            if (binding is null) return Results.NotFound();
            _jobs.AddOrUpdate((binding.Actor.TenantId, binding.ModelId), 1, (_, count) => count + 1);
            return Results.Ok(binding.Actor.TenantId);
        });
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _rsa.Dispose();
    }

    string Token(string tenant = "A", string subject = "alice", string issuer = Issuer, string audience = Audience,
        string type = "at+jwt", bool expired = false, bool duplicate = false, bool absent = false, RSA? signingKey = null)
    {
        List<Claim> claims = [new("sub", subject)];
        if (!absent) claims.Add(new("tenant_id", tenant));
        if (duplicate) claims.Add(new("tenant_id", "B"));
        // Signed token role is intentionally ignored: the directory owns live permissions.
        claims.Add(new("role", "Execute"));
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = audience, Subject = new ClaimsIdentity(claims),
            IssuedAt = now.AddMinutes(-10), NotBefore = now.AddMinutes(-10),
            Expires = expired ? now.AddMinutes(-1) : now.AddMinutes(5), TokenType = type,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(signingKey ?? _rsa) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256)
        };
        return new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
    }

    async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? token, string? spoof = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (spoof is not null) request.Headers.Add("X-Tenant-Id", spoof);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task ValidDiscoveryAndTenantScopedLookup()
    {
        using var a = await Send(HttpMethod.Get, $"/models/A/{Model}", Token());
        using var b = await Send(HttpMethod.Get, $"/models/B/{Model}", Token("B", "bob"));
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        Assert.Contains("secret-A", await a.Content.ReadAsStringAsync());
        Assert.Contains("secret-B", await b.Content.ReadAsStringAsync());
        using var denied = await Send(HttpMethod.Get, $"/models/B/{Model}", Token(), "B");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.DoesNotContain("secret-B", await denied.Content.ReadAsStringAsync());
        Assert.Contains(_audit.Events, d => d.TenantId == "A" && d.Subject == "alice" && d.Action == "View");
    }

    [Fact]
    public async Task RejectsForgedExpiredWrongAudienceIssuerIdTokenAndAmbiguousMembership()
    {
        using var foreign = RSA.Create(2048);
        string?[] tokens = [null, Token(issuer:"https://evil.example.test"), Token(audience:"different"),
            Token(expired:true), Token(type:"JWT"), Token(signingKey:foreign), Token(duplicate:true),
            Token(absent:true), Token(subject:"unknown"), Token(tenant:"B")];
        foreach (var token in tokens)
        {
            using var response = await Send(HttpMethod.Get, $"/models/A/{Model}", token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.DoesNotContain("secret-A", await response.Content.ReadAsStringAsync());
            if (token is not null) Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(tokens.Length, _audit.Events.Count);
        Assert.All(_audit.Events, d => { Assert.Null(d.TenantId); Assert.Null(d.Subject); Assert.Equal("Authenticate", d.Action); });
    }

    [Fact]
    public async Task JobBindingNeverUsesUntrustedTenantAndChecksLivePermissions()
    {
        using var denied = await Send(HttpMethod.Post, $"/jobs/B/{Model}", Token(), "B");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Empty(_jobs);
        using var allowed = await Send(HttpMethod.Post, $"/jobs/A/{Model}", Token(), "B");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(1, _jobs[("A", Model)]);
        _directory.Set(allowPropose: false);
        using var noRole = await Send(HttpMethod.Post, $"/jobs/A/{Model}", Token());
        Assert.Equal(HttpStatusCode.NotFound, noRole.StatusCode);
        _directory.Set(revokeAlice: true);
        using var revoked = await Send(HttpMethod.Get, $"/models/A/{Model}", Token());
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal(1, _jobs[("A", Model)]);
        Assert.False(_jobs.ContainsKey(("B", Model)));
    }

    [Fact]
    public async Task ParallelTenantsDoNotBleedContextOrMutateForeignJobs()
    {
        var a = Token(); var b = Token("B", "bob");
        await Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
        {
            var own = i % 2 == 0 ? "A" : "B";
            var other = own == "A" ? "B" : "A";
            var token = own == "A" ? a : b;
            using var allowed = await Send(HttpMethod.Post, $"/jobs/{own}/{Model}", token, other);
            using var denied = await Send(HttpMethod.Post, $"/jobs/{other}/{Model}", token, other);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }));
        Assert.Equal(20, _jobs[("A", Model)]);
        Assert.Equal(20, _jobs[("B", Model)]);
        Assert.Equal(40, _audit.Events.Count);
    }

    [Fact]
    public async Task NoHttpIdentityFailsClosed()
    {
        using var scope = _app.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<TenantAccess>();
        Assert.Null(await access.BindJobAsync(new("A", Model)));
        Assert.Single(_audit.Events);
        Assert.Null(_audit.Events.Single().TenantId);
    }

    [Fact]
    public void InvalidAuthorityRejected()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddWowDiTenantIdentity(new("http://issuer.test", Audience)));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddWowDiTenantIdentity(new(Issuer, Audience, "sub")));
    }

    [Fact]
    public async Task DuplicateGrantsAndUnlistedModelsFailClosed()
    {
        using var unknown = await Send(HttpMethod.Get, "/models/A/unlisted", Token());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        _directory.Set(duplicateModel: true);
        using var duplicateModel = await Send(HttpMethod.Get, $"/models/A/{Model}", Token());
        Assert.Equal(HttpStatusCode.NotFound, duplicateModel.StatusCode);
        _directory.Set(duplicateMember: true);
        using var duplicateMember = await Send(HttpMethod.Get, $"/models/A/{Model}", Token());
        Assert.Equal(HttpStatusCode.Unauthorized, duplicateMember.StatusCode);
        Assert.Empty(_jobs);
        var auditJson = JsonSerializer.Serialize(_audit.Events);
        Assert.DoesNotContain("unlisted", auditJson);
        Assert.DoesNotContain("secret-A", auditJson);
        Assert.DoesNotContain("secret-B", auditJson);
        Assert.DoesNotContain("Bearer", auditJson);
    }

    sealed class DirectoryMonitor : IOptionsMonitor<TenantDirectoryOptions>
    {
        private TenantDirectoryOptions _current = Make();
        public TenantDirectoryOptions CurrentValue => Volatile.Read(ref _current);
        public TenantDirectoryOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<TenantDirectoryOptions, string?> listener) => null;
        public void Set(bool allowPropose = true, bool revokeAlice = false, bool duplicateMember = false, bool duplicateModel = false)
            => Volatile.Write(ref _current, Make(allowPropose, revokeAlice, duplicateMember, duplicateModel));

        private static TenantDirectoryOptions Make(bool allowPropose = true, bool revokeAlice = false,
            bool duplicateMember = false, bool duplicateModel = false)
        {
            TenantModelGrant Grant() => new() { ModelId = Model, Permissions = allowPropose
                ? [TenantPermission.View, TenantPermission.Propose] : [TenantPermission.View] };
            TenantMembershipGrant Alice() => new() { Issuer = Issuer, TenantId = "A", Subject = "alice", Active = !revokeAlice,
                Models = duplicateModel ? [Grant(), Grant()] : [Grant()] };
            var bob = new TenantMembershipGrant { Issuer = Issuer, TenantId = "B", Subject = "bob", Active = true, Models = [Grant()] };
            return new() { Memberships = duplicateMember ? [Alice(), Alice(), bob] : [Alice(), bob] };
        }
    }

    sealed class Audit : ITenantDenialAudit
    {
        public ConcurrentQueue<TenantDenial> Events { get; } = new();
        public ValueTask RecordAsync(TenantDenial denial, CancellationToken ct)
        { Events.Enqueue(denial); return ValueTask.CompletedTask; }
    }

    sealed class Discovery(RSAParameters key) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsoluteUri switch
            {
                Issuer + "/.well-known/openid-configuration" => JsonSerializer.Serialize(new { issuer = Issuer, jwks_uri = Issuer + "/keys" }),
                Issuer + "/keys" => JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = "test-key",
                    use = "sig", alg = "RS256", n = Base64UrlEncoder.Encode(key.Modulus!), e = Base64UrlEncoder.Encode(key.Exponent!) } } }),
                _ => throw new InvalidOperationException("Unexpected discovery request.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
