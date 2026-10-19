using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palanca.Applications;
using Palanca.Data;
using Palanca.Metrics;
using Palanca.SdkKeys;
using Palanca.Tests.Owners;

namespace Palanca.Tests.Metrics;

/// <summary>Request, error, and failed-authentication counters at <c>/metrics</c> (FLG-064).</summary>
public sealed class MetricsEndpointTests
{
    [Fact]
    public async Task TheEndpointServesPrometheusTextWithoutSignIn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new PalancaFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/metrics", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(response.Content.Headers.ContentType!.Parameters, p => p.Name == "version" && p.Value == "0.0.4");
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("# TYPE palanca_requests_total counter\n", body, StringComparison.Ordinal);
        Assert.Contains("# TYPE palanca_authentication_failures_total counter\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestsAreCountedByAreaAndStatusClass()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new PalancaFactory();
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/health/live", "/health/ready", "/api/v1/flags", "/api/manage/v1/applications", "/sign-in", "/metrics" })
        {
            (await client.GetAsync(path, ct)).Dispose();
        }

        var series = await ScrapeAsync(client);
        Assert.Equal(2, series["palanca_requests_total{area=\"health\",status_class=\"2xx\"}"]);
        Assert.Equal(1, series["palanca_requests_total{area=\"evaluation\",status_class=\"4xx\"}"]);
        Assert.Equal(1, series["palanca_requests_total{area=\"management\",status_class=\"4xx\"}"]);
        Assert.Equal(1, series["palanca_requests_total{area=\"dashboard\",status_class=\"2xx\"}"]);
        // The earlier scrape; a scrape is counted once it has answered, so it never includes itself.
        Assert.Equal(1, series["palanca_requests_total{area=\"metrics\",status_class=\"2xx\"}"]);
    }

    [Fact]
    public async Task FailedAuthenticationsAreCountedByCredential()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new PalancaFactory();
        using var api = factory.CreateClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "palanca_sdk_" + new string('a', 43));
        (await api.GetAsync("/api/v1/flags", ct)).Dispose();
        (await api.GetAsync("/api/v1/flags", ct)).Dispose();
        // An SDK key is no management token.
        (await api.GetAsync("/api/manage/v1/applications", ct)).Dispose();
        using var dashboard = new DashboardClient(factory);
        var enrollPage = await dashboard.GetPageAsync("/enroll");
        (await dashboard.PostOptionsAsync("/enroll/options", enrollPage, new { token = "not-a-link" })).Dispose();

        using var scraper = factory.CreateClient();
        var series = await ScrapeAsync(scraper);

        Assert.Equal(2, series["palanca_authentication_failures_total{credential=\"sdk_key\"}"]);
        Assert.Equal(1, series["palanca_authentication_failures_total{credential=\"management_token\"}"]);
        Assert.Equal(1, series["palanca_authentication_failures_total{credential=\"enrollment_link\"}"]);
        Assert.DoesNotContain(series.Keys, key => key.Contains("credential=\"passkey\"", StringComparison.Ordinal));
    }

    [Fact]
    public void EachKindOfCredentialHasItsOwnTagValue() =>
        Assert.Equal(
            ["sdk_key", "management_token", "passkey", "enrollment_link"],
            Enum.GetValues<FailedCredential>().Select(ServerMetrics.Credential));

    [Fact]
    public async Task AnUnhandledFailureIsCountedAsAnError()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new PalancaFactory { EnvironmentName = "Production" };

        // A real server: the test server would rethrow the failure into the test instead of answering.
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        factory.StartServer();
        var address = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        string key;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var owner = new ChangeActor(ChangeSource.Dashboard, OwnerSetup.Email);
            var applications = scope.ServiceProvider.GetRequiredService<ApplicationCommands>();
            await applications.CreateAsync("hogarhero", "HogarHero", owner, ct);
            await applications.CreateEnvironmentAsync("hogarhero", "staging", "Staging", false, owner, ct);
            key = (await scope.ServiceProvider.GetRequiredService<SdkKeyCommands>().CreateAsync("hogarhero", "staging", "web", owner, ct)).Key!;
        }

        await factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE SdkKeys", ct));
        using var api = new HttpClient { BaseAddress = new Uri(address) };
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using (var response = await api.GetAsync("/api/v1/flags", ct))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        api.DefaultRequestHeaders.Authorization = null;
        var series = await ScrapeAsync(api);
        Assert.Equal(1, series["palanca_requests_total{area=\"evaluation\",status_class=\"5xx\"}"]);
    }

    /// <summary>Reads every sample of a scrape, by metric name and labels.</summary>
    private static async Task<Dictionary<string, long>> ScrapeAsync(HttpClient client)
    {
        var body = await client.GetStringAsync("/metrics", TestContext.Current.CancellationToken);
        return body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#'))
            .Select(line => line.Split(' '))
            .ToDictionary(parts => parts[0], parts => long.Parse(parts[1], CultureInfo.InvariantCulture));
    }
}

/// <summary>Creates meters for tests that build the service's classes without a host.</summary>
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options.Name, options.Version, options.Tags, scope: this);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose() => _meters.ForEach(meter => meter.Dispose());
}
