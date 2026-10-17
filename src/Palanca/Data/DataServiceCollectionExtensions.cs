using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Palanca.Data;

internal static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddPalancaData(this IServiceCollection services)
    {
        services.AddOptions<DatabaseOptions>()
            .BindConfiguration(DatabaseOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart()
            .PostConfigure<IHostEnvironment>((options, environment) =>
            {
                options.FilePath = Path.GetFullPath(options.FilePath, environment.ContentRootPath);
                options.BackupDirectory = Path.GetFullPath(options.BackupDirectory, environment.ContentRootPath);
            });

        services.AddDbContext<PalancaDbContext>((provider, options) =>
        {
            var database = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = database.FilePath }.ToString());
        });

        services.AddHostedService<DatabaseStartup>();
        return services;
    }
}
