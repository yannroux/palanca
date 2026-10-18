using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Palanca.Client.Tests;

/// <summary>The SDK key never reaches logs, metrics or the health report (FLG-065, AC-011).</summary>
public sealed class SecretHygieneTests
{
    [Fact]
    public async Task TheKeyAppearsInNoLogMetricOrHealthReport()
    {
        await using var client = ClientHarness.Create(options => options.StartupWait = TimeSpan.Zero);
        using var failures = new MetricCollector<long>(client.Services.GetRequiredService<IMeterFactory>(), PalancaMetrics.MeterName, "palanca.client.sync.failures");
        client.Service
            .Then(HttpStatusCode.Unauthorized)
            .Then(() => throw new HttpRequestException("Connection refused (palanca:8080)"))
            .Then(() => FakeService.Json(1, "not json"))
            .Flags(1, """{"new-search":true}""", environment: "staging")
            .Flags(2, """{"new-search":true}""")
            .Then(HttpStatusCode.NotModified);
        await client.StartAsync();
        foreach (var seconds in new[] { 1, 2, 4, 8, 30 })
        {
            await client.AdvanceToNextAttemptAsync(TimeSpan.FromSeconds(seconds));
        }

        client.Flags.IsEnabled("unknown", defaultValue: false);
        var health = await client.CheckHealthAsync();

        var logs = client.Logs.GetSnapshot();
        Assert.True(logs.Count >= 6);
        Assert.DoesNotContain(logs.SelectMany(Texts), text => text.Contains(ClientHarness.SdkKey, StringComparison.Ordinal));
        Assert.DoesNotContain(
            failures.GetMeasurementSnapshot().SelectMany(measurement => measurement.Tags.Values),
            value => value?.ToString()?.Contains(ClientHarness.SdkKey, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(ClientHarness.SdkKey, health.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(health.Data.Values, value => value.ToString()!.Contains(ClientHarness.SdkKey, StringComparison.Ordinal));
        Assert.DoesNotContain(ClientHarness.SdkKey, client.Services.GetRequiredService<ClientConfiguration>().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHttpClientFactoryLogsNothingAboutPolls()
    {
        await using var client = ClientHarness.Create();
        client.Service.Flags(1, "{}").Flags(1, "{}");
        await client.StartAsync();

        // A client with the factory's default logging, to show those logs would be collected if the client had them.
        using var control = client.Services.GetRequiredService<IHttpClientFactory>().CreateClient("control");
        using var response = await control.GetAsync(new Uri("http://palanca:8080/api/v1/flags"), TestContext.Current.CancellationToken);

        var factoryCategories = client.Logs.GetSnapshot().Select(record => record.Category).Where(category => category?.StartsWith("System.Net.Http.HttpClient.", StringComparison.Ordinal) == true).ToArray();
        Assert.NotEmpty(factoryCategories);
        Assert.All(factoryCategories, category => Assert.Contains(".control.", category, StringComparison.Ordinal));
    }

    private static IEnumerable<string> Texts(FakeLogRecord record) =>
        [record.Message, record.Exception?.ToString() ?? "", .. (record.StructuredState ?? []).Select(pair => $"{pair.Key}={pair.Value}")];
}
