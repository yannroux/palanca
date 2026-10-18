using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Palanca.Client.Tests;

/// <summary>
/// A client wired as an application would wire it, against a scripted service and a fake clock. Each poll is driven
/// by advancing the clock, then waiting for the attempt to finish.
/// </summary>
internal sealed class ClientHarness : IAsyncDisposable
{
    public const string SdkKey = "palanca_sdk_Bm9zZWNyZXQtdGhpcy1pcy1hLXRlc3Qta2V5LXZhbHVl";

    private readonly ServiceProvider _provider;

    private ClientHarness(Action<PalancaOptions>? configure, TimeSpan? requestTimeout)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddFakeLogging());
        services.AddSingleton<TimeProvider>(Time);
        services.AddPalanca(options =>
        {
            options.Endpoint = new Uri("http://palanca:8080");
            options.SdkKey = SdkKey;
            options.Application = "hogarhero";
            options.Environment = "production";
            configure?.Invoke(options);
        });
        if (requestTimeout is { } timeout)
        {
            // Registered after AddPalanca's own, so it is the one resolved.
            services.AddSingleton(provider =>
            {
                var configuration = ClientConfiguration.Read(provider);
                return configuration with { Settings = configuration.Settings! with { RequestTimeout = timeout } };
            });
        }

        services.AddHealthChecks().AddPalanca();
        services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => Service));
        _provider = services.BuildServiceProvider();
        Flags = _provider.GetRequiredService<IPalancaFlags>();
        Sync = _provider.GetServices<IHostedService>().OfType<PalancaSync>().Single();
        Logs = _provider.GetFakeLogCollector();
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

    public FakeService Service { get; } = new();

    public IPalancaFlags Flags { get; }

    public PalancaSync Sync { get; }

    public FakeLogCollector Logs { get; }

    public IServiceProvider Services => _provider;

    public static ClientHarness Create(Action<PalancaOptions>? configure = null, TimeSpan? requestTimeout = null) => new(configure, requestTimeout);

    /// <summary>
    /// Starts the client as a host would, for a first sync that finishes without the clock moving, and waits until
    /// the next poll is scheduled.
    /// </summary>
    public async Task StartAsync()
    {
        await Sync.StartAsync(Ct);
        await WaitUntilAsync(() => Sync.Attempts > 0);
    }

    /// <summary>Moves the clock by <paramref name="by"/> and waits for the poll that it triggers.</summary>
    public async Task AdvanceToNextAttemptAsync(TimeSpan by)
    {
        var attempts = Sync.Attempts;
        Time.Advance(by);
        await WaitUntilAsync(() => Sync.Attempts > attempts);
    }

    public async Task<HealthReportEntry> CheckHealthAsync() =>
        (await _provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(Ct)).Entries["palanca"];

    public IReadOnlyList<FakeLogRecord> LogsAtLeast(LogLevel level) =>
        [.. Logs.GetSnapshot().Where(record => record.Level >= level)];

    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the client.");
            await Task.Delay(5, Ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Sync.StopAsync(CancellationToken.None);
        await _provider.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}

/// <summary>A scripted evaluation API that records what each request sent.</summary>
internal sealed class FakeService : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<CancellationToken, Task<HttpResponseMessage>>> _answers = new();
    private Func<CancellationToken, Task<HttpResponseMessage>> _fallback = _ => throw new HttpRequestException("Connection refused (palanca:8080)");

    public ConcurrentQueue<SentRequest> Requests { get; } = new();

    /// <summary>Answers the next request with a 200 and these flags, tagged with the revision as the service does.</summary>
    public FakeService Flags(long revision, string flagsJson, string application = "hogarhero", string environment = "production") =>
        Then(() => Json(revision, $$"""{"application":"{{application}}","environment":"{{environment}}","revision":{{revision}},"flags":{{flagsJson}}}"""));

    public FakeService Then(Func<HttpResponseMessage> answer)
    {
        _answers.Enqueue(_ => Task.FromResult(answer()));
        return this;
    }

    public FakeService Then(HttpStatusCode status) => Then(() => new HttpResponseMessage(status));

    /// <summary>The next request gets no answer until it is cancelled.</summary>
    public FakeService ThenHang()
    {
        _answers.Enqueue(async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        });
        return this;
    }

    /// <summary>The next request gets a 200 whose body never arrives.</summary>
    public FakeService ThenStallTheBody() =>
        Then(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });

    /// <summary>Answers every request after the scripted ones this way; by default the connection is refused.</summary>
    public void Otherwise(Func<HttpResponseMessage> answer) => _fallback = _ => Task.FromResult(answer());

    public static HttpResponseMessage Json(long revision, string json)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.ETag = new EntityTagHeaderValue($"\"{revision}\"");
        return response;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(new SentRequest(
            request.Method, request.RequestUri, request.Headers.Authorization?.ToString(), request.Headers.IfNoneMatch.ToString()));
        var answer = _answers.TryDequeue(out var next) ? next : _fallback;
        return answer(cancellationToken);
    }
}

/// <summary>A body that sends nothing until the read is cancelled.</summary>
internal sealed class StalledStream : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed record SentRequest(HttpMethod Method, Uri? Uri, string? Authorization, string IfNoneMatch);
