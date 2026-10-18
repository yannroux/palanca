using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Palanca.Security;

internal sealed class FailedAuthenticationOptions
{
    public const string SectionName = "Palanca:FailedAuthentication";

    /// <summary>Failures allowed per client address within one minute before further attempts are refused (FLG-032).</summary>
    [Range(1, 1000)]
    public int PermitLimit { get; set; } = 10;
}

/// <summary>
/// Counts failed authentication attempts per client address (FLG-032). Once the limit is reached within one
/// minute, further attempts from that address are refused until the oldest failure is a minute old.
/// An IPv6 client is counted by its /64 network, which a single host controls.
/// </summary>
internal sealed class FailedAuthenticationLimiter(TimeProvider timeProvider, IOptions<FailedAuthenticationOptions> options)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, Queue<DateTimeOffset>> _failures = [];
    private readonly Lock _lock = new();
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    /// <summary>The number of client addresses with recent failures, for tests.</summary>
    internal int TrackedAddresses
    {
        get
        {
            lock (_lock)
            {
                return _failures.Count;
            }
        }
    }

    public bool IsLimited(IPAddress? address)
    {
        lock (_lock)
        {
            return Prune(Key(address), timeProvider.GetUtcNow()) is { } failures && failures.Count >= options.Value.PermitLimit;
        }
    }

    public void RecordFailure(IPAddress? address)
    {
        var key = Key(address);
        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            SweepIfDue(now);
            var failures = Prune(key, now);
            if (failures is null)
            {
                failures = new Queue<DateTimeOffset>();
                _failures[key] = failures;
            }

            failures.Enqueue(now);
        }
    }

    // Addresses that never return would otherwise stay forever, so once a minute every entry is pruned.
    private void SweepIfDue(DateTimeOffset now)
    {
        if (now - _lastSweep < Window)
        {
            return;
        }

        _lastSweep = now;
        foreach (var key in _failures.Keys.ToList())
        {
            Prune(key, now);
        }
    }

    private Queue<DateTimeOffset>? Prune(string key, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(key, out var failures))
        {
            return null;
        }

        while (failures.TryPeek(out var oldest) && now - oldest >= Window)
        {
            failures.Dequeue();
        }

        if (failures.Count == 0)
        {
            _failures.Remove(key);
            return null;
        }

        return failures;
    }

    // Requests without an address, such as in-process tests, share one bucket.
    private static string Key(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }
}
