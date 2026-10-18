using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace Palanca.Security;

internal sealed class NetworkOptions
{
    public const string SectionName = "Palanca:Network";

    /// <summary>
    /// Addresses or CIDR ranges of the reverse proxy (Caddy). Only requests from these peers may name the client
    /// address, through <c>X-Real-IP</c>, and the original scheme, through <c>X-Forwarded-Proto</c>.
    /// Empty means the immediate peer is the client and the request arrived as it appears.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = [];

    public bool IsValid() => TrustedProxies.TrueForAll(entry => IPNetwork.TryParse(entry, out _) || IPAddress.TryParse(entry, out _));
}

internal static class ClientAddress
{
    /// <summary>
    /// Resolves the client address used for rate limits (FLG-032), and the scheme the browser used. Caddy passes the
    /// address it resolved from Cloudflare as <c>X-Real-IP</c>, and terminates TLS, so it reports <c>https</c> in
    /// <c>X-Forwarded-Proto</c>; the dashboard's Secure-only cookies need to see it. Both headers are ignored from
    /// any peer that is not a trusted proxy.
    /// </summary>
    public static IServiceCollection AddClientAddress(this IServiceCollection services)
    {
        services.AddOptions<NetworkOptions>()
            .BindConfiguration(NetworkOptions.SectionName)
            .Validate(options => options.IsValid(), "Palanca:Network:TrustedProxies must hold IP addresses or CIDR ranges.")
            .ValidateOnStart();

        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<NetworkOptions>>((options, network) =>
        {
            // With no known proxy, the middleware would trust every peer, so it processes nothing at all.
            options.ForwardedHeaders = network.Value.TrustedProxies.Count == 0
                ? ForwardedHeaders.None
                : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardedForHeaderName = "X-Real-IP";
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var entry in network.Value.TrustedProxies)
            {
                if (IPNetwork.TryParse(entry, out var range))
                {
                    options.KnownIPNetworks.Add(range);
                }
                else
                {
                    options.KnownProxies.Add(IPAddress.Parse(entry));
                }
            }
        });

        return services;
    }
}
