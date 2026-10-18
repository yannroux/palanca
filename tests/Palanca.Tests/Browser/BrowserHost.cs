using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Playwright;
using Palanca.Tests.Owners;

namespace Palanca.Tests.Browser;

/// <summary>
/// Palanca over HTTPS on a free port, and Google Chrome with a virtual passkey authenticator. The certificate is made
/// for the run and Chrome trusts it by its public key, so pages are a secure origin with no certificate error.
/// Without Chrome the test is skipped locally; on CI, where <c>CI</c> is set, it fails.
/// </summary>
public sealed class BrowserHost : IAsyncDisposable
{
    private X509Certificate2 _certificate = null!;
    private IPlaywright _playwright = null!;

    public PalancaFactory Factory { get; private set; } = null!;

    public IBrowser Browser { get; private set; } = null!;

    public string Origin { get; private set; } = null!;

    /// <summary>The app's clock: real time, which a test can shift back to seed changes made days ago.</summary>
    public ShiftedTimeProvider Time { get; } = new();

    public static async Task<BrowserHost> StartAsync()
    {
        var host = new BrowserHost();
        var port = FreePort();
        host.Origin = $"https://localhost:{port}";
        host._certificate = LocalhostCertificate();
        host.Factory = new PalancaFactory { Time = host.Time, Settings = new Dictionary<string, string?> { ["Palanca:Dashboard:Origin"] = host.Origin } };
        host.Factory.UseKestrel(options => options.ListenLocalhost(port, listen => listen.UseHttps(host._certificate)));
        host.Factory.StartServer();

        host._playwright = await Playwright.CreateAsync();

        // A loaded machine (CI, or tests in parallel) can take longer than Playwright's 5-second default to settle a page.
        Assertions.SetDefaultExpectTimeout(15_000);
        var publicKeyPin = Convert.ToBase64String(SHA256.HashData(host._certificate.PublicKey.ExportSubjectPublicKeyInfo()));
        try
        {
            host.Browser = await host._playwright.Chromium.LaunchAsync(new()
            {
                Channel = "chrome",
                Headless = true,
                Args = [$"--ignore-certificate-errors-spki-list={publicKeyPin}"],
            });
        }
        catch (PlaywrightException exception) when (Environment.GetEnvironmentVariable("CI") is null)
        {
            await host.DisposeAsync();
            Assert.Skip($"Google Chrome is not available: {exception.Message.Split('\n')[0]}");
        }

        return host;
    }

    public Task<IBrowserContext> NewContextAsync(int width = 1440, int height = 900) =>
        Browser.NewContextAsync(new() { BaseURL = Origin, ViewportSize = new() { Width = width, Height = height } });

    public static async Task<(ICDPSession DevTools, string Id)> AddVirtualAuthenticatorAsync(IBrowserContext context, IPage page)
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

    /// <summary>Creates the owner, registers a passkey from the enrollment link, and signs in with it.</summary>
    public async Task SignInAsync(IPage page)
    {
        var token = await OwnerSetup.IssueLinkAsync(Factory);
        await page.GotoAsync($"/enroll#{token}");
        await page.GetByLabel("Passkey name").FillAsync("Laptop");
        await page.GetByRole(AriaRole.Button, new() { Name = "Register passkey" }).ClickAsync();
        await page.WaitForURLAsync($"{Origin}/sign-in?status=enrolled");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in with a passkey" }).ClickAsync();
        await page.WaitForURLAsync($"{Origin}/");
    }

    /// <summary>Set PALANCA_SCREENSHOTS to a directory to keep screenshots for design review.</summary>
    public static async Task ScreenshotAsync(IPage page, string name)
    {
        if (Environment.GetEnvironmentVariable("PALANCA_SCREENSHOTS") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, $"{name}.png"), FullPage = true });
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        _playwright?.Dispose();
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        _certificate?.Dispose();
    }

    public sealed class ShiftedTimeProvider : TimeProvider
    {
        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
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
}
