using System.Net;
using Palanca.Tests.Owners;

namespace Palanca.Tests.Security;

/// <summary>The proxy may name the client address and scheme; nobody else may (FLG-032).</summary>
public sealed class ClientAddressTests
{
    private static readonly IPAddress Caddy = IPAddress.Parse("10.0.0.2");

    [Fact]
    public async Task FailedAttemptsAreCountedPerAddressTheProxyReports()
    {
        await using var factory = new PalancaFactory
        {
            PeerAddress = Caddy,
            Settings = new Dictionary<string, string?> { ["Palanca:Network:TrustedProxies:0"] = "10.0.0.0/24" },
        };
        using var client = new DashboardClient(factory);
        var page = await client.GetPageAsync("/enroll");

        await FailTenTimesAsync(client, page, "203.0.113.1");

        Assert.Equal(HttpStatusCode.TooManyRequests, await AttemptAsync(client, page, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.BadRequest, await AttemptAsync(client, page, "203.0.113.2"));
    }

    [Fact]
    public async Task TheHeaderIsIgnoredFromAnyOtherPeer()
    {
        await using var factory = new PalancaFactory { PeerAddress = IPAddress.Parse("198.51.100.7") };
        using var client = new DashboardClient(factory);
        var page = await client.GetPageAsync("/enroll");

        await FailTenTimesAsync(client, page, "203.0.113.1");

        Assert.Equal(HttpStatusCode.TooManyRequests, await AttemptAsync(client, page, "203.0.113.2"));
    }

    [Fact]
    public async Task TheProxyReportsTheSchemeTheBrowserUsed()
    {
        await using var factory = new PalancaFactory
        {
            PeerAddress = Caddy,
            Settings = new Dictionary<string, string?> { ["Palanca:Network:TrustedProxies:0"] = Caddy.ToString() },
        };
        using var client = factory.CreateDefaultClient(new Uri("http://palanca.internal"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sign-in");
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(DashboardClient.SetCookies(response), c => c.StartsWith("__Host-Palanca.Antiforgery=", StringComparison.Ordinal) && c.Contains("secure", StringComparison.Ordinal));
    }

    private static async Task FailTenTimesAsync(DashboardClient client, DashboardClient.Page page, string address)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, await AttemptAsync(client, page, address));
        }
    }

    private static async Task<HttpStatusCode> AttemptAsync(DashboardClient client, DashboardClient.Page page, string address)
    {
        client.Http.DefaultRequestHeaders.Remove("X-Real-IP");
        client.Http.DefaultRequestHeaders.Add("X-Real-IP", address);
        using var response = await client.PostOptionsAsync("/enroll/options", page, new { token = "invalid" });
        return response.StatusCode;
    }
}
