using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Palanca.Data;

namespace Palanca.Tests;

/// <summary>
/// Runs the app against its own SQLite database in a temporary directory. Clients use HTTPS, as browsers do
/// through the proxy, so the dashboard's Secure-only cookies and antiforgery checks behave as in production.
/// </summary>
public sealed class PalancaFactory : WebApplicationFactory<Program>
{
    /// <summary>The dashboard origin the app is configured with, and the origin a test authenticator reports.</summary>
    public const string Origin = "https://localhost";

    private readonly string _environment;
    private readonly TempDirectory? _ownedDirectory;

    public PalancaFactory()
        : this(directory: null, environment: "Development")
    {
    }

    private PalancaFactory(string? directory, string environment)
    {
        _environment = environment;
        if (directory is null)
        {
            _ownedDirectory = new TempDirectory();
            directory = _ownedDirectory.Path;
        }

        DataDirectory = directory;
        ClientOptions.BaseAddress = new Uri(Origin);
    }

    /// <summary>Uses a directory the caller owns, so several factories can share one database.</summary>
    public static PalancaFactory InDirectory(string directory, string environment = "Development") => new(directory, environment);

    public string DataDirectory { get; }

    public string DatabasePath => Path.Combine(DataDirectory, "palanca.db");

    public string BackupDirectory => Path.Combine(DataDirectory, "backups");

    /// <summary>Replaces the app's clock, for tests of expiry and session limits.</summary>
    public FakeTimeProvider? Time { get; init; }

    /// <summary>Settings added after the defaults, such as trusted proxies.</summary>
    public IReadOnlyDictionary<string, string?> Settings { get; init; } = new Dictionary<string, string?>();

    /// <summary>The address requests appear to come from; the test server sets none.</summary>
    public IPAddress? PeerAddress { get; init; }

    /// <summary>Receives the app's logs, for tests of what they contain.</summary>
    public ILoggerProvider? Logs { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureAppConfiguration(configuration =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Palanca:Database:FilePath"] = DatabasePath,
                ["Palanca:Database:BackupDirectory"] = BackupDirectory,
                ["Palanca:Dashboard:Origin"] = Origin,
                ["Palanca:DataProtection:KeysDirectory"] = Path.Combine(DataDirectory, "keys"),
                ["Logging:LogLevel:Default"] = "Warning",
                // Tests that expect a constraint violation would otherwise print its stack trace.
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "None",
            });
            configuration.AddInMemoryCollection(Settings);
        });

        builder.ConfigureTestServices(services =>
        {
            if (Time is not null)
            {
                services.AddSingleton<TimeProvider>(Time);
            }

            if (PeerAddress is not null)
            {
                services.AddSingleton<IStartupFilter>(new PeerAddressFilter(PeerAddress));
            }

            if (Logs is not null)
            {
                services.AddSingleton(Logs);
            }
        });
    }

    internal async Task WithDbAsync(Func<PalancaDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<PalancaDbContext>());
    }

    internal async Task<T> WithDbAsync<T>(Func<PalancaDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<PalancaDbContext>());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _ownedDirectory?.Dispose();
    }
}

/// <summary>Sets the connection's remote address before the app's own middleware runs.</summary>
internal sealed class PeerAddressFilter(IPAddress address) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = address;
            return nextMiddleware(context);
        });
        next(app);
    };
}

public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "palanca-tests", Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        // Pooled connections keep the database files open.
        SqliteConnection.ClearAllPools();
        Directory.Delete(Path, recursive: true);
    }
}
