using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Palanca.Client.Tests;

/// <summary>Polling, conditional requests, failures and the environment check (FLG-052, FLG-053, AC-003, AC-004, AC-010).</summary>
public sealed class SyncTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task EachRequestCarriesTheKeyAndLaterOnesTheLastRevision()
    {
        await using var client = ClientHarness.Create();
        client.Service.Flags(41, """{"new-search":true}""").Then(HttpStatusCode.NotModified);

        await client.StartAsync();
        await client.AdvanceToNextAttemptAsync(Poll);

        var requests = client.Service.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.All(requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(new Uri("http://palanca:8080/api/v1/flags"), request.Uri);
            Assert.Equal($"Bearer {ClientHarness.SdkKey}", request.Authorization);
        });
        Assert.Equal("", requests[0].IfNoneMatch);
        Assert.Equal("\"41\"", requests[1].IfNoneMatch);
    }

    [Theory]
    [InlineData("http://palanca:8080", "http://palanca:8080/api/v1/flags")]
    [InlineData("http://palanca:8080/", "http://palanca:8080/api/v1/flags")]
    [InlineData("https://flags.example.com/palanca", "https://flags.example.com/palanca/api/v1/flags")]
    public async Task TheFlagsAddressIsUnderTheEndpoint(string endpoint, string expected)
    {
        await using var client = ClientHarness.Create(options => options.Endpoint = new Uri(endpoint));
        client.Service.Flags(1, "{}");

        await client.StartAsync();

        Assert.Equal(new Uri(expected), Assert.Single(client.Service.Requests).Uri);
    }

    [Fact]
    public async Task ItPollsEveryIntervalAndServesChanges()
    {
        await using var client = ClientHarness.Create();
        client.Service.Flags(1, """{"new-search":false}""").Flags(2, """{"new-search":true}""");
        await client.StartAsync();
        Assert.False(client.Flags.IsEnabled("new-search", defaultValue: false));

        client.Time.Advance(Poll - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50, Ct);
        Assert.Single(client.Service.Requests);

        await client.AdvanceToNextAttemptAsync(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, client.Service.Requests.Count);
        Assert.True(client.Flags.IsEnabled("new-search", defaultValue: false));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(120)]
    public async Task ThePollIntervalIsConfigurable(int seconds)
    {
        await using var client = ClientHarness.Create(options => options.PollInterval = TimeSpan.FromSeconds(seconds));
        client.Service.Flags(1, "{}");
        client.Service.Otherwise(() => new HttpResponseMessage(HttpStatusCode.NotModified));
        await client.StartAsync();

        await client.AdvanceToNextAttemptAsync(TimeSpan.FromSeconds(seconds));

        Assert.Equal(2, client.Service.Requests.Count);
    }

    [Fact]
    public async Task AnUnchangedPollKeepsTheValuesAndCountsAsASync()
    {
        await using var client = ClientHarness.Create();
        client.Service.Flags(1, """{"new-search":true}""");
        client.Service.Otherwise(() => new HttpResponseMessage(HttpStatusCode.NotModified));
        await client.StartAsync();

        for (var i = 0; i < 12; i++)
        {
            await client.AdvanceToNextAttemptAsync(Poll);
        }

        Assert.True(client.Flags.IsEnabled("new-search", defaultValue: false));
        Assert.Equal(HealthStatus.Healthy, (await client.CheckHealthAsync()).Status);
        Assert.Empty(client.LogsAtLeast(LogLevel.Warning));
    }

    public static TheoryData<string, Func<HttpResponseMessage>> Failures => new()
    {
        { "server error", () => new HttpResponseMessage(HttpStatusCode.InternalServerError) },
        { "revoked key", () => new HttpResponseMessage(HttpStatusCode.Unauthorized) },
        { "too many failures", () => new HttpResponseMessage(HttpStatusCode.TooManyRequests) },
        { "connection refused", () => throw new HttpRequestException("Connection refused (palanca:8080)") },
        { "timeout", () => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.", new TimeoutException()) },
        { "not JSON", () => FakeService.Json(2, "<html>Bad gateway</html>") },
        { "no flags property", () => FakeService.Json(2, """{"application":"hogarhero","environment":"production","revision":2}""") },
        { "no revision", () => FakeService.Json(2, """{"application":"hogarhero","environment":"production","flags":{}}""") },
        { "null body", () => FakeService.Json(2, "null") },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFailedSyncKeepsTheLastValues(string failure, Func<HttpResponseMessage> answer)
    {
        await using var client = ClientHarness.Create();
        client.Service.Flags(1, """{"new-search":true}""").Then(answer);
        await client.StartAsync();

        await client.AdvanceToNextAttemptAsync(Poll);

        Assert.True(client.Flags.IsEnabled("new-search", defaultValue: false), failure);
        var warning = Assert.Single(client.LogsAtLeast(LogLevel.Warning));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("serving the flags of revision 1 and retrying", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARevokedKeyKeepsTheLastValuesAndNamesTheStatus()
    {
        await using var client = ClientHarness.Create();
        client.Service.Flags(1, """{"new-search":true}""").Then(HttpStatusCode.Unauthorized);
        await client.StartAsync();

        await client.AdvanceToNextAttemptAsync(Poll);

        Assert.True(client.Flags.IsEnabled("new-search", defaultValue: false));
        Assert.Equal(
            "Could not sync flags from Palanca (Palanca answered 401 Unauthorized); serving the flags of revision 1 and retrying, without logging again until a sync succeeds",
            Assert.Single(client.LogsAtLeast(LogLevel.Warning)).Message);
    }

    [Theory]
    [InlineData("headers")]
    [InlineData("body")]
    public async Task ASyncWithoutACompleteAnswerInTimeFailsAndKeepsTheValues(string stalled)
    {
        await using var client = ClientHarness.Create(requestTimeout: TimeSpan.FromMilliseconds(200));
        client.Service.Flags(1, """{"new-search":true}""");
        _ = stalled == "headers" ? client.Service.ThenHang() : client.Service.ThenStallTheBody();
        await client.StartAsync();

        await client.AdvanceToNextAttemptAsync(Poll);

        Assert.True(client.Flags.IsEnabled("new-search", defaultValue: false));
        Assert.Equal(
            "Could not sync flags from Palanca (no complete answer within 0.2 seconds); serving the flags of revision 1 and retrying, without logging again until a sync succeeds",
            Assert.Single(client.LogsAtLeast(LogLevel.Warning)).Message);
    }

    [Fact]
    public async Task HandlersAddedToEveryClientAreLeftOutOfThisOne()
    {
        var service = new FakeService().Flags(1, "{}");
        service.Otherwise(() => new HttpResponseMessage(HttpStatusCode.OK));
        var handled = 0;
        var services = new ServiceCollection().AddLogging().AddPalanca(options =>
        {
            options.Endpoint = new Uri("http://palanca:8080");
            options.SdkKey = ClientHarness.SdkKey;
            options.Application = "hogarhero";
            options.Environment = "production";
        });
        services.ConfigureHttpClientDefaults(http => http
            .ConfigurePrimaryHttpMessageHandler(() => service)
            .AddHttpMessageHandler(() => new CountingHandler(() => Interlocked.Increment(ref handled))));
        await using var provider = services.BuildServiceProvider();
        var sync = provider.GetServices<IHostedService>().OfType<PalancaSync>().Single();

        await sync.StartAsync(Ct);
        await ClientHarness.WaitUntilAsync(() => sync.Attempts > 0);
        // Another client still gets the shared handler, which shows the count would see the client's request.
        using var other = provider.GetRequiredService<IHttpClientFactory>().CreateClient("other");
        using var response = await other.GetAsync(new Uri("http://palanca:8080/"), Ct);
        await sync.StopAsync(Ct);

        Assert.Equal(2, service.Requests.Count);
        Assert.Equal(1, handled);
    }

    private sealed class CountingHandler(Action count) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            count();
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(50, 30)]
    public void RetriesBackOffUpToThePollInterval(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), PalancaSync.Backoff(failures, Poll));

    [Fact]
    public async Task RetriesFollowTheBackoffAndReturnToThePollInterval()
    {
        await using var client = ClientHarness.Create(options => options.StartupWait = TimeSpan.Zero);
        client.Service.Then(HttpStatusCode.ServiceUnavailable).Then(HttpStatusCode.ServiceUnavailable).Flags(1, "{}").Flags(2, "{}");
        await client.StartAsync();

        await client.AdvanceToNextAttemptAsync(TimeSpan.FromSeconds(1));
        await client.AdvanceToNextAttemptAsync(TimeSpan.FromSeconds(2));
        client.Time.Advance(Poll - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50, Ct);
        Assert.Equal(3, client.Service.Requests.Count);
        await client.AdvanceToNextAttemptAsync(TimeSpan.FromMilliseconds(1));

        Assert.Equal(4, client.Service.Requests.Count);
    }

    [Fact]
    public async Task AnOutageLogsOneWarningAndItsEndOneMessage()
    {
        await using var client = ClientHarness.Create();
        client.Service.Flags(1, "{}");
        for (var i = 0; i < 5; i++)
        {
            client.Service.Then(HttpStatusCode.BadGateway);
        }

        client.Service.Flags(2, "{}").Then(HttpStatusCode.BadGateway);
        await client.StartAsync();

        await client.AdvanceToNextAttemptAsync(Poll);
        foreach (var seconds in new[] { 1, 2, 4, 8, 16 })
        {
            await client.AdvanceToNextAttemptAsync(TimeSpan.FromSeconds(seconds));
        }

        var outage = client.Logs.GetSnapshot().Where(record => record.Level >= LogLevel.Information).Select(record => record.Message).ToArray();
        Assert.Equal(
            [
                "Serving Palanca's hogarhero/production flags at revision 1 (0 flags)",
                "Could not sync flags from Palanca (Palanca answered 502 Bad Gateway); serving the flags of revision 1 and retrying, without logging again until a sync succeeds",
                "Synced flags from Palanca again after 5 failed attempts",
                "Serving Palanca's hogarhero/production flags at revision 2 (0 flags)",
            ],
            outage);

        await client.AdvanceToNextAttemptAsync(Poll);
        Assert.Equal(2, client.LogsAtLeast(LogLevel.Warning).Count);
    }

    [Theory]
    [InlineData("hogarhero", "staging")]
    [InlineData("billing", "production")]
    public async Task FlagsForAnotherEnvironmentOrApplicationAreRejected(string application, string environment)
    {
        await using var client = ClientHarness.Create();
        client.Service
            .Flags(1, """{"new-search":true}""")
            .Flags(2, """{"new-search":false}""", application, environment)
            .Flags(3, """{"new-search":false}""", application, environment);
        await client.StartAsync();

        await client.AdvanceToNextAttemptAsync(Poll);
        await client.AdvanceToNextAttemptAsync(TimeSpan.FromSeconds(1));

        Assert.True(client.Flags.IsEnabled("new-search", defaultValue: false));
        var error = Assert.Single(client.LogsAtLeast(LogLevel.Warning));
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Equal(
            $"Palanca sent the flags of {application}/{environment}, but this client expects hogarhero/production: check that its SDK key belongs to that environment. Serving the flags of revision 1",
            error.Message);
    }

    [Fact]
    public async Task AClientGivenAnotherEnvironmentsKeyServesCodeDefaults()
    {
        await using var client = ClientHarness.Create();
        client.Service.Otherwise(() => FakeService.Json(1, """{"application":"hogarhero","environment":"staging","revision":1,"flags":{"new-search":true}}"""));

        var start = client.Sync.StartAsync(Ct);
        await ClientHarness.WaitUntilAsync(() => client.Sync.Attempts > 0);
        client.Time.Advance(TimeSpan.FromSeconds(2));
        await start;

        Assert.False(client.Flags.IsEnabled("new-search", defaultValue: false));
        Assert.Contains(client.LogsAtLeast(LogLevel.Error), error => error.Message.EndsWith("Serving code defaults", StringComparison.Ordinal));
        var health = await client.CheckHealthAsync();
        Assert.Equal(HealthStatus.Degraded, health.Status);
        Assert.Contains("Palanca sent the flags of hogarhero/staging, but this client expects hogarhero/production", health.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecoveredMismatchIsLoggedAgainIfItReturns()
    {
        await using var client = ClientHarness.Create();
        client.Service
            .Flags(1, "{}")
            .Flags(2, "{}", environment: "staging")
            .Flags(3, "{}")
            .Flags(4, "{}", environment: "staging");
        await client.StartAsync();

        await client.AdvanceToNextAttemptAsync(Poll);
        await client.AdvanceToNextAttemptAsync(TimeSpan.FromSeconds(1));
        await client.AdvanceToNextAttemptAsync(Poll);

        Assert.Equal(2, client.LogsAtLeast(LogLevel.Error).Count);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
