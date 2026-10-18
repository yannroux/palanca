namespace Palanca.Security;

/// <summary>
/// The times of recent events per key, over a sliding one-minute window, for limits counted per minute: an event
/// stops counting exactly a minute after it happened.
/// </summary>
internal sealed class SlidingWindowLog(TimeProvider timeProvider)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, Queue<DateTimeOffset>> _events = [];
    private readonly Lock _lock = new();
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    /// <summary>The number of keys with recent events, for tests.</summary>
    internal int TrackedKeys
    {
        get
        {
            lock (_lock)
            {
                return _events.Count;
            }
        }
    }

    /// <summary>The number of events for <paramref name="key"/> within the last minute.</summary>
    public int Count(string key)
    {
        lock (_lock)
        {
            return Prune(key, timeProvider.GetUtcNow())?.Count ?? 0;
        }
    }

    public void Add(string key)
    {
        lock (_lock)
        {
            Append(key, timeProvider.GetUtcNow());
        }
    }

    /// <summary>
    /// Adds an event for <paramref name="key"/> unless <paramref name="limit"/> events already happened within the
    /// last minute; then <paramref name="retryAfter"/> is how long until the oldest of them stops counting.
    /// </summary>
    public bool TryAdd(string key, int limit, out TimeSpan retryAfter)
    {
        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (Prune(key, now) is { } events && events.Count >= limit)
            {
                retryAfter = events.Peek() + Window - now;
                return false;
            }

            Append(key, now);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private void Append(string key, DateTimeOffset now)
    {
        SweepIfDue(now);
        var events = Prune(key, now);
        if (events is null)
        {
            events = new Queue<DateTimeOffset>();
            _events[key] = events;
        }

        events.Enqueue(now);
    }

    // Keys that never return would otherwise stay forever, so once a minute every entry is pruned.
    private void SweepIfDue(DateTimeOffset now)
    {
        if (now - _lastSweep < Window)
        {
            return;
        }

        _lastSweep = now;
        foreach (var key in _events.Keys.ToList())
        {
            Prune(key, now);
        }
    }

    private Queue<DateTimeOffset>? Prune(string key, DateTimeOffset now)
    {
        if (!_events.TryGetValue(key, out var events))
        {
            return null;
        }

        while (events.TryPeek(out var oldest) && now - oldest >= Window)
        {
            events.Dequeue();
        }

        if (events.Count == 0)
        {
            _events.Remove(key);
            return null;
        }

        return events;
    }
}
