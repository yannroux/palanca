using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Palanca.Data;

namespace Palanca.Commands;

/// <summary>
/// <c>dotnet Palanca.dll backup</c>, <c>backup verify &lt;file&gt;</c>, and <c>restore &lt;file&gt;</c> (FLG-061), run in the
/// service container. A timer on the host runs <c>backup</c> daily. <c>restore</c> runs while the service is stopped,
/// in a container of its own on the same volume.
/// </summary>
internal static class BackupCommand
{
    public const string Usage = """
        Usage:
          backup                 Copy the database to the backup directory, check the copy, and delete daily copies past the retention.
          backup verify <file>   Check a copy's checksum and integrity, and count the rows in each table.
          restore <file>         Replace the database with a checked copy. Stop the service first.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!IsKnown(args))
        {
            await error.WriteLineAsync(Usage);
            return 2;
        }

        // The same configuration sources as the service, without starting a server.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddPalancaData();
        await using var app = builder.Build();
        return await RunAsync(app.Services, args, output, error, cancellationToken);
    }

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!IsKnown(args))
        {
            await error.WriteLineAsync(Usage);
            return 2;
        }

        await using var scope = services.CreateAsyncScope();
        var backups = ActivatorUtilities.CreateInstance<DatabaseBackups>(scope.ServiceProvider);
        try
        {
            switch (args)
            {
                case ["backup", "verify", var file]:
                    await WriteContentsAsync(output, file, await DatabaseBackups.VerifyAsync(file, cancellationToken));
                    break;

                case ["restore", var file]:
                    var (contents, replaced) = await backups.RestoreAsync(file, cancellationToken);
                    await WriteContentsAsync(output, file, contents);
                    var database = scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
                    await output.WriteLineAsync($"Restored {file} to {database.FilePath}.");
                    if (replaced is not null)
                    {
                        await output.WriteLineAsync($"The database it replaced is now {replaced}.");
                    }

                    break;

                default:
                    var (path, deleted) = await backups.BackUpAsync(cancellationToken);
                    var days = scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.BackupRetentionDays;
                    await output.WriteLineAsync($"Backed up the database to {path}.");
                    await output.WriteLineAsync($"Deleted {deleted} daily {(deleted == 1 ? "copy" : "copies")} older than {days} days.");
                    break;
            }

            return 0;
        }
        catch (Exception exception) when (exception is BackupException or SqliteException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static bool IsKnown(string[] args) => args is ["backup"] or ["backup", "verify", _] or ["restore", _];

    private static async Task WriteContentsAsync(TextWriter output, string file, BackupContents contents)
    {
        await output.WriteLineAsync(contents.HasChecksum
            ? $"{file}: checksum and integrity check passed."
            : $"{file}: integrity check passed. It has no checksum file to compare with.");
        await output.WriteLineAsync($"Latest migration: {contents.LatestMigration ?? "none"}");
        var width = contents.Tables.Count == 0 ? 0 : contents.Tables.Max(table => table.Name.Length);
        foreach (var (name, rows) in contents.Tables)
        {
            await output.WriteLineAsync($"  {name.PadRight(width)}  {rows}");
        }
    }
}
