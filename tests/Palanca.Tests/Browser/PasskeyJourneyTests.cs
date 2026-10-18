using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Playwright;
using Palanca.Tests.Owners;

namespace Palanca.Tests.Browser;

/// <summary>
/// The owner's journey in Chrome with a virtual authenticator: the passkey script, WebAuthn, and the browser's
/// cookie rules, which the HTTP-level tests cannot cover. The server speaks HTTPS with a certificate made for the
/// test, which Chrome is told to trust by its public key, so the page is a secure origin with no certificate error.
/// </summary>
public sealed class PasskeyJourneyTests : IAsyncLifetime
{
    private PalancaFactory _factory = null!;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private string _origin = null!;
    private X509Certificate2 _certificate = null!;

    public async ValueTask InitializeAsync()
    {
        var port = FreePort();
        _origin = $"https://localhost:{port}";
        _certificate = LocalhostCertificate();
        _factory = new PalancaFactory
        {
            Settings = new Dictionary<string, string?> { ["Palanca:Dashboard:Origin"] = _origin },
        };
        _factory.UseKestrel(options => options.ListenLocalhost(port, listen => listen.UseHttps(_certificate)));
        _factory.StartServer();

        _playwright = await Playwright.CreateAsync();
        var publicKeyPin = Convert.ToBase64String(SHA256.HashData(_certificate.PublicKey.ExportSubjectPublicKeyInfo()));
        try
        {
            _browser = await _playwright.Chromium.LaunchAsync(new()
            {
                Channel = "chrome",
                Headless = true,
                Args = [$"--ignore-certificate-errors-spki-list={publicKeyPin}"],
            });
        }
        catch (PlaywrightException exception) when (Environment.GetEnvironmentVariable("CI") is null)
        {
            Assert.Skip($"Google Chrome is not available: {exception.Message.Split('\n')[0]}");
        }
    }

    [Fact]
    public async Task TheOwnerEnrollsSignsInAndManagesPasskeys()
    {
        await using var context = await _browser.NewContextAsync(new()
        {
            BaseURL = _origin,
            ViewportSize = new() { Width = 1440, Height = 900 },
        });
        var page = await context.NewPageAsync();
        var sockets = new List<string>();
        page.WebSocket += (_, socket) => sockets.Add(socket.Url);
        var laptop = await AddVirtualAuthenticatorAsync(context, page);

        var token = await OwnerSetup.IssueLinkAsync(_factory);
        await page.GotoAsync($"/enroll#{token}");
        Assert.Equal($"{_origin}/enroll", page.Url);
        await page.GetByLabel("Passkey name").FillAsync("Laptop");
        await page.GetByRole(AriaRole.Button, new() { Name = "Register passkey" }).ClickAsync();
        await page.WaitForURLAsync($"{_origin}/sign-in?status=enrolled");
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Passkey registered");

        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in with a passkey" }).ClickAsync();
        await page.WaitForURLAsync($"{_origin}/");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Applications" })).ToBeVisibleAsync();
        await ScreenshotAsync(page, "home");

        var session = Assert.Single(await context.CookiesAsync(), c => c.Name == "__Host-Palanca.Session");
        Assert.Equal((true, true, SameSiteAttribute.Strict, "/"), (session.Secure, session.HttpOnly, session.SameSite, session.Path));
        Assert.Equal(-1, session.Expires);

        // One authenticator holds one passkey per account, so the second passkey lives on a second device.
        await page.GetByRole(AriaRole.Link, new() { Name = "Passkeys" }).ClickAsync();
        await page.GetByLabel("Passkey name").FillAsync("Phone");
        await page.RunAndWaitForResponseAsync(
            () => page.GetByRole(AriaRole.Button, new() { Name = "Add passkey" }).ClickAsync(),
            response => response.Request.Method == "POST" && response.Url == $"{_origin}/passkeys");
        await page.WaitForLoadStateAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("already holds a passkey for Palanca");

        await laptop.DevTools.SendAsync("WebAuthn.removeVirtualAuthenticator", new Dictionary<string, object> { ["authenticatorId"] = laptop.Id });
        await AddVirtualAuthenticatorAsync(context, page);
        await page.GetByLabel("Passkey name").FillAsync("Phone");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add passkey" }).ClickAsync();
        await page.WaitForURLAsync($"{_origin}/passkeys?status=added");
        await Assertions.Expect(page.Locator("tbody tr")).ToHaveCountAsync(2);

        var phone = page.Locator("tbody tr", new() { HasText = "Phone" });
        await phone.GetByText("Rename").ClickAsync();
        await phone.GetByLabel("New name for Phone").FillAsync("Work phone");
        await phone.GetByRole(AriaRole.Button, new() { Name = "Save name" }).ClickAsync();
        await page.WaitForURLAsync($"{_origin}/passkeys?status=renamed");
        await ScreenshotAsync(page, "passkeys");

        var laptopRow = page.Locator("tbody tr", new() { HasText = "Laptop" });
        await laptopRow.GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToContainTextAsync("Remove Laptop?");
        await ScreenshotAsync(page, "remove-confirmation");
        await page.GetByRole(AriaRole.Button, new() { Name = "Remove passkey" }).ClickAsync();
        await page.WaitForURLAsync($"{_origin}/passkeys?status=removed");
        await Assertions.Expect(page.Locator("tbody tr")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator("tbody tr")).ToContainTextAsync("Work phone");

        await page.SetViewportSizeAsync(390, 844);
        await ScreenshotAsync(page, "passkeys-mobile");
        await page.SetViewportSizeAsync(1440, 900);

        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await page.WaitForURLAsync($"{_origin}/sign-in?status=signed-out");
        await page.GotoAsync("/passkeys");
        Assert.StartsWith($"{_origin}/sign-in?returnUrl=", page.Url, StringComparison.Ordinal);

        Assert.Empty(sockets);
    }

    private static async Task<(ICDPSession DevTools, string Id)> AddVirtualAuthenticatorAsync(IBrowserContext context, IPage page)
    {
        var devTools = await context.NewCDPSessionAsync(page);
        await devTools.SendAsync("WebAuthn.enable");
        var added = await devTools.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
        {
            ["options"] = new Dictionary<string, object>
            {
                ["protocol"] = "ctap2",
                ["transport"] = "internal",
                ["hasResidentKey"] = true,
                ["hasUserVerification"] = true,
                ["isUserVerified"] = true,
                ["automaticPresenceSimulation"] = true,
            },
        });
        return (devTools, added!.Value.GetProperty("authenticatorId").GetString()!);
    }

    // Set PALANCA_SCREENSHOTS to a directory to keep screenshots of the journey for design review.
    private static async Task ScreenshotAsync(IPage page, string name)
    {
        if (Environment.GetEnvironmentVariable("PALANCA_SCREENSHOTS") is { Length: > 0 } directory)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, $"{name}.png"), FullPage = true });
        }
    }

    private static X509Certificate2 LocalhostCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), password: null);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
        await _factory.DisposeAsync();
        _certificate?.Dispose();
    }
}
