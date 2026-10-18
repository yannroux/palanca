using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Palanca.Tests.Owners;

/// <summary>
/// Plays the browser's part against the dashboard over HTTP: reads a page's antiforgery tokens, makes the options
/// request the passkey script makes, and posts the form the script would submit. Cookies persist between calls;
/// redirects are returned rather than followed.
/// </summary>
public sealed partial class DashboardClient : IDisposable
{
    private readonly CookieContainer _cookies = new();

    public DashboardClient(PalancaFactory factory)
    {
        Http = factory.CreateDefaultClient(new Uri(PalancaFactory.Origin), new CookieHandler(_cookies));
    }

    public HttpClient Http { get; }

    public Cookie? Cookie(string name) => _cookies.GetAllCookies().FirstOrDefault(c => c.Name == name);

    /// <summary>Removes a cookie, as a browser does when it expires.</summary>
    public void Forget(string name)
    {
        if (Cookie(name) is { } cookie)
        {
            cookie.Expired = true;
        }
    }

    /// <summary>Opens the enrollment link and registers a passkey with <paramref name="authenticator"/>.</summary>
    public async Task<HttpResponseMessage> EnrollAsync(string token, string passkeyName, SoftwareAuthenticator authenticator)
    {
        var page = await GetPageAsync("/enroll");
        using var options = await PostOptionsAsync("/enroll/options", page, new { token });
        options.EnsureSuccessStatusCode();
        var credential = authenticator.Create(await options.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return await PostFormAsync("/enroll", page, "enroll", new()
        {
            ["Input.Token"] = token,
            ["Input.Name"] = passkeyName,
            ["Input.Credential.CredentialJson"] = credential,
        });
    }

    /// <summary>Signs in with the newest passkey <paramref name="authenticator"/> holds, or the one at <paramref name="index"/>.</summary>
    public async Task<HttpResponseMessage> SignInAsync(SoftwareAuthenticator authenticator, string path = "/sign-in", int? index = null)
    {
        var page = await GetPageAsync(path);
        using var options = await PostOptionsAsync("/sign-in/options", page);
        if (!options.IsSuccessStatusCode)
        {
            return await PostFormAsync(path, page, "sign-in", new() { ["Credential.Error"] = $"options-{(int)options.StatusCode}" });
        }

        var credential = authenticator.Get(await options.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), index);
        return await PostFormAsync(path, page, "sign-in", new() { ["Credential.CredentialJson"] = credential });
    }

    /// <summary>Adds a passkey on the passkeys page with a new credential from <paramref name="authenticator"/>.</summary>
    public async Task<HttpResponseMessage> AddPasskeyAsync(SoftwareAuthenticator authenticator, string passkeyName)
    {
        var page = await GetPageAsync("/passkeys");
        using var options = await PostOptionsAsync("/passkeys/options", page);
        options.EnsureSuccessStatusCode();
        var credential = authenticator.Create(await options.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return await PostFormAsync("/passkeys", page, "add-passkey", new()
        {
            ["Input.Name"] = passkeyName,
            ["Input.Credential.CredentialJson"] = credential,
        });
    }

    public async Task<Page> GetPageAsync(string path)
    {
        using var response = await Http.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new Page(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    public Task<HttpResponseMessage> PostOptionsAsync(string path, Page page, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        // A page without a passkey button carries the same request token in its forms.
        request.Headers.Add("RequestVerificationToken", page.HeaderToken is { Length: > 0 } header ? header : page.FormToken);
        return Http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public Task<HttpResponseMessage> PostFormAsync(string path, Page page, string formName, Dictionary<string, string> fields)
    {
        fields["_handler"] = formName;
        fields["__RequestVerificationToken"] = page.FormToken;
        return Http.PostAsync(path, new FormUrlEncodedContent(fields), TestContext.Current.CancellationToken);
    }

    /// <summary>Posts fields that may repeat a name, as a group of checkboxes does.</summary>
    public Task<HttpResponseMessage> PostFormAsync(string path, Page page, string formName, IEnumerable<KeyValuePair<string, string>> fields) =>
        Http.PostAsync(path, new FormUrlEncodedContent([.. fields, new("_handler", formName), new("__RequestVerificationToken", page.FormToken)]),
            TestContext.Current.CancellationToken);

    public void Dispose() => Http.Dispose();

    public sealed partial class Page(string html)
    {
        public string Html { get; } = html;

        public string FormToken => FormTokenPattern().Match(Html).Groups[1].Value;

        public string HeaderToken => WebUtility.HtmlDecode(HeaderTokenPattern().Match(Html).Groups[1].Value);

        public IReadOnlyList<string> CredentialIds => [.. CredentialIdPattern().Matches(Html).Select(m => m.Groups[1].Value)];

        [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"", RegexOptions.None, "en-US")]
        private static partial Regex FormTokenPattern();

        [GeneratedRegex("request-token-value=\"([^\"]+)\"", RegexOptions.None, "en-US")]
        private static partial Regex HeaderTokenPattern();

        [GeneratedRegex("name=\"CredentialId\" value=\"([^\"]+)\"", RegexOptions.None, "en-US")]
        private static partial Regex CredentialIdPattern();
    }

    /// <summary>A cookie jar that, like a browser, keeps Secure cookies only for HTTPS requests.</summary>
    private sealed class CookieHandler(CookieContainer cookies) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var header = cookies.GetCookieHeader(request.RequestUri!);
            if (header.Length > 0)
            {
                request.Headers.Add("Cookie", header);
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                {
                    cookies.SetCookies(request.RequestUri!, setCookie);
                }
            }

            return response;
        }
    }

    /// <summary>Where <paramref name="response"/> redirects, as a path and query whether the header is absolute or relative.</summary>
    public static string? RedirectPath(HttpResponseMessage response) =>
        response.Headers.Location is { } location ? (location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString) : null;

    /// <summary>The raw Set-Cookie headers of <paramref name="response"/>.</summary>
    public static IReadOnlyList<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? [.. values] : [];

}
