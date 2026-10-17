using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Palanca;
using Palanca.Commands;
using Palanca.Data;
using Palanca.Flags;
using Palanca.Security;

if (args is ["healthcheck", ..])
{
    var livenessUri = HealthcheckCommand.LivenessUri(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"));
    return await HealthcheckCommand.RunAsync(livenessUri, CancellationToken.None);
}

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddPalancaData();
builder.Services.AddScoped<FlagValueWriter>();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [HealthTags.Ready]);

var app = builder.Build();

app.UseMiddleware<SecurityHeadersMiddleware>();

// Liveness runs no checks: answering at all proves the process is up.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(HealthTags.Ready) });

await app.RunAsync();
return 0;
