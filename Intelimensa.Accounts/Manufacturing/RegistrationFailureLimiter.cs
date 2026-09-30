using System.Collections.Concurrent;

namespace Intelimensa.Accounts.Manufacturing;

/// <summary>
/// Per-user cap on failed device registrations (unknown serial, wrong code, unregisterable unit),
/// so the endpoint can't be used to guess serials or codes. In-memory by design: one process, one
/// deploy (see CLAUDE.md), and losing the counters on restart only ever resets a window.
/// </summary>
public class RegistrationFailureLimiter
{
    public const int MaxFailures = 10;

    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset WindowStart)> _state = new();

    /// <summary>Seconds until the user may try again, or null if they aren't currently blocked.</summary>
    public int? RetryAfterSeconds(string userId, DateTimeOffset now)
    {
        if (_state.TryGetValue(userId, out var s) && now - s.WindowStart < Window && s.Failures >= MaxFailures)
            return (int)Math.Ceiling((s.WindowStart + Window - now).TotalSeconds);
        return null;
    }

    public void RecordFailure(string userId, DateTimeOffset now)
    {
        _state.AddOrUpdate(
            userId,
            _ => (1, now),
            (_, s) => now - s.WindowStart >= Window ? (1, now) : (s.Failures + 1, s.WindowStart));

        // Opportunistic cleanup so abandoned entries don't accumulate.
        if (_state.Count > 1000)
            foreach (var (key, s) in _state)
                if (now - s.WindowStart >= Window)
                    _state.TryRemove(key, out _);
    }
}
