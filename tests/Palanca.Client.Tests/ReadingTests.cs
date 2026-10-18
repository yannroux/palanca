using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Palanca.Client.Tests;

/// <summary>Reads are served from memory and fall back to code defaults (FLG-050, FLG-054).</summary>
public sealed class ReadingTests
{
    private static readonly PalancaFlag NewSearch = new("new-search", DefaultValue: true);

    [Fact]
    public async Task BeforeTheFirstSyncEveryFlagReadsItsDefaultWithoutWarning()
    {
        await using var client = ClientHarness.Create();

        Assert.True(client.Flags.IsEnabled("new-search", defaultValue: true));
        Assert.False(client.Flags.IsEnabled("new-search", defaultValue: false));
        Assert.True(client.Flags.IsEnabled(NewSearch));
        Assert.Empty(client.Service.Requests);
        Assert.Empty(client.LogsAtLeast(LogLevel.Warning));
    }

    [Fact]
    public async Task ReadsServeTheServicesValues()
    {
        await using var client = ClientHarness.Create();
        client.Service.Flags(1, """{"new-search":false,"operator-denue-imports":true}""");

        await client.StartAsync();

        Assert.False(client.Flags.IsEnabled("new-search", defaultValue: true));
        Assert.False(client.Flags.IsEnabled(NewSearch));
        Assert.True(client.Flags.IsEnabled("operator-denue-imports", defaultValue: false));
    }

    [Fact]
    public async Task KeysAreCaseSensitive()
    {
        await using var client = ClientHarness.Create();
        client.Service.Flags(1, """{"new-search":true}""");
        await client.StartAsync();

        Assert.False(client.Flags.IsEnabled("New-Search", defaultValue: false));
    }

    [Fact]
    public async Task AnUnknownFlagReadsItsDefaultAndWarnsOncePerKey()
    {
        await using var client = ClientHarness.Create();
        client.Service.Flags(1, """{"new-search":true}""");
        await client.StartAsync();

        Assert.True(client.Flags.IsEnabled("dark-mode", defaultValue: true));
        Assert.False(client.Flags.IsEnabled("dark-mode", defaultValue: false));
        Assert.False(client.Flags.IsEnabled("beta-banner", defaultValue: false));

        var warnings = client.LogsAtLeast(LogLevel.Warning);
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, warning => Assert.Equal(PalancaMetrics.MeterName, warning.Category));
        Assert.Equal(
            "Flag dark-mode is not in Palanca's hogarhero/production flags, so it reads its default (True); create it in Palanca or remove it from the code",
            warnings[0].Message);
        Assert.Contains("beta-banner", warnings[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WarningsAboutUnknownKeysAreBounded()
    {
        await using var client = ClientHarness.Create();
        client.Service.Flags(1, "{}");
        await client.StartAsync();

        for (var i = 0; i < PalancaFlags.MaxWarnedKeys + 10; i++)
        {
            Assert.False(client.Flags.IsEnabled($"generated-{i}", defaultValue: false));
        }

        Assert.Equal(PalancaFlags.MaxWarnedKeys, client.LogsAtLeast(LogLevel.Warning).Count);
    }

    [Fact]
    public async Task ReadsNeverThrow()
    {
        await using var client = ClientHarness.Create();
        client.Service.Flags(1, """{"new-search":true}""");
        await client.StartAsync();

        Assert.True(client.Flags.IsEnabled(null!, defaultValue: true));
        Assert.False(client.Flags.IsEnabled(null!, defaultValue: false));
        Assert.False(client.Flags.IsEnabled((PalancaFlag)null!));
        Assert.False(((IPalancaFlags)null!).IsEnabled(NewSearch));
    }

    [Fact]
    public async Task AFailingLoggerDoesNotFailARead()
    {
        await using var client = ClientHarness.Create();
        client.Service.Flags(1, "{}");
        await client.StartAsync();
        var flags = new PalancaFlags(
            client.Services.GetRequiredService<ClientState>(),
            client.Services.GetRequiredService<ClientConfiguration>(),
            LoggerFactory.Create(logging => logging.AddProvider(new ThrowingLoggerProvider())));

        Assert.True(flags.IsEnabled("unknown", defaultValue: true));
    }

    private sealed class ThrowingLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ThrowingLogger();

        public void Dispose()
        {
        }

        private sealed class ThrowingLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                throw new InvalidOperationException("The log sink is down.");
        }
    }
}
