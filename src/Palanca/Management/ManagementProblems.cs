namespace Palanca.Management;

/// <summary>The management API's errors, as RFC 9457 problem details (FLG-088).</summary>
internal static class ManagementProblems
{
    /// <summary>Missing, unknown, revoked, or expired: the answer is the same, so it says nothing about which.</summary>
    public static IResult Unauthorized() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "A valid management token is required.");

    public static IResult TooManyFailures() =>
        Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many failed attempts from this address. Try again in a minute.");

    public static IResult OutOfScope() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "This token cannot reach that application.");

    public static IResult Invalid(string title) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);

    public static IResult NotFound(string title) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: title);
}
