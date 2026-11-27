using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace WowDi.Enterprise.Identity;

public enum TenantPermission { View, Write, Propose, Approve, Execute }

// A directory is authoritative for live membership, including revocation and model scopes.
// Implementations must query by the complete issuer/subject/tenant key, never by subject alone.
public interface ITenantMembershipDirectory
{
    ValueTask<bool> IsActiveAsync(TenantActor actor, CancellationToken cancellationToken);
    ValueTask<bool> AllowsAsync(TenantActor actor, TenantPermission permission, string modelId, CancellationToken cancellationToken);
}

public sealed record TenantActor
{
    internal TenantActor(string issuer, string subject, string tenantId)
        => (Issuer, Subject, TenantId) = (issuer, subject, tenantId);
    public string Issuer { get; }
    public string Subject { get; }
    public string TenantId { get; }
}

public sealed record TenantResource(string TenantId, string ModelId);
public sealed record TenantJobBinding
{
    internal TenantJobBinding(TenantActor actor, string modelId)
        => (Actor, ModelId) = (actor, modelId);
    public TenantActor Actor { get; }
    public string ModelId { get; }
}

public sealed record TenantDenial(string? TenantId, string? Subject, string Action, string CorrelationId, DateTimeOffset Timestamp);

// The audit sink receives no raw token, request, exception, target tenant or customer payload.
public interface ITenantDenialAudit
{
    ValueTask RecordAsync(TenantDenial denial, CancellationToken cancellationToken);
}

public sealed class LoggerTenantDenialAudit(ILogger<LoggerTenantDenialAudit> logger) : ITenantDenialAudit
{
    public ValueTask RecordAsync(TenantDenial denial, CancellationToken cancellationToken)
    {
        logger.LogWarning("Tenant access denied: tenant={TenantId} subject={Subject} action={Action} correlation={CorrelationId} at={Timestamp}",
            denial.TenantId, denial.Subject, denial.Action, denial.CorrelationId, denial.Timestamp);
        return ValueTask.CompletedTask;
    }
}

public sealed record TenantOidcOptions(string Authority, string Audience, string TenantClaim = "tenant_id");

public sealed class TenantAccess
{
    private readonly IHttpContextAccessor _http;
    private readonly ITenantMembershipDirectory _directory;
    private readonly ITenantDenialAudit _audit;
    private readonly TenantOidcOptions _options;
    public TenantAccess(IHttpContextAccessor http, ITenantMembershipDirectory directory, ITenantDenialAudit audit, TenantOidcOptions options)
        => (_http, _directory, _audit, _options) = (http, directory, audit, options);

    internal static string? Single(ClaimsPrincipal principal, string type)
    {
        var values = principal.FindAll(type).Select(c => c.Value).ToArray();
        return values.Length == 1 && ValidId(values[0]) ? values[0] : null;
    }
    private static bool ValidId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl) && value == value.Trim();

    private async ValueTask<TenantActor?> ReadActorAsync()
    {
        var http = _http.HttpContext;
        if (http is null) return null;
        var authentication = await http.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        var principal = authentication.Succeeded ? authentication.Principal : null;
        if (principal is null || principal.Identities.Count() != 1 || principal.Identity?.IsAuthenticated != true)
            return null;
        var issuer = Single(principal, "iss");
        var subject = Single(principal, "sub");
        var tenant = Single(principal, _options.TenantClaim);
        return issuer == _options.Authority && subject is not null && tenant is not null
            ? new TenantActor(issuer, subject, tenant) : null;
    }

    // Use the authenticated tenant before querying storage. Never fetch a foreign resource to check access.
    public async ValueTask<TenantActor?> AuthorizeAsync(TenantPermission permission, TenantResource resource, CancellationToken cancellationToken = default)
    {
        var actor = await ReadActorAsync();
        if (actor is not null && Enum.IsDefined(permission) && ValidId(resource.ModelId) &&
            string.Equals(actor.TenantId, resource.TenantId, StringComparison.Ordinal) &&
            await _directory.IsActiveAsync(actor, cancellationToken) &&
            await _directory.AllowsAsync(actor, permission, resource.ModelId, cancellationToken))
            return actor;
        await DenyAsync(actor, Enum.IsDefined(permission) ? permission.ToString() : "UnknownPermission");
        return null;
    }

    // A job binding is made only from authorized identity; caller-supplied tenant is never persisted as authority.
    public async ValueTask<TenantJobBinding?> BindJobAsync(TenantResource resource, CancellationToken cancellationToken = default)
    {
        var actor = await AuthorizeAsync(TenantPermission.Propose, resource, cancellationToken);
        return actor is null ? null : new TenantJobBinding(actor, resource.ModelId);
    }

    private async ValueTask DenyAsync(TenantActor? actor, string action)
    {
        await _audit.RecordAsync(new TenantDenial(actor?.TenantId, actor?.Subject, action,
            _http.HttpContext?.TraceIdentifier ?? "no-request", DateTimeOffset.UtcNow), CancellationToken.None);
    }
}

