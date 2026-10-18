using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Palanca.Data;
using Palanca.Owners;

namespace Palanca.Commands;

/// <summary>
/// <c>dotnet Palanca.dll owner enroll &lt;email&gt;</c> and <c>dotnet Palanca.dll owner recover</c>, run in the
/// service container (FLG-046). Each prints a single-use enrollment link that expires after 15 minutes.
/// The link goes to standard output only, never to the service's logs (AC-011).
/// </summary>
internal static class OwnerCommand
{
    public const string Usage = """
        Usage:
          owner enroll <email>   Create the owner account and print an enrollment link.
          owner recover          Revoke every passkey and session, then print a new enrollment link.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!IsKnown(args))
        {
            await error.WriteLineAsync(Usage);
            return 2;
        }

        // The same configuration sources as the service, ASPNETCORE_ENVIRONMENT included, without starting a server.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddPalancaData();
        builder.Services.AddOptions<DashboardOptions>().BindConfiguration(DashboardOptions.SectionName);
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
        var dashboard = scope.ServiceProvider.GetRequiredService<IOptions<DashboardOptions>>().Value;
        if (!dashboard.IsValid())
        {
            await error.WriteLineAsync("Palanca:Dashboard:Origin is not set to the dashboard's origin, so no link can be printed.");
            return 1;
        }

        var db = scope.ServiceProvider.GetRequiredService<PalancaDbContext>();
        if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
        {
            await error.WriteLineAsync("The database is not up to date. Start the service once, then run this command again.");
            return 1;
        }

        // Built here rather than registered, so no web request can ever resolve it (FLG-046).
        var commands = ActivatorUtilities.CreateInstance<OwnerHostCommands>(scope.ServiceProvider);
        var result = args is ["enroll", var email]
            ? await commands.EnrollAsync(email, cancellationToken)
            : await commands.RecoverAsync(cancellationToken);
        if (result.Error is not null)
        {
            await error.WriteLineAsync(result.Error);
            return 1;
        }

        var lifetime = (int)OwnerHostCommands.EnrollmentLifetime.TotalMinutes;
        await output.WriteLineAsync($"Enrollment link for {result.Email}, valid for {lifetime} minutes and usable once:");
        await output.WriteLineAsync(EnrollmentLink(dashboard, result.Token!));
        return 0;
    }

    private static bool IsKnown(string[] args) => args is ["enroll", _] or ["recover"];

    // The token rides in the fragment, which browsers never send to the server or in a Referer header.
    public static string EnrollmentLink(DashboardOptions dashboard, string token) => $"{dashboard.NormalizedOrigin}/enroll#{token}";
}
