using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WowDi.Enterprise.Identity;

namespace WowDi.Enterprise.Operations;

/// <summary>Host-owned sink. Do not expose the store to untrusted clients.</summary>
public sealed class DurableDenialAudit : ITenantDenialAudit
{
    private readonly DbContextOptions<OperationsDb> _options;
    private readonly byte[] _key;
    private readonly TimeProvider _clock;
    public DurableDenialAudit(DbContextOptions<OperationsDb> options,ReadOnlySpan<byte> pseudonymKey,TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        if(pseudonymKey.Length!=32)throw new ArgumentException("A dedicated 256-bit secret is required.");
        _options=options;_key=pseudonymKey.ToArray();_clock=clock;
    }
    public async ValueTask RecordAsync(TenantDenial denial,CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(denial);
        var tenant=Valid(denial.TenantId)?denial.TenantId:null;
        var subject=tenant is not null&&Valid(denial.Subject)?Digest("subject",tenant,denial.Subject!):null;
        var correlation=Valid(denial.CorrelationId)?Digest("correlation",tenant??"",denial.CorrelationId):Digest("correlation",tenant??"","invalid");
        var action=denial.Action switch
        {
            "View" or "Write" or "Propose" or "Approve" or "Execute" or "Authenticate" or "UnknownPermission"=>denial.Action,
            _=>"Unknown"
        };
        await using var db=new OperationsDb(_options);
        db.Denials.Add(new(){Id=Guid.NewGuid(),Tenant=tenant,SubjectDigest=subject,CorrelationDigest=correlation,Action=action,RecordedAt=_clock.GetUtcNow()});
        await db.SaveChangesAsync(cancellationToken);
    }
    private string Digest(string domain,string tenant,string value)=>Convert.ToHexStringLower(HMACSHA256.HashData(_key,JsonSerializer.SerializeToUtf8Bytes(new[]{domain,tenant,value})));
    private static bool Valid(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=512&&value==value.Trim()&&!value.Any(char.IsControl);
}
