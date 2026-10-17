using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Palanca.Data;

/// <summary>Ready means the database is readable and writable.</summary>
internal sealed class DatabaseHealthCheck(PalancaDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            _ = await db.Applications.AnyAsync(cancellationToken);

            // SQLite opens a read-only file without complaint and only fails on an actual write,
            // so rewrite the migration history rows to their own values, then roll back.
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE "__EFMigrationsHistory" SET "ProductVersion" = "ProductVersion";""", cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (SqliteException exception)
        {
            return HealthCheckResult.Unhealthy("The database is not readable and writable.", exception);
        }
    }
}
