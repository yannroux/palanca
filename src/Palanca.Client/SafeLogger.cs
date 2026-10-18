using Microsoft.Extensions.Logging;

namespace Palanca.Client;

/// <summary>
/// Passes the client's logs on, and drops them if a log provider throws: a log sink that fails must not fail a read,
/// stop the poll loop, or stop the host from starting.
/// </summary>
internal sealed class SafeLogger(ILogger inner) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        try
        {
            return inner.BeginScope(state);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        try
        {
            return inner.IsEnabled(logLevel);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
        catch (Exception)
        {
            // The log is lost; the client goes on.
        }
    }
}
