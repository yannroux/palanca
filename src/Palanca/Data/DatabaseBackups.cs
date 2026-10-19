using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Palanca.Data;

/// <summary>A backup the command cannot take, check, or restore, with a message for the operator.</summary>
internal sealed class BackupException(string message) : Exception(message);

/// <summary>What a copy of the database holds.</summary>
/// <param name="HasChecksum">Whether a checksum file was beside the copy; it matched if so.</param>
/// <param name="LatestMigration">The last migration applied to the copy, or null if it has none.</param>
/// <param name="Tables">Each table and its row count, by name.</param>
internal sealed record BackupContents(bool HasChecksum, string? LatestMigration, IReadOnlyList<(string Name, long Rows)> Tables);

/// <summary>
/// The daily backup and its restore (FLG-061). A backup is a <c>VACUUM INTO</c> copy of the live database, which
/// passes <c>PRAGMA integrity_check</c> before it takes its final name, with its SHA-256 checksum beside it in
/// <c>sha256sum</c> format. Daily copies older than the retention are deleted; copies taken before a migration are kept.
/// </summary>
internal sealed partial class DatabaseBackups(PalancaDbContext db, IOptions<DatabaseOptions> options, TimeProvider timeProvider)
{
    public const string ChecksumExtension = ".sha256";

    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>Copies the live database into the backup directory, then deletes daily copies past the retention.</summary>
    public async Task<(string Path, int Deleted)> BackUpAsync(CancellationToken cancellationToken)
    {
        var database = options.Value;

        // SQLite would create an empty database rather than fail.
        if (!IsDatabaseFile(database.FilePath))
        {
            throw new BackupException($"There is no database at {database.FilePath}.");
        }

        Directory.CreateDirectory(database.BackupDirectory);
        var now = timeProvider.GetUtcNow();
        var name = $"palanca-{now.ToString(TimestampFormat, CultureInfo.InvariantCulture)}.db";
        var target = Path.Combine(database.BackupDirectory, name);
        if (File.Exists(target))
        {
            throw new BackupException($"{target} already exists.");
        }

        // The copy takes its final name last, once it is checked and its checksum is written, so a failed run never
        // leaves a backup to restore, nor one without its checksum.
        var partial = target + ".partial";
        File.Delete(partial);
        try
        {
            // VACUUM INTO writes a consistent copy of a live database, WAL contents included.
            await db.Database.ExecuteSqlAsync($"VACUUM INTO {partial}", cancellationToken);
            RestrictToOwner(partial);
            await CheckIntegrityAsync(partial, cancellationToken);
            await File.WriteAllTextAsync(target + ChecksumExtension, $"{await HashAsync(partial, cancellationToken)}  {name}\n", cancellationToken);
            RestrictToOwner(target + ChecksumExtension);
            File.Move(partial, target);
        }
        finally
        {
            File.Delete(partial);
        }

        return (target, DeleteExpired(database.BackupDirectory, now - TimeSpan.FromDays(database.BackupRetentionDays)));
    }

    /// <summary>Checks a copy's checksum, when one is beside it, and its integrity, then counts what it holds.</summary>
    public static async Task<BackupContents> VerifyAsync(string path, CancellationToken cancellationToken)
    {
        if (!IsDatabaseFile(path))
        {
            throw new BackupException($"There is no database at {path}.");
        }

        var checksumPath = path + ChecksumExtension;
        var hasChecksum = File.Exists(checksumPath);
        if (hasChecksum)
        {
            var expected = (await File.ReadAllTextAsync(checksumPath, cancellationToken)).Split(' ', 2)[0].Trim();
            if (!string.Equals(expected, await HashAsync(path, cancellationToken), StringComparison.OrdinalIgnoreCase))
            {
                throw new BackupException($"{path} does not match its checksum in {checksumPath}.");
            }
        }

        await CheckIntegrityAsync(path, cancellationToken);
        await using var connection = OpenReadOnly(path);
        await connection.OpenAsync(cancellationToken);
        var tables = new List<(string, long)>();
        foreach (var table in await QueryAsync<string>(connection,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite!_%' ESCAPE '!' AND name NOT LIKE '!_!_EF%' ESCAPE '!' ORDER BY name;",
            cancellationToken))
        {
            tables.Add((table, (await QueryAsync<long>(connection, $"SELECT count(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\";", cancellationToken)).Single()));
        }

        var hasHistory = (await QueryAsync<long>(connection, "SELECT count(*) FROM sqlite_master WHERE name = '__EFMigrationsHistory';", cancellationToken)).Single() > 0;
        var latest = hasHistory
            ? (await QueryAsync<string>(connection, "SELECT max(MigrationId) FROM __EFMigrationsHistory;", cancellationToken)).SingleOrDefault()
            : null;
        return new BackupContents(hasChecksum, latest, tables);
    }

