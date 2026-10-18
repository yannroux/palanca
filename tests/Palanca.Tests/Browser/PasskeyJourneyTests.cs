using Microsoft.Playwright;
using Palanca.Tests.Owners;

namespace Palanca.Tests.Browser;

/// <summary>
/// The owner's sign-in journey in Chrome with a virtual authenticator: the passkey script, WebAuthn, and the
/// browser's cookie rules, which the HTTP-level tests cannot cover.
/// </summary>
public sealed class PasskeyJourneyTests : IAsyncLifetime
{
    private BrowserHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await BrowserHost.StartAsync();

    [Fact]
    public async Task TheOwnerEnrollsSignsInAndManagesPasskeys()
    {
        var origin = _host.Origin;
        await using var context = await _host.NewContextAsync();
        var page = await context.NewPageAsync();
        var sockets = new List<string>();
        page.WebSocket += (_, socket) => sockets.Add(socket.Url);
        var laptop = await BrowserHost.AddVirtualAuthenticatorAsync(context, page);

        var token = await OwnerSetup.IssueLinkAsync(_host.Factory);
        await page.GotoAsync($"/enroll#{token}");
        Assert.Equal($"{origin}/enroll", page.Url);
        await page.GetByLabel("Passkey name").FillAsync("Laptop");
        await page.GetByRole(AriaRole.Button, new() { Name = "Register passkey" }).ClickAsync();
        await page.WaitForURLAsync($"{origin}/sign-in?status=enrolled");
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Passkey registered");
        await BrowserHost.ScreenshotAsync(page, "sign-in");

        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in with a passkey" }).ClickAsync();
        await page.WaitForURLAsync($"{origin}/");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Applications" })).ToBeVisibleAsync();

        var session = Assert.Single(await context.CookiesAsync(), c => c.Name == "__Host-Palanca.Session");
        Assert.Equal((true, true, SameSiteAttribute.Strict, "/"), (session.Secure, session.HttpOnly, session.SameSite, session.Path));
        Assert.Equal(-1, session.Expires);

        // One authenticator holds one passkey per account, so the second passkey lives on a second device.
        await page.GetByRole(AriaRole.Link, new() { Name = "Passkeys" }).ClickAsync();
        await page.GetByLabel("Passkey name").FillAsync("Phone");
        await page.RunAndWaitForResponseAsync(
            () => page.GetByRole(AriaRole.Button, new() { Name = "Add passkey" }).ClickAsync(),
            response => response.Request.Method == "POST" && response.Url == $"{origin}/passkeys");
        await page.WaitForLoadStateAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("already holds a passkey for Palanca");

        await laptop.DevTools.SendAsync("WebAuthn.removeVirtualAuthenticator", new Dictionary<string, object> { ["authenticatorId"] = laptop.Id });
        await BrowserHost.AddVirtualAuthenticatorAsync(context, page);
        await page.GetByLabel("Passkey name").FillAsync("Phone");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add passkey" }).ClickAsync();
        await page.WaitForURLAsync($"{origin}/passkeys?status=added");
        await Assertions.Expect(page.Locator("tbody tr")).ToHaveCountAsync(2);

        var phone = page.Locator("tbody tr", new() { HasText = "Phone" });
        await phone.GetByText("Rename").ClickAsync();
        await phone.GetByLabel("New name for Phone").FillAsync("Work phone");
        await phone.GetByRole(AriaRole.Button, new() { Name = "Save name" }).ClickAsync();
        await page.WaitForURLAsync($"{origin}/passkeys?status=renamed");
        await BrowserHost.ScreenshotAsync(page, "passkeys");

        var laptopRow = page.Locator("tbody tr", new() { HasText = "Laptop" });
        await laptopRow.GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToContainTextAsync("Remove Laptop?");
        await page.GetByRole(AriaRole.Button, new() { Name = "Remove passkey" }).ClickAsync();
        await page.WaitForURLAsync($"{origin}/passkeys?status=removed");
        await Assertions.Expect(page.Locator("tbody tr")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator("tbody tr")).ToContainTextAsync("Work phone");

        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await page.WaitForURLAsync($"{origin}/sign-in?status=signed-out");
        await page.GotoAsync("/passkeys");
        Assert.StartsWith($"{origin}/sign-in?returnUrl=", page.Url, StringComparison.Ordinal);

        Assert.Empty(sockets);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }
}
