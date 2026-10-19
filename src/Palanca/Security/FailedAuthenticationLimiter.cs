using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Palanca.Metrics;

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
/// A public IPv6 client is counted by its /64 network, which a single host controls; private and link-local IPv6
/// addresses, such as containers on a Docker network, are counted one by one. Each failure is also counted in the
/// service's metrics, by the kind of credential presented (FLG-064).
/// </summary>
internal sealed class FailedAuthenticationLimiter(
    TimeProvider timeProvider, IOptions<FailedAuthenticationOptions> options, ServerMetrics metrics)
{
    private readonly SlidingWindowLog _failures = new(timeProvider);

    /// <summary>The number of client addresses with recent failures, for tests.</summary>
    internal int TrackedAddresses => _failures.TrackedKeys;

    public bool IsLimited(IPAddress? address) => _failures.Count(Key(address)) >= options.Value.PermitLimit;

    public void RecordFailure(IPAddress? address, FailedCredential credential)
    {
        _failures.Add(Key(address));
        metrics.RecordAuthenticationFailure(credential);
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

        if (address.AddressFamily != AddressFamily.InterNetworkV6
            || address.IsIPv6UniqueLocal || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }
}
