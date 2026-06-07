using System.Collections.Concurrent;
using System.Net;

namespace NutAgent.Nut;

// Per-IP login attempt throttle, shared across all NutSession instances on a server so
// repeated USERNAME/PASSWORD guesses are tracked across reconnects rather than reset
// every time the attacker opens a new TCP connection.
internal sealed class LoginThrottle
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan Window  = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);

    private sealed record Entry(int Failures, DateTime WindowStartUtc, DateTime? LockedUntilUtc);

    private readonly ConcurrentDictionary<IPAddress, Entry> _entries = new();

    public bool IsLockedOut(IPAddress address) =>
        _entries.TryGetValue(address, out var e) &&
        e.LockedUntilUtc is { } until && DateTime.UtcNow < until;

    public void RecordFailure(IPAddress address)
    {
        var now = DateTime.UtcNow;
        _entries.AddOrUpdate(address,
            _ => new Entry(1, now, null),
            (_, e) =>
            {
                if (now - e.WindowStartUtc > Window)
                    e = new Entry(0, now, null);

                var failures    = e.Failures + 1;
                var lockedUntil = failures >= MaxFailures ? now + Lockout : e.LockedUntilUtc;
                return new Entry(failures, e.WindowStartUtc, lockedUntil);
            });
    }

    public void RecordSuccess(IPAddress address) => _entries.TryRemove(address, out _);
}
