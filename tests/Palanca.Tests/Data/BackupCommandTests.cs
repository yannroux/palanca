using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Palanca.Applications;
using Palanca.Commands;
using Palanca.Data;
using Palanca.Flags;
using Palanca.ManagementTokens;
using Palanca.SdkKeys;
using Palanca.Tests.Owners;

namespace Palanca.Tests.Data;

/// <summary>The daily backup, its check, and the restore (FLG-061, AC-009).</summary>
public sealed class BackupCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 7, 40, 0, TimeSpan.Zero);

    [Fact]
    public async Task ABackupIsACheckedCopyWithItsChecksum()
    {
        await using var factory = new PalancaFactory { Time = new FakeTimeProvider(Now) };
        await SeedAsync(factory);

        var (exitCode, output, error) = await RunAsync(factory.Services, "backup");

        Assert.True(exitCode == 0, error);
        var backup = Path.Combine(factory.BackupDirectory, "palanca-20261019T074000Z.db");
        Assert.Equal($"Backed up the database to {backup}.\nDeleted 0 daily copies older than 14 days.\n", output.ReplaceLineEndings("\n"));
        Assert.Equal(["palanca-20261019T074000Z.db", "palanca-20261019T074000Z.db.sha256"], FileNames(factory.BackupDirectory));
        Assert.Equal(
            $"{Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(backup, TestContext.Current.CancellationToken)))}  palanca-20261019T074000Z.db\n",
            await File.ReadAllTextAsync(backup + ".sha256", TestContext.Current.CancellationToken));
        Assert.Equal("hogarhero", await ScalarAsync(backup, "SELECT Key FROM Applications;"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(backup));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(backup + ".sha256"));
        }
    }

    [Fact]
    public async Task DailyCopiesPastTheRetentionAreDeletedAndOthersKept()
    {
        await using var factory = new PalancaFactory { Time = new FakeTimeProvider(Now) };
        _ = factory.Services;
        Directory.CreateDirectory(factory.BackupDirectory);
        string[] kept =
        [
            "notes.txt",
            "palanca-20261001T000000Z-before-20261018174529_ManagementTokens.db",
            "palanca-20261005T074000Z.db",
            "palanca-20261005T074000Z.db.sha256",
        ];
        foreach (var name in kept.Concat(["palanca-20261004T073959Z.db", "palanca-20261004T073959Z.db.sha256"]))
        {
            await File.WriteAllTextAsync(Path.Combine(factory.BackupDirectory, name), "x", TestContext.Current.CancellationToken);
        }

        var (exitCode, output, error) = await RunAsync(factory.Services, "backup");

        Assert.True(exitCode == 0, error);
        Assert.Contains("Deleted 1 daily copy older than 14 days.", output, StringComparison.Ordinal);
        Assert.Equal(kept.Concat(["palanca-20261019T074000Z.db", "palanca-20261019T074000Z.db.sha256"]).Order(StringComparer.Ordinal), FileNames(factory.BackupDirectory));
    }

    [Fact]
    public async Task ABackupNeedsADatabase()
    {
        using var directory = new TempDirectory();
        await using var host = CommandHost(directory.Path);

        var (exitCode, _, error) = await RunAsync(host.Services, "backup");

        Assert.Equal(1, exitCode);
        Assert.Contains("There is no database at", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(directory.Path, "palanca.db")));
    }

    [Fact]
    public async Task UnknownArgumentsPrintTheUsage()
    {
        await using var factory = new PalancaFactory();

        var (exitCode, _, error) = await RunAsync(factory.Services, "backup", "now");

        Assert.Equal(2, exitCode);
        Assert.Contains("backup verify <file>", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyCountsTheRowsInEachTable()
    {
        await using var factory = new PalancaFactory();
        await SeedAsync(factory);
        var backup = await BackUpAsync(factory);

        var (exitCode, output, error) = await RunAsync(factory.Services, "backup", "verify", backup);

        Assert.True(exitCode == 0, error);
        var lines = output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal($"{backup}: checksum and integrity check passed.", lines[0]);
        Assert.Matches(@"^Latest migration: \d{14}_\w+$", lines[1]);
        var rows = lines.Skip(2).Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToDictionary(parts => parts[0], parts => long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1, rows["Applications"]);
        Assert.Equal(2, rows["Environments"]);
        Assert.Equal(1, rows["Flags"]);
        Assert.Equal(1, rows["SdkKeys"]);
        Assert.Equal(1, rows["ManagementTokens"]);
        Assert.DoesNotContain(rows.Keys, table => table.StartsWith("__EF", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifyRejectsACopyThatNoLongerMatchesItsChecksum()
    {
        await using var factory = new PalancaFactory();
        var backup = await BackUpAsync(factory);
        await File.AppendAllTextAsync(backup, "x", TestContext.Current.CancellationToken);

        var (exitCode, _, error) = await RunAsync(factory.Services, "backup", "verify", backup);

        Assert.Equal(1, exitCode);
        Assert.Contains("does not match its checksum", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyRejectsACorruptCopy()
    {
        await using var factory = new PalancaFactory();
        await SeedAsync(factory);
        var backup = await BackUpAsync(factory);
        File.Delete(backup + DatabaseBackups.ChecksumExtension);
        await using (var file = File.Open(backup, FileMode.Open, FileAccess.ReadWrite))
        {
            // Overwrites the third page, past the header and the schema, with bytes no page holds.
            var header = new byte[18];
            file.ReadExactly(header);
            var pageSize = (header[16] << 8) | header[17];
            file.Position = 2L * pageSize;
            file.Write(Enumerable.Repeat((byte)0xFF, pageSize).ToArray());
        }

        var (exitCode, _, error) = await RunAsync(factory.Services, "backup", "verify", backup);

        Assert.Equal(1, exitCode);
        Assert.Matches("failed the integrity check|cannot be read as a database", error);
    }

    [Fact]
    public async Task VerifyRejectsAFileThatIsNotADatabase()
    {
        await using var factory = new PalancaFactory();
        var file = Path.Combine(factory.DataDirectory, "notes.db");
        await File.WriteAllTextAsync(file, "Not a database, but long enough to look like a page header to nobody.", TestContext.Current.CancellationToken);

        var (exitCode, _, error) = await RunAsync(factory.Services, "backup", "verify", file);

        Assert.Equal(1, exitCode);
        Assert.Contains("cannot be read as a database", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARestoredBackupReproducesEverything()
    {
        var ct = TestContext.Current.CancellationToken;
        using var archive = new TempDirectory();
        using var authenticator = new SoftwareAuthenticator();
        string backup, sdkKey, token;
        IReadOnlyList<(string Name, long Rows)> sourceTables;
        await using (var source = new PalancaFactory())
        {
            using (var dashboard = new DashboardClient(source))
            {
                await OwnerSetup.SignInAsync(source, dashboard, authenticator);
            }

            (sdkKey, token) = await SeedAsync(source);
            sourceTables = (await DatabaseBackups.VerifyAsync(source.DatabasePath, ct)).Tables;
            backup = Archive(await BackUpAsync(source), archive.Path);
        }

        // The source and its data directory are gone, as on a scratch host.
        using var target = new TempDirectory();
        await using (var host = CommandHost(target.Path))
        {
            var (exitCode, output, error) = await RunAsync(host.Services, "restore", backup);
            Assert.True(exitCode == 0, error);
            Assert.Contains($"Restored {backup} to {Path.Combine(target.Path, "palanca.db")}.", output, StringComparison.Ordinal);
        }

        await using var restored = PalancaFactory.InDirectory(target.Path);
        using (var dashboard = new DashboardClient(restored))
        {
            using var signedIn = await dashboard.SignInAsync(authenticator);
            Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
            Assert.Equal("/", DashboardClient.RedirectPath(signedIn));
        }

        using var api = restored.CreateClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sdkKey);
        using (var flags = await api.GetAsync("/api/v1/flags", ct))
        {
            Assert.Equal(HttpStatusCode.OK, flags.StatusCode);
            Assert.Contains("\"new-search\":true", await flags.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }

        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using (var managed = await api.GetAsync("/api/manage/v1/applications/hogarhero/flags", ct))
        {
            Assert.Equal(HttpStatusCode.OK, managed.StatusCode);
        }

        Assert.Equal(OwnerSetup.Email, await restored.WithDbAsync(db => db.Owners.Select(o => o.Email).SingleAsync(ct)));

        // Every table, history and passkeys included, has the rows it had when the source was backed up.
        Assert.Contains(sourceTables, table => table is { Name: "OwnerPasskeys", Rows: 1 });
        Assert.Contains(sourceTables, table => table is { Name: "ChangeRecords", Rows: > 0 });
        Assert.Equal(sourceTables, (await DatabaseBackups.VerifyAsync(Path.Combine(target.Path, "palanca.db"), ct)).Tables);
    }

    [Fact]
    public async Task ARestoreSetsTheReplacedDatabaseAside()
    {
        using var archive = new TempDirectory();
        string backup;
        await using (var source = new PalancaFactory())
        {
            await SeedAsync(source);
            backup = Archive(await BackUpAsync(source), archive.Path);
        }

        using var target = new TempDirectory();
        await using (var before = PalancaFactory.InDirectory(target.Path))
        {
            await using var scope = before.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ApplicationCommands>()
                .CreateAsync("other", "Other", new ChangeActor(ChangeSource.Dashboard, OwnerSetup.Email), TestContext.Current.CancellationToken);
        }

        SqliteConnection.ClearAllPools();
        await using (var host = CommandHost(target.Path, new FakeTimeProvider(Now)))
        {
            var (exitCode, output, error) = await RunAsync(host.Services, "restore", backup);

            Assert.True(exitCode == 0, error);
            var replaced = Path.Combine(target.Path, "palanca-replaced-20261019T074000Z.db");
            Assert.Contains($"The database it replaced is now {replaced}.", output, StringComparison.Ordinal);
            Assert.Equal("other", await ScalarAsync(replaced, "SELECT Key FROM Applications;"));
        }

        Assert.Equal("hogarhero", await ScalarAsync(Path.Combine(target.Path, "palanca.db"), "SELECT Key FROM Applications;"));
    }

    [Fact]
    public async Task ARestoreRefusesACopyFromANewerVersion()
    {
        using var archive = new TempDirectory();
        string backup;
        await using (var source = new PalancaFactory())
        {
            backup = Archive(await BackUpAsync(source), archive.Path);
        }

        File.Delete(backup + DatabaseBackups.ChecksumExtension);
        await using (var connection = new SqliteConnection($"Data Source={backup};Pooling=False"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('99991231000000_Future', '99.0.0');";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        using var target = new TempDirectory();
        await using var host = CommandHost(target.Path);
        var (exitCode, _, error) = await RunAsync(host.Services, "restore", backup);

        Assert.Equal(1, exitCode);
        Assert.Contains("comes from a newer version of Palanca (99991231000000_Future)", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(target.Path, "palanca.db")));
    }

    [Fact]
    public async Task ARestoreRefusesADatabaseWithoutMigrationHistory()
    {
        using var archive = new TempDirectory();
        var file = Path.Combine(archive.Path, "other.db");
        await using (var connection = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Legacy (Value TEXT); INSERT INTO Legacy VALUES ('kept');";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        using var target = new TempDirectory();
        await using var host = CommandHost(target.Path);
        var (exitCode, _, error) = await RunAsync(host.Services, "restore", file);

        Assert.Equal(1, exitCode);
        Assert.Contains("is not a Palanca database", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(target.Path, "palanca.db")));
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(IServiceProvider services, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await BackupCommand.RunAsync(services, args, output, error, TestContext.Current.CancellationToken);
        return (exitCode, output.ToString(), error.ToString());
    }

    /// <summary>The command's own host, without a server, for a data directory that may hold no database yet.</summary>
    private static WebApplication CommandHost(string directory, TimeProvider? time = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Palanca:Database:FilePath"] = Path.Combine(directory, "palanca.db"),
            ["Palanca:Database:BackupDirectory"] = Path.Combine(directory, "backups"),
        });
        builder.Services.AddSingleton(time ?? TimeProvider.System);
        builder.Services.AddPalancaData();
        return builder.Build();
    }

    private static async Task<string> BackUpAsync(PalancaFactory factory)
    {
        var (exitCode, _, error) = await RunAsync(factory.Services, "backup");
        Assert.True(exitCode == 0, error);
        return Assert.Single(Directory.GetFiles(factory.BackupDirectory, "palanca-*.db"));
    }

    /// <summary>Copies a backup and its checksum out of a factory's directory, which goes with the factory.</summary>
    private static string Archive(string backup, string directory)
    {
        var archived = Path.Combine(directory, Path.GetFileName(backup));
        File.Copy(backup, archived);
        File.Copy(backup + DatabaseBackups.ChecksumExtension, archived + DatabaseBackups.ChecksumExtension);
        return archived;
    }

    private static async Task<(string SdkKey, string Token)> SeedAsync(PalancaFactory factory)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var owner = new ChangeActor(ChangeSource.Dashboard, OwnerSetup.Email);
        var applications = services.GetRequiredService<ApplicationCommands>();
        await applications.CreateAsync("hogarhero", "HogarHero", owner, ct);
        await applications.CreateEnvironmentAsync("hogarhero", "staging", "Staging", false, owner, ct);
        await applications.CreateEnvironmentAsync("hogarhero", "production", "Production", true, owner, ct);
        await services.GetRequiredService<FlagCommands>().CreateAsync("hogarhero", "new-search", "The new search page.", ["staging"], owner, ct);
        var key = (await services.GetRequiredService<SdkKeyCommands>().CreateAsync("hogarhero", "staging", "web", owner, ct)).Key!;
        var (_, created) = await services.GetRequiredService<ManagementTokenCommands>()
            .CreateAsync("agent", TokenPermission.ReadOnly, allApplications: false, ["hogarhero"], 365, owner, ct);
        return (key, created!.Token);
    }

    private static IEnumerable<string> FileNames(string directory) =>
        Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!;

    private static async Task<object?> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}
