namespace Palanca.Owners;

internal sealed class DashboardOptions
{
    public const string SectionName = "Palanca:Dashboard";

    /// <summary>
    /// The exact origin the dashboard is served from, such as <c>https://palanca.hogarhero.com</c>.
    /// Passkeys are bound to its host, they are accepted only from this origin, and enrollment links point at it.
    /// </summary>
    public string Origin { get; set; } = "";

    public Uri OriginUri => new(Origin);

    /// <summary>The origin as browsers report it in a passkey response: scheme, host, and any non-default port.</summary>
    public string NormalizedOrigin => OriginUri.GetLeftPart(UriPartial.Authority);

    public string RelyingPartyId => OriginUri.Host;

    public bool IsValid() =>
        Uri.TryCreate(Origin, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0;
}
