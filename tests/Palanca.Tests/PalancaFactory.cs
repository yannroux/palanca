using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Palanca.Data;

namespace Palanca.Tests;

/// <summary>Runs the app against its own SQLite database in a temporary directory.</summary>
public sealed class PalancaFactory : WebApplicationFactory<Program>
{
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
    }

    /// <summary>Uses a directory the caller owns, so several factories can share one database.</summary>
    public static PalancaFactory InDirectory(string directory, string environment = "Development") => new(directory, environment);

    public string DataDirectory { get; }

    public string DatabasePath => Path.Combine(DataDirectory, "palanca.db");

    public string BackupDirectory => Path.Combine(DataDirectory, "backups");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Palanca:Database:FilePath"] = DatabasePath,
            ["Palanca:Database:BackupDirectory"] = BackupDirectory,
            ["Logging:LogLevel:Default"] = "Warning",
            // Tests that expect a constraint violation would otherwise print its stack trace.
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "None",
        }));
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
