using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Palanca.Client.Tests;

/// <summary>The bounded startup wait and configuration (FLG-051, AC-007's startup half).</summary>
public sealed class StartupTests
{
    [Fact]
    public async Task StartupEndsAsSoonAsTheFirstFlagsArrive()
    {
        await using var client = ClientHarness.Create();
        client.Service.Flags(1, """{"new-search":true}""");

        await client.Sync.StartAsync(Ct);

        Assert.True(client.Flags.IsEnabled("new-search", defaultValue: false));
        Assert.Empty(client.LogsAtLeast(LogLevel.Warning));
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData(5, 5)]
    public async Task StartupGoesOnWithDefaultsWhenTheServiceDoesNotAnswer(int? configured, int expected)
    {
        await using var client = ClientHarness.Create(options =>
        {
            if (configured is { } seconds)
            {
                options.StartupWait = TimeSpan.FromSeconds(seconds);
            }
        });
        client.Service.ThenHang();

        var start = client.Sync.StartAsync(Ct);
        await ClientHarness.WaitUntilAsync(() => !client.Service.Requests.IsEmpty);
        client.Time.Advance(TimeSpan.FromSeconds(expected) - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50, Ct);
        Assert.False(start.IsCompleted);

        client.Time.Advance(TimeSpan.FromMilliseconds(1));
        await start;

        Assert.True(client.Flags.IsEnabled("new-search", defaultValue: true));
        Assert.Equal(
            $"No flags from Palanca within the startup wait of {TimeSpan.FromSeconds(expected)}; starting with code defaults until they arrive",
            Assert.Single(client.LogsAtLeast(LogLevel.Warning)).Message);
    }

    [Fact]
    public async Task AZeroStartupWaitDoesNotWait()
    {
        await using var client = ClientHarness.Create(options => options.StartupWait = TimeSpan.Zero);
        client.Service.ThenHang();

        await client.Sync.StartAsync(Ct);

        Assert.Empty(client.LogsAtLeast(LogLevel.Warning));
    }

    [Fact]
    public async Task AHostStartsWithinTheWaitWhileTheServiceIsDown()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.AddFakeLogging();
        builder.Services.AddPalanca(options =>
        {
            options.Endpoint = new Uri("http://palanca:8080");
            options.SdkKey = ClientHarness.SdkKey;
            options.Application = "hogarhero";
            options.Environment = "staging";
            options.StartupWait = TimeSpan.FromMilliseconds(300);
        });
        var refusing = new FakeService();
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => refusing));
        using var host = builder.Build();

        var stopwatch = Stopwatch.StartNew();
        await host.StartAsync(Ct);
        stopwatch.Stop();

        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2));
        Assert.True(host.Services.GetRequiredService<IPalancaFlags>().IsEnabled("new-search", defaultValue: true));

        // Failed polls never stop the host: an exception leaving a hosted service would.
        await ClientHarness.WaitUntilAsync(() => refusing.Requests.Count >= 2);
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task AnUnconfiguredClientServesDefaultsAndSaysWhatIsMissing()
    {
        await using var client = ClientHarness.Create(options =>
        {
            options.Endpoint = null;
            options.SdkKey = " ";
        });

        await client.Sync.StartAsync(Ct);
        await Task.Delay(50, Ct);

        Assert.Empty(client.Service.Requests);
        Assert.True(client.Flags.IsEnabled("new-search", defaultValue: true));
        var error = Assert.Single(client.LogsAtLeast(LogLevel.Warning));
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Equal("Palanca is not configured, so every flag reads its code default: Endpoint is missing; SdkKey is missing", error.Message);
        var health = await client.CheckHealthAsync();
        Assert.Equal(HealthStatus.Degraded, health.Status);
        Assert.Equal("Every flag reads its code default. Palanca is not configured: Endpoint is missing; SdkKey is missing.", health.Description);
    }

    public static TheoryData<string?, string?, string?, string> MissingSettings => new()
    {
        { "palanca:8080", "hogarhero", "production", "Endpoint must be an absolute http or https address" },
        { "ftp://palanca", "hogarhero", "production", "Endpoint must be an absolute http or https address" },
        { "http://palanca:8080", null, "production", "Application is missing" },
        { "http://palanca:8080", "hogarhero", "", "Environment is missing" },
    };

    [Theory]
    [MemberData(nameof(MissingSettings))]
    public void EachRequiredSettingIsChecked(string? endpoint, string? application, string? environment, string problem)
    {
        var configuration = ClientConfiguration.From(new PalancaOptions
        {
            Endpoint = endpoint is null ? null : new Uri(endpoint, UriKind.RelativeOrAbsolute),
            SdkKey = ClientHarness.SdkKey,
            Application = application,
            Environment = environment,
        });

        Assert.Null(configuration.Settings);
        Assert.Equal([problem], configuration.Problems);
    }

    [Fact]
    public async Task DurationsOutOfRangeAreRaisedToTheirMinimum()
    {
        await using var client = ClientHarness.Create(options =>
        {
            options.PollInterval = TimeSpan.FromSeconds(1);
            options.StartupWait = TimeSpan.FromSeconds(-1);
            options.StaleAfter = TimeSpan.FromSeconds(2);
        });
        client.Service.Flags(1, "{}");

        await client.StartAsync();

        var settings = client.Services.GetRequiredService<ClientConfiguration>().Settings!;
        Assert.Equal((TimeSpan.FromSeconds(5), TimeSpan.Zero, TimeSpan.FromSeconds(5)), (settings.PollInterval, settings.StartupWait, settings.StaleAfter));
        Assert.Equal(
            [
                "Palanca option adjusted: PollInterval 00:00:01 is under the minimum, so the client polls every 00:00:05",
                "Palanca option adjusted: StartupWait -00:00:01 is negative, so startup does not wait",
                "Palanca option adjusted: StaleAfter 00:00:02 is under the poll interval, so it is raised to 00:00:05",
            ],
            client.LogsAtLeast(LogLevel.Warning).Select(record => record.Message));
    }

    [Fact]
    public void TheClientBindsFromConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Palanca:Endpoint"] = "http://palanca:8080",
            // HogarHero reads a mounted secret named by <key>:File into <key>. A trailing line break is trimmed.
            ["Palanca:SdkKey:File"] = "/run/secrets/palanca_sdk_key",
            ["Palanca:SdkKey"] = ClientHarness.SdkKey + "\n",
            ["Palanca:Application"] = "hogarhero",
            ["Palanca:Environment"] = "staging",
            ["Palanca:PollInterval"] = "00:00:10",
            ["Palanca:StartupWait"] = "00:00:01",
            ["Palanca:StaleAfter"] = "00:02:00",
        }).Build();
        var services = new ServiceCollection().AddLogging().AddPalanca(configuration.GetSection(PalancaOptions.SectionName));
        using var provider = services.BuildServiceProvider();

        var settings = provider.GetRequiredService<ClientConfiguration>().Settings;

        Assert.Equal(
            new ClientSettings(new Uri("http://palanca:8080/api/v1/flags"), ClientHarness.SdkKey, "hogarhero", "staging", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2)),
            settings);
    }

    [Fact]
    public void TheDefaultsAreThePrdsTimings()
    {
        var options = new PalancaOptions();

        Assert.Equal((TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5)), (options.PollInterval, options.StartupWait, options.StaleAfter));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
