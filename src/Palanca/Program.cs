using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Palanca;
using Palanca.Applications;
using Palanca.Commands;
using Palanca.Components;
using Palanca.Data;
using Palanca.Evaluation;
using Palanca.Flags;
using Palanca.History;
using Palanca.Management;
using Palanca.ManagementTokens;
using Palanca.Owners;
using Palanca.SdkKeys;
using Palanca.Security;

if (args is ["healthcheck", ..])
{
    var livenessUri = HealthcheckCommand.LivenessUri(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"));
    return await HealthcheckCommand.RunAsync(livenessUri, CancellationToken.None);
}

if (args is ["owner", .. var ownerArgs])
{
    return await OwnerCommand.RunAsync(ownerArgs, Console.Out, Console.Error, CancellationToken.None);
}

// In the container TMPDIR points into the data volume, which may predate the directory.
Directory.CreateDirectory(Path.GetTempPath());

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddPalancaData();
builder.Services.AddScoped<FlagValueWriter>();
builder.Services.AddScoped<FlagCommands>();
builder.Services.AddScoped<ApplicationCommands>();
builder.Services.AddScoped<BoardQueries>();
builder.Services.AddScoped<HistoryQueries>();
builder.Services.AddScoped<SdkKeyCommands>();
builder.Services.AddScoped<ManagementTokenCommands>();
builder.Services.AddScoped<ManagementQueries>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<NewSecretHandoff>();
builder.Services.AddClientAddress();
builder.Services.AddOwnerAuthentication();
builder.Services.AddRazorComponents();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [HealthTags.Ready]);

var app = builder.Build();

app.UseForwardedHeaders();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Liveness runs no checks: answering at all proves the process is up.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(HealthTags.Ready) }).AllowAnonymous();

app.MapStaticAssets().AllowAnonymous();
app.MapOwnerEndpoints();
app.MapEvaluationEndpoints();
app.MapManagementEndpoints();
app.MapRazorComponents<App>();

await app.RunAsync();
return 0;
