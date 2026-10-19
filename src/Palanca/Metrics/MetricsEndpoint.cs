namespace Palanca.Metrics;

/// <summary>
/// <c>GET /metrics</c> (FLG-064): the service's counters in the Prometheus text format. Like the health endpoints it is
/// unauthenticated and meant for the private networks only; Caddy does not route it.
/// </summary>
internal static class MetricsEndpoint
{
    public const string Path = "/metrics";

    public static IServiceCollection AddServerMetrics(this IServiceCollection services)
    {
        services.AddMetrics();
        services.AddSingleton<ServerMetrics>();
        return services;
    }

    /// <summary>Counts every request. It goes first, so it sees the final status of everything after it.</summary>
    public static IApplicationBuilder UseRequestMetrics(this IApplicationBuilder app) => app.UseMiddleware<RequestMetricsMiddleware>();

    public static IEndpointRouteBuilder MapMetricsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, (HttpContext context, ServerMetrics metrics) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Text(metrics.ToPrometheusText(), "text/plain; version=0.0.4; charset=utf-8");
        }).AllowAnonymous();
        return endpoints;
    }
}

internal sealed class RequestMetricsMiddleware(RequestDelegate next, ServerMetrics metrics)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception)
        {
            // The server answers an exception that escapes the pipeline with 500.
            metrics.RecordRequest(context.Request.Path, StatusCodes.Status500InternalServerError);
            throw;
        }

        metrics.RecordRequest(context.Request.Path, context.Response.StatusCode);
    }
}
