using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Palanca.Client.Tests;

/// <summary>Sync metrics on the Palanca.Client meter (FLG-056).</summary>
public sealed class MetricsTests
{
    [Fact]
    public async Task BeforeTheFirstSyncTheGaugesHaveNoValue()
    {
        await using var client = ClientHarness.Create();
        using var lastSuccess = Collect<double>(client, "palanca.client.sync.last_success");
        using var revision = Collect<long>(client, "palanca.client.snapshot.revision");

        lastSuccess.RecordObservableInstruments();
        revision.RecordObservableInstruments();

        Assert.Empty(lastSuccess.GetMeasurementSnapshot());
        Assert.Empty(revision.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task TheGaugesReportTheLastSuccessAndTheRevisionServed()
    {
        await using var client = ClientHarness.Create();
        using var lastSuccess = Collect<double>(client, "palanca.client.sync.last_success");
        using var revision = Collect<long>(client, "palanca.client.snapshot.revision");
        client.Service.Flags(7, "{}").Then(HttpStatusCode.NotModified).Then(HttpStatusCode.BadGateway);
        await client.StartAsync();
        await client.AdvanceToNextAttemptAsync(TimeSpan.FromSeconds(30));
        await client.AdvanceToNextAttemptAsync(TimeSpan.FromSeconds(30));

        lastSuccess.RecordObservableInstruments();
        revision.RecordObservableInstruments();

        // The 304 counts as a success; the failure after it does not.
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 12, 0, 30, TimeSpan.Zero).ToUnixTimeSeconds(), lastSuccess.LastMeasurement?.Value);
        Assert.Equal("s", lastSuccess.Instrument?.Unit);
        Assert.Equal(7, revision.LastMeasurement?.Value);
    }

    [Fact]
    public async Task EachFailureIsCountedWithItsReason()
    {
        await using var client = ClientHarness.Create(options => options.StartupWait = TimeSpan.Zero);
        using var failures = Collect<long>(client, "palanca.client.sync.failures");
        client.Service
            .Then(HttpStatusCode.Unauthorized)
            .Then(() => throw new HttpRequestException("Connection refused (palanca:8080)"))
            .Then(() => FakeService.Json(1, "not json"))
            .Flags(1, "{}", environment: "staging")
            .Flags(1, "{}");
        await client.StartAsync();
        foreach (var seconds in new[] { 1, 2, 4, 8 })
        {
            await client.AdvanceToNextAttemptAsync(TimeSpan.FromSeconds(seconds));
        }

        Assert.Equal(
            ["rejected", "unreachable", "invalid", "mismatch"],
            failures.GetMeasurementSnapshot().Select(measurement => Assert.IsType<string>(measurement.Tags["reason"])));
        Assert.All(failures.GetMeasurementSnapshot(), measurement => Assert.Equal(1, measurement.Value));
    }

    [Fact]
    public async Task AMetricsListenerThatThrowsDoesNotStopPolling()
    {
        await using var client = ClientHarness.Create(options => options.StartupWait = TimeSpan.Zero);
        // Only this client's meter: a listener sees every meter in the process, including other tests' clients.
        var meterFactory = client.Services.GetRequiredService<IMeterFactory>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PalancaMetrics.MeterName && ReferenceEquals(instrument.Meter.Scope, meterFactory))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("The exporter is down."));
        listener.Start();
        client.Service.Then(HttpStatusCode.BadGateway).Then(HttpStatusCode.BadGateway).Flags(1, """{"new-search":true}""");
        await client.StartAsync();

        await client.AdvanceToNextAttemptAsync(TimeSpan.FromSeconds(1));
        await client.AdvanceToNextAttemptAsync(TimeSpan.FromSeconds(2));

        Assert.False(client.Sync.ExecuteTask!.IsCompleted);
        Assert.Equal(3, client.Service.Requests.Count);
        Assert.True(client.Flags.IsEnabled("new-search", defaultValue: false));
    }

    private static MetricCollector<T> Collect<T>(ClientHarness client, string instrument)
        where T : struct =>
        new(client.Services.GetRequiredService<IMeterFactory>(), PalancaMetrics.MeterName, instrument);
}
