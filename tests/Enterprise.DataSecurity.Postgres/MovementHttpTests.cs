using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using WowDi.DecisionGraph;
using WowDi.Enterprise.Identity;
using WowDi.Enterprise.Persistence;
using WowDi.Udir;
using Xunit;

namespace WowDi.Enterprise.DataSecurity.Postgres.Tests;

public sealed partial class MovementPostgresTests
{
    private const string Issuer="https://issuer.example.test", Audience="wowdi-server";
    [Fact]
    public async Task KestrelJwtAndActualRegistryAuthorizeRedactedExport()
    {
        using var rsa=RSA.Create(2048);
        var builder=WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<ITenantMembershipDirectory,MovementDirectory>();
        builder.Services.AddSingleton<ITenantDenialAudit,MovementDenialAudit>();
        builder.Services.AddSingleton<IDataMovementPolicy,MovementPolicy>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(EnterpriseDb.Configure(_connection));
        builder.Services.AddSingleton<IEvidenceMovementStore>(_store);
        builder.Services.AddScoped<EnterpriseRepository>();
        builder.Services.AddScoped<DataMovementGate>();
        builder.Services.AddScoped<EvidenceMovement>();
        builder.Services.AddWowDiTenantIdentity(new(Issuer,Audience));
        builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
            o=>o.BackchannelHttpHandler=new MovementDiscovery(rsa.ExportParameters(false)));
        await using var app=builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        // Test host only. Final REST routes and public contracts remain owned by S12.
        app.MapPost("/fixture/seed/{tenant}",async (string tenant,EnterpriseRepository repository,HttpContext ctx)=>
        {
            var doc=UdirDocument.Create(UdirDocument.Read(System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","factory.json"))).Model with{TenantId=tenant});
            var graph=DecisionGraphSnapshot.Create(new(tenant,"factory","r17"),[],[]);
            var source=System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","factory.txt"));
            var inputs=new Dictionary<string,ReadOnlyMemory<byte>>{{Convert.ToHexStringLower(SHA256.HashData(source)),source}};
            return await repository.SaveRevisionAsync(doc,graph,inputs,ctx.RequestAborted) is null?Results.NotFound():Results.Ok();
        });
        app.MapGet("/fixture/export/{tenant}/{evidence}",async(string tenant,string evidence,EvidenceMovement movement,HttpContext ctx)=>
        {
            var result=await movement.ExportAsync(Key(tenant,evidence),ctx.RequestAborted);
            if(result is null)return Results.NotFound();
            ctx.Response.Headers.CacheControl="no-store";
            return Results.Bytes(result.Bytes.ToArray(),"application/json");
        });
        app.MapPost("/fixture/expire/{tenant}/{evidence}",async(string tenant,string evidence,EvidenceMovement movement,HttpContext ctx)=>
        {
            var result=await movement.ExpireAsync(Key(tenant,evidence),ctx.RequestAborted);
            return result==RetentionResult.Missing?Results.NotFound():Results.Ok(result.ToString());
        });
        await app.StartAsync();
        using var client=new HttpClient{BaseAddress=new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
        async Task<HttpResponseMessage> Send(HttpMethod method,string path,string? token)
        {
            using var request=new HttpRequestMessage(method,path);
            if(token is not null) request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
            return await client.SendAsync(request);
        }
        string Token(string tenant,string subject="fixture-actor",bool expired=false)
        {
            var now=DateTime.UtcNow;
            return new JwtSecurityTokenHandler().CreateEncodedJwt(new SecurityTokenDescriptor
            {
                Issuer=Issuer,Audience=Audience,Subject=new ClaimsIdentity([new("sub",subject),new("tenant_id",tenant)]),
                IssuedAt=now.AddMinutes(-10),NotBefore=now.AddMinutes(-10),Expires=expired?now.AddMinutes(-1):now.AddMinutes(5),
                TokenType="at+jwt",SigningCredentials=new SigningCredentials(new RsaSecurityKey(rsa){KeyId="test-key"},SecurityAlgorithms.RsaSha256)
            });
        }
        using var seed=await Send(HttpMethod.Post,"/fixture/seed/http-fixture",Token("http-fixture"));
        Assert.Equal(HttpStatusCode.OK,seed.StatusCode);
        var key=Key("http-fixture");
        var data=Evidence(key,DateTimeOffset.UtcNow.AddHours(1));
        await _store.RegisterPublishedAsync(data);
        await _store.RegisterPublishedAsync(Evidence(Key("other"),DateTimeOffset.UtcNow.AddHours(1)));
        using var missing=await Send(HttpMethod.Get,"/fixture/export/http-fixture/e1",null);
        using var expired=await Send(HttpMethod.Get,"/fixture/export/http-fixture/e1",Token("http-fixture",expired:true));
        using var foreign=await Send(HttpMethod.Get,"/fixture/export/other/e1",Token("http-fixture"));
        using var forbidden=await Send(HttpMethod.Get,"/fixture/export/http-fixture/e1",Token("http-fixture","no-movement"));
        Assert.Equal(HttpStatusCode.Unauthorized,missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,expired.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,forbidden.StatusCode);
        Assert.Equal(0L,await Count("SELECT count(*) FROM wowdi_security.movement_audit"));
        using var allowed=await Send(HttpMethod.Get,"/fixture/export/http-fixture/e1",Token("http-fixture"));
        Assert.Equal(HttpStatusCode.OK,allowed.StatusCode);
        Assert.True(allowed.Headers.CacheControl!.NoStore);
        var output=await allowed.Content.ReadAsStringAsync();
        Assert.Contains("Feasible",output);
        Assert.Contains(data.OriginalSha256,output);
        Assert.DoesNotContain(Encoding.UTF8.GetString(data.OriginalBytes.Span),output);
        Assert.Equal(1L,await Count("SELECT count(*) FROM wowdi_security.movement_audit WHERE tenant='http-fixture' AND subject='fixture-actor' AND action='RedactedExport'"));
        Assert.Equal(0L,await Count("SELECT count(*) FROM wowdi_security.movement_audit WHERE tenant='other'"));
        using var denied=await Send(HttpMethod.Post,"/fixture/expire/other/e1",Token("http-fixture"));
        Assert.Equal(HttpStatusCode.NotFound,denied.StatusCode);
        Assert.NotNull(await _store.ReadAsync(Key("other"),default));
    }
    private sealed class MovementDirectory:ITenantMembershipDirectory
    {
        public ValueTask<bool> IsActiveAsync(TenantActor actor,CancellationToken ct)=>ValueTask.FromResult(actor.TenantId is "http-fixture" or "other");
        public ValueTask<bool> AllowsAsync(TenantActor actor,TenantPermission permission,string model,CancellationToken ct)=>
            ValueTask.FromResult(model=="factory"&&permission is TenantPermission.View or TenantPermission.Write);
    }
    private sealed class MovementDenialAudit:ITenantDenialAudit
    { public ValueTask RecordAsync(TenantDenial denial,CancellationToken ct)=>ValueTask.CompletedTask; }
    private sealed class MovementPolicy:IDataMovementPolicy
    { public ValueTask<bool> AllowsAsync(TenantActor actor,string modelId,DataMovementAction action,CancellationToken ct)=>ValueTask.FromResult(actor.Subject=="fixture-actor"); }
    private sealed class MovementDiscovery(RSAParameters key):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var json=request.RequestUri!.AbsoluteUri switch
            {
                Issuer+"/.well-known/openid-configuration"=>JsonSerializer.Serialize(new{issuer=Issuer,jwks_uri=Issuer+"/keys"}),
                Issuer+"/keys"=>JsonSerializer.Serialize(new{keys=new[]{new{kty="RSA",kid="test-key",use="sig",alg="RS256",n=Base64UrlEncoder.Encode(key.Modulus!),e=Base64UrlEncoder.Encode(key.Exponent!)}}}),
                _=>throw new InvalidOperationException("Unexpected discovery request.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")});
        }
    }
}
