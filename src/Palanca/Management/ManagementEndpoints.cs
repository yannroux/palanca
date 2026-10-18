using System.Globalization;
using Palanca.Data;

namespace Palanca.Management;

/// <summary>
/// The management API under <c>/api/manage/v1</c> (FLG-080): what scripts and AI agents read without the dashboard.
/// Every request needs a management token (FLG-081), and reaches only the applications in the token's scope (FLG-082).
/// </summary>
internal static class ManagementEndpoints
{
    public const string BasePath = "/api/manage/v1";

    public static IEndpointRouteBuilder MapManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // The token check stands in for the dashboard's sign-in requirement, so the session cookie authorizes nothing here.
        var api = endpoints.MapGroup(BasePath).AllowAnonymous().AddEndpointFilter<ManagementAuthentication>();
        api.MapGet("/applications", ListApplicationsAsync);
        api.MapGet("/applications/{app}/flags", ListFlagsAsync);
        api.MapGet("/applications/{app}/flags/{flag}", GetFlagAsync);
        api.MapGet("/applications/{app}/history", GetHistoryAsync);

        // Any other path or method answers after the token check, like the rest of the API, rather than with the
        // dashboard's sign-in redirect. No endpoint archives or deletes flags, or manages applications or credentials (FLG-085).
        api.Map("/{**path}", () => ManagementProblems.NotFound("The management API has no such endpoint."));
        return endpoints;
    }

    private static async Task<IResult> ListApplicationsAsync(HttpContext context, ManagementQueries queries) =>
        Results.Ok(await queries.ApplicationsAsync(context.ManagementCaller(), context.RequestAborted));

    private static async Task<IResult> ListFlagsAsync(string app, HttpContext context, ManagementQueries queries)
    {
        var (application, problem) = await ReachAsync(app, context, queries);
        return application is null
            ? problem!
            : Results.Ok(new FlagListResponse(application.Key, await queries.FlagsAsync(application, flagKey: null, context.RequestAborted)));
    }

    private static async Task<IResult> GetFlagAsync(string app, string flag, HttpContext context, ManagementQueries queries)
    {
        var (application, problem) = await ReachAsync(app, context, queries);
        if (application is null)
        {
            return problem!;
        }

        return (await queries.FlagsAsync(application, flag, context.RequestAborted)) is [var found]
            ? Results.Ok(found)
            : ManagementProblems.NotFound("The application has no flag with that key, or it is archived.");
    }

    // The cursor is parsed here, so a malformed one gets problem details like every other error.
    private static async Task<IResult> GetHistoryAsync(
        string app, string? environment, string? flag, string? before, HttpContext context, ManagementQueries queries)
    {
        var (application, problem) = await ReachAsync(app, context, queries);
        if (application is null)
        {
            return problem!;
        }

        long? cursor = null;
        if (before is not null)
        {
            if (!long.TryParse(before, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                return ManagementProblems.Invalid("Pass as before the next value of the previous page.");
            }

            cursor = id;
        }

        return Results.Ok(await queries.HistoryAsync(application, environment, flag, cursor, context.RequestAborted));
    }

    /// <summary>
    /// The application, when it exists and the token reaches it. A token scoped to chosen applications gets 403 for
    /// any other key, whether or not that application exists, so it learns nothing outside its scope.
    /// </summary>
    private static async Task<(Application? Application, IResult? Problem)> ReachAsync(string key, HttpContext context, ManagementQueries queries)
    {
        var caller = context.ManagementCaller();
        var application = await queries.ApplicationAsync(key, context.RequestAborted);
        if (application is not null && caller.Reaches(application.Id))
        {
            return (application, null);
        }

        return (null, application is null && caller.AllApplications
            ? ManagementProblems.NotFound("There is no application with that key, or it is archived.")
            : ManagementProblems.OutOfScope());
    }
}
