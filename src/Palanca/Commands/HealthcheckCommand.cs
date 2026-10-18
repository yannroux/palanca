namespace Palanca.Commands;

/// <summary>
/// Probes the liveness endpoint of the server running in the same container.
/// The chiseled runtime image has no shell or curl, so the Docker health check runs
/// <c>dotnet Palanca.dll healthcheck</c> instead.
/// </summary>
internal static class HealthcheckCommand
{
    private const string DefaultPort = "8080";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>Builds the liveness URI from the value of <c>ASPNETCORE_HTTP_PORTS</c>.</summary>
    public static Uri LivenessUri(string? httpPorts)
    {
        var port = httpPorts?
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? DefaultPort;
        return new Uri($"http://127.0.0.1:{port}/health/live");
    }

    public static async Task<int> RunAsync(Uri livenessUri, CancellationToken cancellationToken)
    {
        // A redirect is not an answer from the liveness endpoint, so it must not be followed to a page that answers 200.
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout };
        return await RunAsync(client, livenessUri, cancellationToken);
    }

    /// <returns>0 when the server answers with a success status, otherwise 1.</returns>
    public static async Task<int> RunAsync(HttpClient client, Uri livenessUri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(livenessUri, cancellationToken);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
