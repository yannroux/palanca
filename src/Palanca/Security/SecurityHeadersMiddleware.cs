namespace Palanca.Security;

/// <summary>
/// Adds the security headers every response carries. The dashboard loads only its own
/// scripts, styles, and fonts, so the content security policy allows nothing else.
/// HSTS is set by the edge proxy, which terminates TLS.
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; " +
        "script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self'";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        return next(context);
    }
}
