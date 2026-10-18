using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
using Palanca.ManagementTokens;
using Palanca.SdkKeys;
using Palanca.Tests.Owners;

namespace Palanca.Tests.Client;

/// <summary>
/// The client package against the service in the test process, hosted as a worker would host it
/// (FLG-050 to FLG-054, AC-001 to AC-004, AC-010, AC-012, and AC-015 rehearsed).
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
    private string _billingStaging = null!;

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
        await applications.CreateEnvironmentAsync("billing", "staging", "Staging", false, Owner, Ct);
        await applications.CreateEnvironmentAsync("billing", "production", "Production", false, Owner, Ct);
        await flags.CreateAsync("billing", "invoice-pdf", "Invoices as PDF", ["production"], Owner, Ct);
        await flags.CreateAsync("billing", "credit-notes", "Credit notes", ["staging"], Owner, Ct);
        _staging = (await keys.CreateAsync("hogarhero", "staging", "hogarhero-web", Owner, Ct)).Key!;
        _production = (await keys.CreateAsync("hogarhero", "production", "hogarhero-web", Owner, Ct)).Key!;
        _billing = (await keys.CreateAsync("billing", "production", "billing-web", Owner, Ct)).Key!;
        _billingStaging = (await keys.CreateAsync("billing", "staging", "billing-web", Owner, Ct)).Key!;
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

        // Attempts counts a sync once the client has applied it, unlike Statuses, which sees the response first.
        var productionSyncs = production.Sync.Attempts;
        await RunAsync<FlagValueWriter>(writer => writer.SetAsync("hogarhero", "operator-denue-imports", "staging", true, Owner, confirmed: false, Ct));
        await PollUntilAsync(() => staging.Flags.IsEnabled("operator-denue-imports", defaultValue: false) && production.Sync.Attempts > productionSyncs);

        // Production polled after the change and still serves its own value, off.
        Assert.False(production.Flags.IsEnabled("operator-denue-imports", defaultValue: true));
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
        var keyId = await _factory.WithDbAsync(db => db.SdkKeys.Where(k => k.Environment.Key == "staging" && k.Environment.Application.Key == "hogarhero").Select(k => k.Id).SingleAsync(Ct));
        await RunAsync<SdkKeyCommands>(keys => keys.RevokeAsync("hogarhero", keyId, Owner, Ct));

        await PollUntilAsync(() => staging.Logs.GetSnapshot().Any(record => record.Level == LogLevel.Warning));

        Assert.Contains(HttpStatusCode.Unauthorized, staging.Statuses);
        Assert.True(staging.Flags.IsEnabled("new-search", defaultValue: false));
        var warning = staging.Logs.GetSnapshot().Single(record => record.Level == LogLevel.Warning);
        Assert.Contains("Palanca answered 401 Unauthorized", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(staging.Logs.GetSnapshot(), record =>
            record.Message.Contains(_staging, StringComparison.Ordinal) || (record.Exception?.ToString().Contains(_staging, StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public async Task ASecondApplicationReadsOnlyItsOwnFlagsInEachOfItsEnvironments()
    {
        var billing = await StartClientAsync(_billing, "billing", "production");
        var billingStaging = await StartClientAsync(_billingStaging, "billing", "staging");

        Assert.True(billing.Flags.IsEnabled("invoice-pdf", defaultValue: false));
        Assert.False(billing.Flags.IsEnabled("credit-notes", defaultValue: true));
        Assert.False(billingStaging.Flags.IsEnabled("invoice-pdf", defaultValue: true));
        Assert.True(billingStaging.Flags.IsEnabled("credit-notes", defaultValue: false));

        // HogarHero's flags are unknown to both, so they read their code default.
        Assert.All([billing, billingStaging], client =>
        {
            Assert.True(client.Flags.IsEnabled("new-search", defaultValue: true));
            Assert.False(client.Flags.IsEnabled("new-search", defaultValue: false));
        });
        Assert.Contains(billing.Logs.GetSnapshot(), record =>
            record.Level == LogLevel.Warning && record.Message.StartsWith("Flag new-search is not in Palanca's billing/production flags", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAgentsNewStagingFlagReachesTheStagingClientWithinOnePoll()
    {
        // AC-015 rehearsed in the test process. The criterion itself is checked from outside the VPS, against
        // HogarHero staging, in Phase 3.
        var staging = await StartClientAsync(_staging, "hogarhero", "staging");
        var production = await StartClientAsync(_production, "hogarhero", "production");
        var token = await CreateTokenAsync("claude-hogarhero", TokenPermission.ReadWrite, ["hogarhero"]);
        Assert.False(staging.Flags.IsEnabled("provider-onboarding-v2", defaultValue: false));

        using var api = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/manage/v1/applications/hogarhero/flags")
        {
            Content = JsonContent.Create(new { key = "provider-onboarding-v2", description = "Second version of provider onboarding", values = new { staging = true } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await api.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // One poll interval on the clients' clock; the PRD allows the interval plus 5 seconds. Startup returns once
        // the first flags arrive, a moment before the next poll is scheduled, so the clock waits for that.
        await WaitUntilAsync(() => staging.Sync.Attempts > 0 && production.Sync.Attempts > 0);
        _clientTime.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => staging.Flags.IsEnabled("provider-onboarding-v2", defaultValue: false));

        // Production got the new flag too, off: it reads false even with a default of true.
        await WaitUntilAsync(() => !production.Flags.IsEnabled("provider-onboarding-v2", defaultValue: true));
        var records = await _factory.WithDbAsync(db => db.ChangeRecords.Where(r => r.FlagKey == "provider-onboarding-v2").ToListAsync(Ct));
        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Equal((ChangeSource.Api, "claude-hogarhero"), (record.Source, record.Actor)));
    }

    private async Task<string> CreateTokenAsync(string name, TokenPermission permission, string[] applications)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var (result, created) = await scope.ServiceProvider.GetRequiredService<ManagementTokenCommands>()
            .CreateAsync(name, permission, allApplications: false, applications, 365, Owner, Ct);
        Assert.True(result.Succeeded, result.Message);
        return created!.Token;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the client.");
            await Task.Delay(20, Ct);
        }
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

        public PalancaSync Sync => Host.Services.GetServices<IHostedService>().OfType<PalancaSync>().Single();

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
