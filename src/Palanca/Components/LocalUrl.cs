namespace Palanca.Components;

internal static class LocalUrl
{
    /// <summary>Returns <paramref name="url"/> when it is a path on this site, otherwise the home page, so no link can redirect off-site.</summary>
    public static string OrHome(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
            && Uri.IsWellFormedUriString(url, UriKind.Relative)
            ? url
            : "/";
}
