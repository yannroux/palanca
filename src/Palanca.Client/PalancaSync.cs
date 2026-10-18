using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Palanca.Client;

/// <summary>The evaluation API's answer; every property must be present for the client to use it.</summary>
internal sealed record FlagsDocument(string? Application, string? Environment, long? Revision, Dictionary<string, bool>? Flags);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(FlagsDocument))]
internal sealed partial class ClientJsonContext : JsonSerializerContext;

/// <summary>
/// Polls <c>GET /api/v1/flags</c> and swaps in each new snapshot (FLG-051 to FLG-053). Failures keep the snapshot
/// already served and are logged once per outage. Nothing escapes the loop, logs included, because an exception
/// leaving a hosted service stops the host.
/// </summary>
internal sealed partial class PalancaSync(
    ClientConfiguration configuration,
    ClientState state,
    ClientMetrics metrics,
    IHttpClientFactory httpClientFactory,
    TimeProvider time,
    ILoggerFactory loggerFactory) : BackgroundService
{
    public const string HttpClientName = "Palanca.Client";
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger = new SafeLogger(loggerFactory.CreateLogger(PalancaMetrics.MeterName));
    private readonly TaskCompletionSource _firstSnapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _attempts;
    private int _failures;
    private Outage _logged;

    private enum Outage
    {
        None,
        Unavailable,
        Mismatch,
    }

    /// <summary>Finished sync attempts, for tests that advance a fake clock between polls.</summary>
    internal int Attempts => Volatile.Read(ref _attempts);

    /// <summary>
    /// Starts polling, then waits until the first snapshot arrives or the startup wait ends (FLG-051). Hosted
    /// services start before an ASP.NET Core app takes requests, so its first requests already see the flags.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var adjustment in configuration.Adjustments)
        {
            LogAdjusted(_logger, adjustment);
        }

        if (configuration.Settings is not { } settings)
        {
            LogNotConfigured(_logger, string.Join("; ", configuration.Problems));
            return;
        }

        await base.StartAsync(cancellationToken);
        if (settings.StartupWait <= TimeSpan.Zero)
        {
            return;
        }

        using var waitEnded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waited = Task.Delay(settings.StartupWait, time, waitEnded.Token);
        if (await Task.WhenAny(_firstSnapshot.Task, waited) != _firstSnapshot.Task && !cancellationToken.IsCancellationRequested)
        {
            LogStartingWithDefaults(_logger, settings.StartupWait);
        }

        await waitEnded.CancelAsync();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = configuration.Settings!;
        while (!stoppingToken.IsCancellationRequested)
        {
            bool succeeded;
            try
            {
                succeeded = await SyncAsync(settings, stoppingToken);
            }
            catch (Exception)
            {
                // Such as a metrics listener that throws: the snapshot stays and the loop goes on.
                succeeded = false;
            }

            // Scheduled before the attempt is counted, so a test clock advanced on seeing the count also fires it.
            var next = Task.Delay(succeeded ? settings.PollInterval : Backoff(_failures, settings.PollInterval), time, stoppingToken);
            Interlocked.Increment(ref _attempts);
            try
            {
                await next;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>1, 2, 4, 8… seconds after consecutive failures, never longer than the poll interval.</summary>
    internal static TimeSpan Backoff(int failures, TimeSpan pollInterval)
    {
        var delay = FirstRetry * Math.Pow(2, Math.Clamp(failures - 1, 0, 16));
        return delay < pollInterval ? delay : pollInterval;
    }

    private async Task<bool> SyncAsync(ClientSettings settings, CancellationToken stoppingToken)
    {
        var current = state.Current;

        // One deadline for the whole sync: a response whose body stalls would otherwise hold the loop forever.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(settings.RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, settings.FlagsAddress);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.SdkKey);
            if (current.Snapshot?.ETag is { } etag)
            {
                request.Headers.IfNoneMatch.Add(etag);
            }

            using var http = httpClientFactory.CreateClient(HttpClientName);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NotModified && current.Snapshot is { } unchanged)
            {
                return Succeeded(settings, current, unchanged);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return Failed(current, SyncFailure.Rejected, $"Palanca answered {(int)response.StatusCode} {response.ReasonPhrase}", exception: null);
            }

            var document = await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.FlagsDocument, deadline.Token);
            if (document is not { Application: { } application, Environment: { } environment, Revision: { } revision, Flags: { } flags })
            {
                return Failed(current, SyncFailure.Invalid, "Palanca's answer is not a flags document", exception: null);
            }

            // A staging key deployed to production answers with staging's flags: keep what is served instead.
            if (!string.Equals(application, settings.Application, StringComparison.Ordinal)
                || !string.Equals(environment, settings.Environment, StringComparison.Ordinal))
            {
                return Mismatched(settings, current, application, environment);
            }

            return Succeeded(settings, current, new FlagSnapshot(revision, flags.ToFrozenDictionary(StringComparer.Ordinal), response.Headers.ETag));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            var seconds = settings.RequestTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture);
            return Failed(current, SyncFailure.Unreachable, $"no complete answer within {seconds} seconds", exception: null);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            return Failed(current, SyncFailure.Unreachable, exception.Message, exception);
        }
        catch (Exception exception)
        {
            // Whatever else goes wrong, the loop keeps the snapshot and tries again.
            return Failed(current, SyncFailure.Invalid, "Palanca's answer could not be read", exception);
        }
    }

    private bool Succeeded(ClientSettings settings, SyncStatus current, FlagSnapshot snapshot)
    {
        var (failures, recovered) = (_failures, _logged != Outage.None);
        _failures = 0;
        _logged = Outage.None;
        state.Set(new SyncStatus(snapshot, time.GetUtcNow(), Problem: null));
        _firstSnapshot.TrySetResult();

        if (recovered)
        {
            LogRecovered(_logger, failures);
        }

        if (snapshot.Revision != current.Snapshot?.Revision)
        {
            LogServing(_logger, settings.Application, settings.Environment, snapshot.Revision, snapshot.Flags.Count);
        }

        return true;
    }

    private bool Failed(SyncStatus current, SyncFailure failure, string problem, Exception? exception)
    {
        Record(current, failure, problem);
        if (_logged == Outage.None)
        {
            LogUnavailable(_logger, exception, problem, Serving(current));
            _logged = Outage.Unavailable;
        }

        return false;
    }

    private bool Mismatched(ClientSettings settings, SyncStatus current, string application, string environment)
    {
        var problem = $"Palanca sent the flags of {application}/{environment}, but this client expects {settings.Application}/{settings.Environment}";
        Record(current, SyncFailure.Mismatch, problem);
        if (_logged != Outage.Mismatch)
        {
            LogMismatch(_logger, application, environment, settings.Application, settings.Environment, Serving(current));
            _logged = Outage.Mismatch;
        }

        return false;
    }

    private void Record(SyncStatus current, SyncFailure failure, string problem)
    {
        _failures++;
        metrics.RecordFailure(failure);
        state.Set(current with { Problem = problem });
    }

    private static string Serving(SyncStatus current) =>
        current.Snapshot is { } snapshot ? $"the flags of revision {snapshot.Revision}" : "code defaults";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Palanca option adjusted: {Adjustment}")]
    private static partial void LogAdjusted(ILogger logger, string adjustment);

    [LoggerMessage(Level = LogLevel.Error, Message = "Palanca is not configured, so every flag reads its code default: {Problems}")]
    private static partial void LogNotConfigured(ILogger logger, string problems);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No flags from Palanca within the startup wait of {StartupWait}; starting with code defaults until they arrive")]
    private static partial void LogStartingWithDefaults(ILogger logger, TimeSpan startupWait);

    [LoggerMessage(Level = LogLevel.Information, Message = "Serving Palanca's {Application}/{Environment} flags at revision {Revision} ({Count} flags)")]
    private static partial void LogServing(ILogger logger, string application, string environment, long revision, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not sync flags from Palanca ({Problem}); serving {Serving} and retrying, without logging again until a sync succeeds")]
    private static partial void LogUnavailable(ILogger logger, Exception? exception, string problem, string serving);

    [LoggerMessage(Level = LogLevel.Error, Message = "Palanca sent the flags of {Application}/{Environment}, but this client expects {ExpectedApplication}/{ExpectedEnvironment}: check that its SDK key belongs to that environment. Serving {Serving}")]
    private static partial void LogMismatch(ILogger logger, string application, string environment, string expectedApplication, string expectedEnvironment, string serving);

    [LoggerMessage(Level = LogLevel.Information, Message = "Synced flags from Palanca again after {Failures} failed attempts")]
    private static partial void LogRecovered(ILogger logger, int failures);
}
