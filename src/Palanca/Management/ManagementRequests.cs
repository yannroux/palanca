using System.Text.Json;
using System.Text.Json.Serialization;

namespace Palanca.Management;

// A missing required property fails deserialization, so it gets 400 rather than a default such as enabled: false.
// A present but null or invalid key or description is refused by the same rules as the dashboard's.

/// <param name="Values">Environments to turn the flag on or off in, by key; any environment left out starts off.</param>
internal sealed record CreateFlagRequest(
    [property: JsonRequired] string Key, [property: JsonRequired] string Description, Dictionary<string, bool>? Values = null);

internal sealed record EditFlagRequest([property: JsonRequired] string Description);

internal sealed record SetValueRequest([property: JsonRequired] bool Enabled);

/// <param name="Revision">The environment's revision after the request, which the evaluation API serves as its ETag.</param>
internal sealed record ValueResponse(string Application, string Environment, string Flag, bool Enabled, long Revision);

internal static class ManagementRequests
{
    /// <summary>
    /// Reads a request's JSON body. Endpoints call this rather than binding the body as a parameter, because binding
    /// runs before the token check: a malformed body would then get 400 before a missing token gets 401. Every body
    /// error is problem details too.
    /// </summary>
    public static async Task<(T? Body, IResult? Problem)> ReadAsync<T>(HttpContext context)
        where T : class
    {
        if (!context.Request.HasJsonContentType())
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Send the body as JSON, with Content-Type: application/json."));
        }

        try
        {
            return await context.Request.ReadFromJsonAsync<T>(context.RequestAborted) is { } body
                ? (body, null)
                : (null, ManagementProblems.Invalid("Send the body as a JSON object."));
        }
        catch (JsonException)
        {
            return (null, ManagementProblems.Invalid("The body is not valid JSON for this request, or lacks a required property."));
        }
    }
}
