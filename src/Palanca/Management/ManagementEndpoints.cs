using System.Globalization;
using Microsoft.OpenApi;
using Palanca.Data;
using Palanca.Flags;

namespace Palanca.Management;

/// <summary>
/// The management API under <c>/api/manage/v1</c> (FLG-080): what scripts and AI agents read and change without the
/// dashboard. Every request needs a management token (FLG-081), and reaches only the applications in the token's
/// scope; only read-write tokens write (FLG-082).
/// </summary>
internal static class ManagementEndpoints
{
    public const string BasePath = "/api/manage/v1";

    /// <summary>The OpenAPI document's name, which also makes its path: <c>/api/manage/v1/openapi.json</c>.</summary>
    private const string OpenApiDocument = "openapi";

    public static IServiceCollection AddManagementApi(this IServiceCollection services)
    {
        services.AddOptions<ManagementOptions>()
            .BindConfiguration(ManagementOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<ManagementWriteLimiter>();
        services.AddScoped<ManagementQueries>();

        // The document covers only the management API (FLG-088), not the evaluation API or the dashboard.
        services.AddOpenApi(OpenApiDocument, options =>
        {
            options.ShouldInclude = description => description.RelativePath?.StartsWith(BasePath[1..] + "/", StringComparison.Ordinal) == true;
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Palanca management API",
                    Version = "v1",
                    Description = "Read and change Palanca's feature flags with a management token, created on the dashboard's Tokens page.",
                };
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    ["managementToken"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        Description = "A management token, palanca_mgt_ followed by 43 characters.",
                    },
                };
                foreach (var operation in document.Paths.Values.SelectMany(path => path.Operations ?? []))
                {
                    operation.Value.Security ??= [];
                    operation.Value.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("managementToken", document)] = [] });
                }

                return Task.CompletedTask;
            });
        });
        return services;
    }

    public static IEndpointRouteBuilder MapManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // The contract is public, so agents can discover it before they hold a token.
        endpoints.MapOpenApi($"{BasePath}/{{documentName:regex(^{OpenApiDocument}$)}}.json").AllowAnonymous();

        // The token check stands in for the dashboard's sign-in requirement, so the session cookie authorizes nothing here.
        var api = endpoints.MapGroup(BasePath)
            .AllowAnonymous()
            .AddEndpointFilter<ManagementAuthentication>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        api.MapGet("/applications", ListApplicationsAsync)
            .WithName("ListApplications").WithSummary("The applications in the token's scope, with their environments.")
            .Produces<ApplicationsResponse>();
        api.MapGet("/applications/{app}/flags", ListFlagsAsync)
            .WithName("ListFlags").WithSummary("The application's flags, with their value in each environment.")
            .Produces<FlagListResponse>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        api.MapGet("/applications/{app}/flags/{flag}", GetFlagAsync)
            .WithName("GetFlag").WithSummary("One flag, with its value in each environment.")
            .Produces<FlagResource>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        api.MapGet("/applications/{app}/history", GetHistoryAsync)
            .WithName("GetHistory").WithSummary("The application's change records, newest first, 50 at a time.")
            .Produces<HistoryResponse>().ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/applications/{app}/flags", CreateFlagAsync)
            .WithName("CreateFlag").WithSummary("Create a flag. It starts off in every environment its values do not turn on.")
            .WithJsonBody<CreateFlagRequest>().Produces<FlagResource>(StatusCodes.Status201Created).WritesProblems();
        api.MapPatch("/applications/{app}/flags/{flag}", EditFlagAsync)
            .WithName("EditFlag").WithSummary("Change a flag's description.")
            .WithJsonBody<EditFlagRequest>().Produces<FlagResource>().WritesProblems();
        api.MapPut("/applications/{app}/flags/{flag}/values/{env}", SetValueAsync)
            .WithName("SetValue").WithSummary("Turn a flag on or off in one environment, production included. Setting the current value changes nothing.")
            .WithJsonBody<SetValueRequest>().Produces<ValueResponse>().WritesProblems();

        // Any other path or method answers after the token check, like the rest of the API, rather than with the
        // dashboard's sign-in redirect. No endpoint archives or deletes flags, or manages applications or credentials (FLG-085).
        api.Map("/{**path}", () => ManagementProblems.NotFound("The management API has no such endpoint."))
            .ExcludeFromDescription();
        return endpoints;
    }

    /// <summary>
    /// Describes the endpoint's JSON body in the OpenAPI document only. <c>Accepts</c> would also make routing skip the
    /// endpoint for another content type, so the request would reach the catch-all and get 404 rather than 415.
    /// </summary>
    private static RouteHandlerBuilder WithJsonBody<T>(this RouteHandlerBuilder builder) =>
        builder.AddOpenApiOperationTransformer(async (operation, context, cancellationToken) =>
        {
            var schema = await context.GetOrCreateSchemaAsync(typeof(T), parameterDescription: null, cancellationToken);
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = schema } },
            };
        });

    private static RouteHandlerBuilder WritesProblems(this RouteHandlerBuilder builder) => builder
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

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

        return (await queries.FlagsAsync(application, flag, context.RequestAborted)) is [var found] ? Results.Ok(found) : FlagNotFound();
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

    /// <summary>Creates a flag. Unlike the dashboard, a read-write token may start it on in a protected environment (FLG-082).</summary>
    private static async Task<IResult> CreateFlagAsync(
        string app, HttpContext context, ManagementQueries queries, ManagementWriteLimiter limiter, FlagCommands flags)
    {
        var (application, problem) = await ReachForWriteAsync(app, context, queries, limiter);
        if (application is null)
        {
            return problem!;
        }

        var (body, invalid) = await ManagementRequests.ReadAsync<CreateFlagRequest>(context);
        if (body is null)
        {
            return invalid!;
        }

        var values = body.Values ?? [];
        var environments = await queries.EnvironmentKeysAsync(application, context.RequestAborted);
        if (values.Keys.FirstOrDefault(key => !environments.Contains(key)) is { } unknown)
        {
            return ManagementProblems.Invalid($"{application.Name} has no environment with the key {unknown}.");
        }

        var result = await flags.CreateAsync(
            application.Key, body.Key, body.Description, [.. values.Where(value => value.Value).Select(value => value.Key)],
            context.ManagementCaller().Actor, context.RequestAborted);
        if (!result.Succeeded)
        {
            return ManagementProblems.From(result);
        }

        var created = (await queries.FlagsAsync(application, body.Key, context.RequestAborted)).Single();
        return Results.Created($"{BasePath}/applications/{application.Key}/flags/{created.Key}", created);
    }

    private static async Task<IResult> EditFlagAsync(
        string app, string flag, HttpContext context, ManagementQueries queries, ManagementWriteLimiter limiter, FlagCommands flags)
    {
        var (application, problem) = await ReachForWriteAsync(app, context, queries, limiter);
        if (application is null)
        {
            return problem!;
        }

        var (body, invalid) = await ManagementRequests.ReadAsync<EditFlagRequest>(context);
        if (body is null)
        {
            return invalid!;
        }

        // The dashboard can describe an archived flag; the API, which never shows one, cannot.
        if ((await queries.FlagsAsync(application, flag, context.RequestAborted)).Count == 0)
        {
            return FlagNotFound();
        }

        var result = await flags.EditDescriptionAsync(application.Key, flag, body.Description, context.ManagementCaller().Actor, context.RequestAborted);
        return result.Succeeded
            ? Results.Ok((await queries.FlagsAsync(application, flag, context.RequestAborted)).Single())
            : ManagementProblems.From(result);
    }

    /// <summary>
    /// Sets one value. A read-write token may change a protected environment without the dashboard's confirmation, by
    /// design (FLG-082); setting the current value again changes nothing and records nothing (FLG-089).
    /// </summary>
    private static async Task<IResult> SetValueAsync(
        string app, string flag, string env, HttpContext context, ManagementQueries queries, ManagementWriteLimiter limiter, FlagValueWriter writer)
    {
        var (application, problem) = await ReachForWriteAsync(app, context, queries, limiter);
        if (application is null)
        {
            return problem!;
        }

        var (body, invalid) = await ManagementRequests.ReadAsync<SetValueRequest>(context);
        if (body is null)
        {
            return invalid!;
        }

        var result = await writer.SetAsync(application.Key, flag, env, body.Enabled, context.ManagementCaller().Actor, confirmed: true, context.RequestAborted);
        return result.Outcome == SetFlagValueOutcome.NotFound
            ? ManagementProblems.NotFound("The application has no flag or environment with those keys, or it is archived.")
            : Results.Ok(new ValueResponse(application.Key, env, flag, body.Enabled, result.Revision));
    }

    private static IResult FlagNotFound() => ManagementProblems.NotFound("The application has no flag with that key, or it is archived.");

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

    /// <summary>
    /// The application, when the token may write to it now: a read-only token gets 403 for every write (AC-017), and a
    /// token past its writes for the minute gets 429 (FLG-086). Writes the token may not attempt do not count.
    /// </summary>
    private static async Task<(Application? Application, IResult? Problem)> ReachForWriteAsync(
        string key, HttpContext context, ManagementQueries queries, ManagementWriteLimiter limiter)
    {
        var caller = context.ManagementCaller();
        if (caller.Permission != TokenPermission.ReadWrite)
        {
            return (null, ManagementProblems.ReadOnly());
        }

        var (application, problem) = await ReachAsync(key, context, queries);
        if (application is null)
        {
            return (null, problem);
        }

        if (!limiter.TryAcquire(caller.TokenId, out var retryAfter))
        {
            context.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            return (null, ManagementProblems.TooManyWrites(limiter.WritesPerMinute));
        }

        return (application, null);
    }
}
