using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Palanca.Tests.Data;

public sealed class DatabaseStartupTests
{
    [Fact]
    public async Task NewDatabaseIsMigratedWithoutABackup()
    {
        await using var factory = new PalancaFactory();

        var pending = await factory.WithDbAsync(db => db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));

        Assert.Empty(pending);
        Assert.False(Directory.Exists(factory.BackupDirectory));
    }

    [Fact]
    public async Task DatabaseUsesWriteAheadLogging()
    {
        await using var factory = new PalancaFactory();
        _ = factory.Services;

        Assert.Equal("wal", await ScalarAsync(factory.DatabasePath, "PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task ExistingDatabaseIsBackedUpBeforeMigrating()
    {
        using var directory = new TempDirectory();
        var databasePath = Path.Combine(directory.Path, "palanca.db");
        await ExecuteAsync(databasePath, "CREATE TABLE Legacy (Value TEXT); INSERT INTO Legacy VALUES ('kept');");
        await using var factory = PalancaFactory.InDirectory(directory.Path);

        _ = factory.Services;

        var backup = Assert.Single(Directory.GetFiles(factory.BackupDirectory));
        Assert.Matches(@"^palanca-\d{8}T\d{6}Z-before-\d{14}_InitialSchema\.db$", Path.GetFileName(backup));
        Assert.Equal("kept", await ScalarAsync(backup, "SELECT Value FROM Legacy;"));
        Assert.Equal(0L, await ScalarAsync(backup, "SELECT count(*) FROM sqlite_master WHERE name = 'Applications';"));
        Assert.Equal(1L, await ScalarAsync(databasePath, "SELECT count(*) FROM sqlite_master WHERE name = 'Applications';"));
    }

    [Fact]
    public async Task UpToDateDatabaseIsNotBackedUpAgain()
    {
        using var directory = new TempDirectory();
        await using (var first = PalancaFactory.InDirectory(directory.Path))
        {
            _ = first.Services;
        }

        await using var second = PalancaFactory.InDirectory(directory.Path);
        _ = second.Services;

        Assert.False(Directory.Exists(second.BackupDirectory));
    }

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<object?> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}
