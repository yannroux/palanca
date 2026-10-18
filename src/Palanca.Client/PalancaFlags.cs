using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Palanca.Client;

/// <summary>Serves reads from the current snapshot (FLG-050, FLG-054).</summary>
internal sealed partial class PalancaFlags(ClientState state, ClientConfiguration configuration, ILoggerFactory loggerFactory) : IPalancaFlags
{
    // Bounds the memory spent remembering warned keys, in case keys are built at run time.
    internal const int MaxWarnedKeys = 1000;

    private readonly ILogger _logger = new SafeLogger(loggerFactory.CreateLogger(PalancaMetrics.MeterName));
    private readonly ConcurrentDictionary<string, bool> _warnedKeys = new(StringComparer.Ordinal);

    // Counted apart from the dictionary, whose Count takes every lock: a hot read of a deleted flag must stay cheap.
    private int _warnedCount;

    public bool IsEnabled(string key, bool defaultValue)
    {
        try
        {
            // Before the first snapshot every key is unknown, so there is nothing yet to warn about.
            if (key is null || state.Current.Snapshot is not { } snapshot)
            {
                return defaultValue;
            }

            if (snapshot.Flags.TryGetValue(key, out var enabled))
            {
                return enabled;
            }

            if (!_warnedKeys.ContainsKey(key) && Volatile.Read(ref _warnedCount) < MaxWarnedKeys && _warnedKeys.TryAdd(key, true))
            {
                Interlocked.Increment(ref _warnedCount);
                LogUnknownFlag(_logger, key, configuration.Settings?.Application, configuration.Settings?.Environment, defaultValue);
            }

            return defaultValue;
        }
        catch (Exception)
        {
            // A read must never fail the caller.
            return defaultValue;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flag {Key} is not in Palanca's {Application}/{Environment} flags, so it reads its default ({DefaultValue}); create it in Palanca or remove it from the code")]
    private static partial void LogUnknownFlag(ILogger logger, string key, string? application, string? environment, bool defaultValue);
}
