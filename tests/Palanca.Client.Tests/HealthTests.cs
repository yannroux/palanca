using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Palanca.Client.Tests;

/// <summary>The health check is Degraded at worst (FLG-055, AC-007).</summary>
public sealed class HealthTests
{
    // As in the outage drill (AC-007): the poll at 30 seconds, retries after 1, 2, 4, 8 and 16, then every 30 seconds.
    private static readonly int[] TenMinutesOfFailedPolls = [30, 1, 2, 4, 8, 16, .. Enumerable.Repeat(30, 18)];

    [Fact]
    public async Task BeforeTheFirstSyncTheClientIsDegraded()
    {
        await using var client = ClientHarness.Create();

        var health = await client.CheckHealthAsync();

        Assert.Equal(HealthStatus.Degraded, health.Status);
        Assert.Equal("No flags from Palanca yet, so every flag reads its code default. No sync has finished yet.", health.Description);
    }

    [Fact]
    public async Task FreshFlagsAreHealthy()
    {
        await using var client = ClientHarness.Create();
        client.Service.Flags(7, "{}");
        await client.StartAsync();
        client.Time.Advance(TimeSpan.FromSeconds(12));

        var health = await client.CheckHealthAsync();

        Assert.Equal(HealthStatus.Healthy, health.Status);
        Assert.Equal("Serving revision 7, synced 12 seconds ago.", health.Description);
        Assert.Equal(7L, health.Data["revision"]);
        Assert.Equal("2026-10-18T12:00:00.0000000+00:00", health.Data["lastSuccess"]);
    }

    [Fact]
    public async Task AnOutageIsHealthyUntilTheFlagsAreStaleThenDegraded()
    {
        await using var client = ClientHarness.Create();
        client.Service.Flags(7, "{}");
        client.Service.Otherwise(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await client.StartAsync();

        var elapsed = TimeSpan.Zero;
        foreach (var seconds in TenMinutesOfFailedPolls)
        {
            await client.AdvanceToNextAttemptAsync(TimeSpan.FromSeconds(seconds));
            elapsed += TimeSpan.FromSeconds(seconds);
            var health = await client.CheckHealthAsync();
            Assert.Equal(elapsed > TimeSpan.FromMinutes(5) ? HealthStatus.Degraded : HealthStatus.Healthy, health.Status);
            Assert.EndsWith("The last sync failed: Palanca answered 503 Service Unavailable.", health.Description, StringComparison.Ordinal);
        }

        Assert.True(elapsed >= TimeSpan.FromMinutes(10));
        Assert.StartsWith("The flags are stale. Serving revision 7, synced 601 seconds ago.", (await client.CheckHealthAsync()).Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStaleThresholdIsConfigurable()
    {
        await using var client = ClientHarness.Create(options => options.StaleAfter = TimeSpan.FromMinutes(1));
        client.Service.Flags(7, "{}");
        await client.StartAsync();

        client.Time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(HealthStatus.Healthy, (await client.CheckHealthAsync()).Status);

        // Every poll from here on fails, so the flags pass a minute old during the retries.
        client.Service.Otherwise(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await client.AdvanceToNextAttemptAsync(TimeSpan.FromSeconds(1));
        foreach (var seconds in new[] { 1, 2, 4, 8, 16 })
        {
            await client.AdvanceToNextAttemptAsync(TimeSpan.FromSeconds(seconds));
        }

        Assert.Equal(HealthStatus.Degraded, (await client.CheckHealthAsync()).Status);
    }

    [Fact]
    public async Task ACheckRegisteredWithoutTheClientReportsDegraded()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddHealthChecks().AddPalanca();
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Degraded, report.Entries["palanca"].Status);
        Assert.Equal("The Palanca client is not registered: call services.AddPalanca(...).", report.Entries["palanca"].Description);
    }

    [Fact]
    public async Task ACheckThatThrowsReportsDegraded()
    {
        var services = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(new BrokenClock()).AddPalanca(options =>
        {
            options.Endpoint = new Uri("http://palanca:8080");
            options.SdkKey = ClientHarness.SdkKey;
            options.Application = "hogarhero";
            options.Environment = "production";
        });
        services.AddHealthChecks().AddPalanca();
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ClientState>().Set(new SyncStatus(
            new FlagSnapshot(1, System.Collections.Frozen.FrozenDictionary<string, bool>.Empty, null), DateTimeOffset.UnixEpoch, null));

        var entry = (await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken)).Entries["palanca"];

        Assert.Equal(HealthStatus.Degraded, entry.Status);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    private sealed class BrokenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("The clock is broken.");
    }

    [Fact]
    public async Task TheCheckTakesANameAndTags()
    {
        var services = new ServiceCollection().AddLogging().AddPalanca(options => { });
        services.AddHealthChecks().AddPalanca("flags", ["ready"]);
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["ready"], report.Entries["flags"].Tags);
    }
}
