using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
using WowDi.Runtime.Governance;
using WowDi.Solver.Contracts;
using WowDi.Udir;
using Xunit;

namespace WowDi.Enterprise.Operations.Tests;

public sealed partial class DenialTests
{
    private const string Issuer="https://issuer.example.test", Audience="wowdi-operations";
    [Fact]
    public async Task RealHttpJwtAndRegistryAdmissionScopeUsageAndDenialAudit()
    {
        using var rsa=RSA.Create(2048);
        var builder=WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<ITenantMembershipDirectory,FixtureDirectory>();
        builder.Services.AddSingleton<ITenantDenialAudit>(new DurableDenialAudit(OperationsDb.Configure(_connection),Key,TimeProvider.System));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(OperationsDb.Configure(_connection));
        builder.Services.AddSingleton(EnterpriseDb.Configure(_connection));
        builder.Services.AddScoped<EnterpriseRepository>();
        builder.Services.AddScoped<UsageReader>();
        builder.Services.AddWowDiTenantIdentity(new(Issuer,Audience));
        builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
            o=>o.BackchannelHttpHandler=new Discovery(rsa.ExportParameters(false)));
        await using var app=builder.Build();
        app.UseAuthentication();app.UseAuthorization();
        // Test-only routes, not a published S12 wire contract.
        app.MapPost("/fixture/admit/{tenant}",async(string tenant,EnterpriseRepository repo,HttpContext ctx)=>
        {
            var doc=UdirDocument.Create(UdirDocument.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","factory.json"))).Model with{TenantId=tenant});
            var key=new GraphRevision(tenant,doc.Model.ModelId,doc.Model.RevisionId);
            var source=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","factory.txt"));
            var inputs=new Dictionary<string,ReadOnlyMemory<byte>>{{Convert.ToHexStringLower(SHA256.HashData(source)),source}};
            if(await repo.SaveRevisionAsync(doc,DecisionGraphSnapshot.Create(key,[],[]),inputs,ctx.RequestAborted) is null)return Results.NotFound();
            var job=await repo.AdmitJobAsync(key,"fixture-idempotency",Encoding.UTF8.GetBytes("fixture"),ctx.RequestAborted);
            return job is null?Results.NotFound():Results.Json(job.JobId);
        });
        app.MapGet("/fixture/usage/{tenant}/{model}",async(string tenant,string model,UsageReader reader,HttpContext ctx)=>
        {
            var now=DateTimeOffset.UtcNow;
            var totals=await reader.ReadAsync(new(tenant,model),now.AddDays(-1),now.AddDays(1),"cgroup-v2-charged-memory",ctx.RequestAborted);
            if(totals is null)return Results.NotFound();
            ctx.Response.Headers.CacheControl="no-store";
            return Results.Json(totals);
        });
        await app.StartAsync();
        using var client=new HttpClient{BaseAddress=new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
        string Token(string tenant,string subject="actor",bool expired=false)
        {
            var now=DateTime.UtcNow;
            return new JwtSecurityTokenHandler().CreateEncodedJwt(new SecurityTokenDescriptor
            {
                Issuer=Issuer,Audience=Audience,Subject=new ClaimsIdentity([new("sub",subject),new("tenant_id",tenant)]),
                IssuedAt=now.AddMinutes(-10),NotBefore=now.AddMinutes(-10),Expires=expired?now.AddMinutes(-1):now.AddMinutes(5),
                TokenType="at+jwt",SigningCredentials=new SigningCredentials(new RsaSecurityKey(rsa){KeyId="fixture"},SecurityAlgorithms.RsaSha256)
            });
        }
        async Task<HttpResponseMessage> Send(HttpMethod method,string path,string? token)
        {
            using var req=new HttpRequestMessage(method,path);
            if(token is not null)req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
            return await client.SendAsync(req);
        }
        var recorder=new RunUsageRecorder(OperationsDb.Configure(_connection));
        foreach(var tenant in new[]{"tenant-a","tenant-b"})
        {
            using var admitted=await Send(HttpMethod.Post,"/fixture/admit/"+tenant,Token(tenant));
            Assert.Equal(HttpStatusCode.OK,admitted.StatusCode);
            var job=(await admitted.Content.ReadFromJsonAsync<string>())!;
            var lease=new WorkerLease(new(new(tenant,"factory","r17",job),new(1,1048576,TimeSpan.FromSeconds(30)),new string('a',64)),Guid.NewGuid(),1,TimeSpan.FromSeconds(20));
            await recorder.RecordAsync(lease,new(Termination.Completed,0,[1],new(100,tenant=="tenant-a"?50:80,null),true,null,1024,"cgroup-v2-charged-memory",100));
        }
        using var allowed=await Send(HttpMethod.Get,"/fixture/usage/tenant-a/factory",Token("tenant-a"));
        Assert.Equal(HttpStatusCode.OK,allowed.StatusCode);
        Assert.True(allowed.Headers.CacheControl!.NoStore);
        var totals=(await allowed.Content.ReadFromJsonAsync<UsageTotals>())!;
        Assert.Equal(1,totals.Attempts);
        Assert.Equal(100m,totals.ReservedCoreMs);
        Assert.Equal(50m,totals.MeasuredCpuMs);
        Assert.Equal(1,totals.CpuMeasuredAttempts);
        Assert.Equal(1024,totals.PeakScopeBytes);
        using var foreign=await Send(HttpMethod.Get,"/fixture/usage/tenant-b/factory",Token("tenant-a"));
        using var wrongModel=await Send(HttpMethod.Get,"/fixture/usage/tenant-a/foreign-model",Token("tenant-a"));
        using var wrongRole=await Send(HttpMethod.Get,"/fixture/usage/tenant-a/factory",Token("tenant-a","denied"));
        using var missing=await Send(HttpMethod.Get,"/fixture/usage/tenant-a/factory",null);
        using var expired=await Send(HttpMethod.Get,"/fixture/usage/tenant-a/factory",Token("tenant-a",expired:true));
        Assert.Equal(HttpStatusCode.NotFound,foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,wrongModel.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,wrongRole.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,expired.StatusCode);
        Assert.Equal(3L,await Count("SELECT count(*) FROM wowdi_ops.denials WHERE tenant='tenant-a' AND action='View'"));
        Assert.Equal(2L,await Count("SELECT count(*) FROM wowdi_ops.denials WHERE tenant IS NULL AND action='Authenticate'"));
        Assert.Equal(0L,await Count("SELECT count(*) FROM wowdi_ops.denials WHERE tenant='tenant-b'"));
    }
    private sealed class FixtureDirectory:ITenantMembershipDirectory
    {
        public ValueTask<bool> IsActiveAsync(TenantActor a,CancellationToken ct)=>ValueTask.FromResult(a.TenantId is "tenant-a" or "tenant-b");
        public ValueTask<bool> AllowsAsync(TenantActor a,TenantPermission p,string model,CancellationToken ct)=>
            ValueTask.FromResult(a.Subject=="actor"&&model=="factory"&&p is TenantPermission.View or TenantPermission.Write or TenantPermission.Propose);
    }
    private sealed class Discovery(RSAParameters key):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var json=request.RequestUri!.AbsoluteUri switch
            {
                Issuer+"/.well-known/openid-configuration"=>JsonSerializer.Serialize(new{issuer=Issuer,jwks_uri=Issuer+"/keys"}),
                Issuer+"/keys"=>JsonSerializer.Serialize(new{keys=new[]{new{kty="RSA",kid="fixture",use="sig",alg="RS256",n=Base64UrlEncoder.Encode(key.Modulus!),e=Base64UrlEncoder.Encode(key.Exponent!)}}}),
                _=>throw new InvalidOperationException("Unexpected fixture discovery request.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")});
        }
    }
}