    /// <summary>
    /// Replaces the database with a checked copy. The service must be stopped first, because it holds the database
    /// open. The database it replaces is renamed aside with its WAL files, never deleted.
    /// </summary>
    /// <returns>What the copy holds, and where the replaced database went, if there was one.</returns>
    public async Task<(BackupContents Contents, string? ReplacedPath)> RestoreAsync(string source, CancellationToken cancellationToken)
    {
        var contents = await VerifyAsync(source, cancellationToken);
        if (contents.LatestMigration is null)
        {
            throw new BackupException($"{source} is not a Palanca database: it has no migration history.");
        }

        // An older copy is fine: the service migrates it at startup, after copying it aside.
        if (!db.Database.GetMigrations().Contains(contents.LatestMigration))
        {
            throw new BackupException($"{source} comes from a newer version of Palanca ({contents.LatestMigration}). Restore it with that version.");
        }

        var database = options.Value;
        Directory.CreateDirectory(Path.GetDirectoryName(database.FilePath)!);
        SqliteConnection.ClearAllPools();
        string? replaced = null;
        if (File.Exists(database.FilePath))
        {
            var timestamp = timeProvider.GetUtcNow().ToString(TimestampFormat, CultureInfo.InvariantCulture);
            replaced = Path.Combine(Path.GetDirectoryName(database.FilePath)!, $"palanca-replaced-{timestamp}.db");

            // The WAL files keep their pairing with the database file, so the replaced database still opens as it was.
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(database.FilePath + suffix))
                {
                    File.Move(database.FilePath + suffix, replaced + suffix);
                }
            }
        }

        var incoming = database.FilePath + ".restoring";
        File.Copy(source, incoming, overwrite: true);
        File.Move(incoming, database.FilePath);
        return (contents, replaced);
    }

    private static bool IsDatabaseFile(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    private static async Task CheckIntegrityAsync(string path, CancellationToken cancellationToken)
    {
        string? result;
        try
        {
            await using var connection = OpenReadOnly(path);
            await connection.OpenAsync(cancellationToken);
            result = (await QueryAsync<string>(connection, "PRAGMA integrity_check;", cancellationToken)).FirstOrDefault();
        }
        catch (SqliteException exception)
        {
            throw new BackupException($"{path} cannot be read as a database: {exception.Message}");
        }

        if (result != "ok")
        {
            throw new BackupException($"{path} failed the integrity check: {result}");
        }
    }

    private static SqliteConnection OpenReadOnly(string path) =>
        new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());

    private static async Task<List<T>> QueryAsync<T>(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetFieldValue<T>(0));
        }

        return values;
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    // Backups hold the owner's passkeys and every key's hash; only the service's own user reads them.
    private static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static int DeleteExpired(string directory, DateTimeOffset cutoff)
    {
        var deleted = 0;
        foreach (var file in Directory.GetFiles(directory, "palanca-*.db"))
        {
            if (DailyCopy().Match(Path.GetFileName(file)) is { Success: true } match
                && DateTimeOffset.ParseExact(match.Groups[1].Value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) < cutoff)
            {
                File.Delete(file);
                File.Delete(file + ChecksumExtension);
                deleted++;
            }
        }

        return deleted;
    }

    // Daily copies only: the ones taken before a migration end in "-before-<migration>.db".
    [GeneratedRegex(@"^palanca-(\d{8}T\d{6}Z)\.db$")]
    private static partial Regex DailyCopy();
}
