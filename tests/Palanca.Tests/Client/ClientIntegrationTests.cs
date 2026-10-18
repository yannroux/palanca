using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Palanca.Applications;
using Palanca.Client;
using Palanca.Data;
using Palanca.Flags;
using Palanca.SdkKeys;
using Palanca.Tests.Owners;

namespace Palanca.Tests.Client;

/// <summary>
/// The client package against the service in the test process, hosted as a worker would host it
/// (FLG-050 to FLG-054, AC-002, AC-003, AC-004, AC-010, AC-012).
/// </summary>
public sealed class ClientIntegrationTests : IAsyncLifetime
{
    private static readonly ChangeActor Owner = new(ChangeSource.Dashboard, OwnerSetup.Email);
    private readonly PalancaFactory _factory = new();

    // The clients poll on this clock; the service keeps real time.
    private readonly FakeTimeProvider _clientTime = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
    private readonly List<IHost> _hosts = [];
    private string _staging = null!;
    private string _production = null!;
    private string _billing = null!;

    public async ValueTask InitializeAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<ApplicationCommands>();
        var flags = scope.ServiceProvider.GetRequiredService<FlagCommands>();
        var keys = scope.ServiceProvider.GetRequiredService<SdkKeyCommands>();
        await applications.CreateAsync("hogarhero", "HogarHero", Owner, Ct);
        await applications.CreateEnvironmentAsync("hogarhero", "staging", "Staging", false, Owner, Ct);
        await applications.CreateEnvironmentAsync("hogarhero", "production", "Production", true, Owner, Ct);
        await flags.CreateAsync("hogarhero", "new-search", "New search results page", ["staging"], Owner, Ct);
        await flags.CreateAsync("hogarhero", "operator-denue-imports", "Operator-started DENUE imports", [], Owner, Ct);
        await applications.CreateAsync("billing", "Billing", Owner, Ct);
        await applications.CreateEnvironmentAsync("billing", "production", "Production", false, Owner, Ct);
        await flags.CreateAsync("billing", "invoice-pdf", "Invoices as PDF", ["production"], Owner, Ct);
        _staging = (await keys.CreateAsync("hogarhero", "staging", "hogarhero-web", Owner, Ct)).Key!;
        _production = (await keys.CreateAsync("hogarhero", "production", "hogarhero-web", Owner, Ct)).Key!;
        _billing = (await keys.CreateAsync("billing", "production", "billing-web", Owner, Ct)).Key!;
    }

    [Fact]
    public async Task EachClientServesItsEnvironmentAndFollowsChanges()
    {
        var staging = await StartClientAsync(_staging, "hogarhero", "staging");
        var production = await StartClientAsync(_production, "hogarhero", "production");

        // The startup wait has already brought each environment's flags.
        Assert.True(staging.Flags.IsEnabled("new-search", defaultValue: false));
        Assert.False(production.Flags.IsEnabled("new-search", defaultValue: true));
        Assert.False(staging.Flags.IsEnabled("operator-denue-imports", defaultValue: true));

        await RunAsync<FlagValueWriter>(writer => writer.SetAsync("hogarhero", "operator-denue-imports", "staging", true, Owner, confirmed: false, Ct));
        await PollUntilAsync(() => staging.Flags.IsEnabled("operator-denue-imports", defaultValue: false));

        Assert.False(production.Flags.IsEnabled("operator-denue-imports", defaultValue: false));
        Assert.Equal(HealthStatus.Healthy, (await staging.CheckHealthAsync()).Status);
    }

    [Fact]
    public async Task AnUnchangedPollGetsNotModified()
    {
        var staging = await StartClientAsync(_staging, "hogarhero", "staging");

        await PollUntilAsync(() => staging.Statuses.Count >= 3);

        Assert.Equal(HttpStatusCode.OK, staging.Statuses.First());
        Assert.All(staging.Statuses.Skip(1), status => Assert.Equal(HttpStatusCode.NotModified, status));
    }

    [Fact]
    public async Task AProductionClientGivenTheStagingKeyKeepsItsDefaultsAndLogsTheMismatch()
    {
        var misconfigured = await StartClientAsync(_staging, "hogarhero", "production", startupWait: TimeSpan.Zero);

        await PollUntilAsync(() => misconfigured.Logs.GetSnapshot().Any(record => record.Level == LogLevel.Error));

        // Staging has new-search on; production code keeps its default.
        Assert.False(misconfigured.Flags.IsEnabled("new-search", defaultValue: false));
        Assert.Equal(
            "Palanca sent the flags of hogarhero/staging, but this client expects hogarhero/production: check that its SDK key belongs to that environment. Serving code defaults",
            misconfigured.Logs.GetSnapshot().Single(record => record.Level == LogLevel.Error).Message);
        Assert.Equal(HealthStatus.Degraded, (await misconfigured.CheckHealthAsync()).Status);
    }

    [Fact]
    public async Task ARevokedKeyKeepsTheLastValues()
    {
        var staging = await StartClientAsync(_staging, "hogarhero", "staging");
        var keyId = await _factory.WithDbAsync(db => db.SdkKeys.Where(k => k.Environment.Key == "staging").Select(k => k.Id).SingleAsync(Ct));
        await RunAsync<SdkKeyCommands>(keys => keys.RevokeAsync("hogarhero", keyId, Owner, Ct));

        await PollUntilAsync(() => staging.Statuses.Contains(HttpStatusCode.Unauthorized));

        Assert.True(staging.Flags.IsEnabled("new-search", defaultValue: false));
        var warning = staging.Logs.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
        Assert.Contains("Palanca answered 401 Unauthorized", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(staging.Logs.GetSnapshot(), record =>
            record.Message.Contains(_staging, StringComparison.Ordinal) || (record.Exception?.ToString().Contains(_staging, StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public async Task ASecondApplicationReadsOnlyItsOwnFlags()
    {
        var billing = await StartClientAsync(_billing, "billing", "production");

        Assert.True(billing.Flags.IsEnabled("invoice-pdf", defaultValue: false));
        Assert.True(billing.Flags.IsEnabled("new-search", defaultValue: true));
        Assert.False(billing.Flags.IsEnabled("new-search", defaultValue: false));
        Assert.Contains(billing.Logs.GetSnapshot(), record =>
            record.Level == LogLevel.Warning && record.Message.StartsWith("Flag new-search is not in Palanca's billing/production flags", StringComparison.Ordinal));
    }

    private async Task<ClientHost> StartClientAsync(string sdkKey, string application, string environment, TimeSpan? startupWait = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.SetMinimumLevel(LogLevel.Trace).AddFakeLogging();
        builder.Services.AddSingleton<TimeProvider>(_clientTime);
        builder.Services.AddPalanca(options =>
        {
            options.Endpoint = new Uri(PalancaFactory.Origin);
            options.SdkKey = sdkKey;
            options.Application = application;
            options.Environment = environment;
            options.StartupWait = startupWait ?? options.StartupWait;
        });
        builder.Services.AddHealthChecks().AddPalanca();
        var statuses = new ConcurrentQueue<HttpStatusCode>();
        // The client drops handlers added to every client, so the recorder wraps the primary handler instead.
        builder.Services.ConfigureHttpClientDefaults(http => http
            .ConfigurePrimaryHttpMessageHandler(() => new StatusRecorder(statuses) { InnerHandler = _factory.Server.CreateHandler() }));
        var host = builder.Build();
        _hosts.Add(host);

        // With a startup wait, a first sync that succeeds ends it without the fake clock moving.
        await host.StartAsync(Ct);
        return new ClientHost(host, statuses);
    }

    /// <summary>Moves the clients' clock a poll at a time until the condition holds.</summary>
    private async Task PollUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the client.");
            _clientTime.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(20, Ct);
        }
    }

    private async Task RunAsync<T>(Func<T, Task> action)
        where T : notnull
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<T>());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }

        await _factory.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record ClientHost(IHost Host, ConcurrentQueue<HttpStatusCode> Statuses)
    {
        public IPalancaFlags Flags => Host.Services.GetRequiredService<IPalancaFlags>();

        public FakeLogCollector Logs => Host.Services.GetFakeLogCollector();

        public async Task<HealthReportEntry> CheckHealthAsync() =>
            (await Host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(Ct)).Entries["palanca"];
    }

    private sealed class StatusRecorder(ConcurrentQueue<HttpStatusCode> statuses) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            statuses.Enqueue(response.StatusCode);
            return response;
        }
    }
}
