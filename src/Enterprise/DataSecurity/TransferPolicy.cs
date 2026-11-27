using WowDi.Enterprise.Identity;

namespace WowDi.Enterprise.DataSecurity;

// Exact tenant-scoped destination registration. This class performs no network access.
// A transport must pin DNS/IP egress and disable redirects or reauthorize each hop.
public sealed record ApprovedDestination(string TenantId, string Endpoint);
public sealed class DestinationPolicy
{
    private readonly HashSet<(string Tenant, string Endpoint)> _approved;
    public DestinationPolicy(IEnumerable<ApprovedDestination> destinations)
    {
        _approved = new();
        foreach (var destination in destinations)
        {
            if (!TryCanonical(destination.Endpoint, out var endpoint) || string.IsNullOrWhiteSpace(destination.TenantId))
                throw new ArgumentException("Invalid destination registration.", nameof(destinations));
            _approved.Add((destination.TenantId, endpoint));
        }
    }

    public bool Allows(TenantActor actor, string endpoint) =>
        TryCanonical(endpoint, out var canonical) && _approved.Contains((actor.TenantId, canonical));

    private static bool TryCanonical(string value, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrEmpty(value) || value.Length > 2048 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
            value.Contains('\\') || value.Contains('%') || value.Contains('?') || value.Contains('#') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
            uri.HostNameType != UriHostNameType.Dns || uri.IsLoopback || uri.Host.EndsWith('.') ||
            !uri.IdnHost.Contains('.') || uri.IdnHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(value, uri.AbsoluteUri, StringComparison.Ordinal)) return false;
        canonical = uri.AbsoluteUri;
        return true;
    }
}

// Process-local fixed-window admission. Register as singleton; distributed limits belong to E08.
public sealed class TenantAdmissionLimiter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly int _permits, _maxTenants;
    private readonly TimeSpan _duration;
    private sealed record Window(long Started, int Used);

    public TenantAdmissionLimiter(int permits, TimeSpan duration, int maxTenants, TimeProvider? clock = null)
    {
        if (permits <= 0 || maxTenants is < 1 or > 100000 || duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(permits));
        (_permits, _duration, _maxTenants, _clock) = (permits, duration, maxTenants, clock ?? TimeProvider.System);
    }

    public bool TryAcquire(TenantActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            if (_windows.TryGetValue(actor.TenantId, out var current))
            {
                if (_clock.GetElapsedTime(current.Started, now) >= _duration)
                    current = new(now, 0);
                if (current.Used >= _permits) return false;
                _windows[actor.TenantId] = current with { Used = current.Used + 1 };
                return true;
            }
            if (_windows.Count >= _maxTenants)
            {
                foreach (var key in _windows.Where(pair => _clock.GetElapsedTime(pair.Value.Started, now) >= _duration)
                    .Select(pair => pair.Key).ToArray()) _windows.Remove(key);
                if (_windows.Count >= _maxTenants) return false;
            }
            _windows.Add(actor.TenantId, new(now, 1));
            return true;
        }
    }
}