public static class TenantIdentityRegistration
{
    public static IServiceCollection AddWowDiTenantIdentity(this IServiceCollection services, TenantOidcOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority) ||
            authority.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(authority.UserInfo) ||
            !string.IsNullOrEmpty(authority.Query) || !string.IsNullOrEmpty(authority.Fragment) ||
            string.IsNullOrWhiteSpace(options.Audience) || string.IsNullOrWhiteSpace(options.TenantClaim) ||
            options.TenantClaim is "iss" or "sub")
            throw new ArgumentException("HTTPS OIDC authority, audience and distinct tenant claim are required.", nameof(options));

        services.AddSingleton(options);
        services.AddHttpContextAccessor();
        services.AddScoped<TenantAccess>();
        // Directory and denial sink must be explicitly registered by the host.
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(bearer =>
            {
                bearer.Authority = options.Authority;
                bearer.Audience = options.Audience;
                bearer.RequireHttpsMetadata = true;
                bearer.MapInboundClaims = false;
                bearer.IncludeErrorDetails = false;
                bearer.SaveToken = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = options.Authority,
                    ValidateAudience = true, ValidAudience = options.Audience,
                    RequireAudience = true, RequireExpirationTime = true,
                    ValidateLifetime = true, ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true, ClockSkew = TimeSpan.Zero,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
                        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512],
                    // This profile requires an explicit JWT access-token type, not an ID token.
                    ValidTypes = ["at+jwt", "application/at+jwt"],
                    IssuerValidator = (issuer, _, _) => issuer == options.Authority
                        ? issuer : throw new SecurityTokenInvalidIssuerException("Invalid issuer."),
                    AudienceValidator = (audiences, _, _) => audiences is not null &&
                        audiences.Contains(options.Audience, StringComparer.Ordinal)
                };
                bearer.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var actor = context.Principal;
                        var issuer = actor is null ? null : TenantAccess.Single(actor, "iss");
                        var subject = actor is null ? null : TenantAccess.Single(actor, "sub");
                        var tenant = actor is null ? null : TenantAccess.Single(actor, options.TenantClaim);
                        if (issuer != options.Authority || subject is null || tenant is null ||
                            !await context.HttpContext.RequestServices.GetRequiredService<ITenantMembershipDirectory>()
                                .IsActiveAsync(new TenantActor(issuer!, subject, tenant), context.HttpContext.RequestAborted))
                            context.Fail("Invalid tenant membership.");
                    },
                    OnChallenge = async context =>
                    {
                        await context.HttpContext.RequestServices.GetRequiredService<ITenantDenialAudit>()
                            .RecordAsync(new TenantDenial(null, null, "Authenticate",
                                context.HttpContext.TraceIdentifier, DateTimeOffset.UtcNow), CancellationToken.None);
                    }
                };
            });
        services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser().Build());
        return services;
    }
}
