using System.Net;
using Microsoft.EntityFrameworkCore;
using Palanca.Data;

namespace Palanca.Tests.Owners;

/// <summary>The passkeys page (FLG-047): add up to 10, rename, and remove any but the last.</summary>
public sealed class PasskeyManagementTests : IAsyncDisposable
{
    private readonly PalancaFactory _factory = new();
    private readonly SoftwareAuthenticator _authenticator = new();
    private readonly DashboardClient _client;

    public PasskeyManagementTests() => _client = new DashboardClient(_factory);

    [Fact]
    public async Task AddingAPasskeyRecordsItAndItSignsIn()
    {
        await OwnerSetup.SignInAsync(_factory, _client, _authenticator);

        using var added = await _client.AddPasskeyAsync(_authenticator, "Phone");

        Assert.Equal("/passkeys?status=added", DashboardClient.RedirectPath(added));
        var names = await _factory.WithDbAsync(db => db.OwnerPasskeys.OrderBy(p => p.CreatedAt).Select(p => p.Name).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["Laptop", "Phone"], names);
        var record = await _factory.WithDbAsync(db => db.ChangeRecords.OrderBy(r => r.Id).LastAsync(TestContext.Current.CancellationToken));
        Assert.Equal((ChangeAction.PasskeyRegistered, OwnerSetup.Email, "Phone"), (record.Action, record.Actor, record.NewValue));

        using var other = new DashboardClient(_factory);
        using var signedIn = await other.SignInAsync(_authenticator);
        Assert.Equal("/", DashboardClient.RedirectPath(signedIn));
    }

    [Fact]
    public async Task RenamingAPasskeyChangesOnlyItsName()
    {
        await OwnerSetup.SignInAsync(_factory, _client, _authenticator);
        var page = await _client.GetPageAsync("/passkeys");
        var id = Assert.Single(page.CredentialIds);

        using var renamed = await _client.PostFormAsync("/passkeys", page, $"passkey-{id}", new()
        {
            ["CredentialId"] = id,
            ["Action"] = "rename",
            ["Name"] = "  Work laptop  ",
        });

        Assert.Equal("/passkeys?status=renamed", DashboardClient.RedirectPath(renamed));
        Assert.Equal("Work laptop", await _factory.WithDbAsync(db => db.OwnerPasskeys.Select(p => p.Name).SingleAsync(TestContext.Current.CancellationToken)));
        var record = await _factory.WithDbAsync(db => db.ChangeRecords.OrderBy(r => r.Id).LastAsync(TestContext.Current.CancellationToken));
        Assert.Equal((ChangeAction.PasskeyRenamed, "Laptop", "Work laptop"), (record.Action, record.OldValue, record.NewValue));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a name that is far too long to be useful as the label of a passkey, over 64")]
    public async Task ABlankOrLongNameIsRefused(string name)
    {
        await OwnerSetup.SignInAsync(_factory, _client, _authenticator);
        var page = await _client.GetPageAsync("/passkeys");
        var id = Assert.Single(page.CredentialIds);

        using var response = await _client.PostFormAsync("/passkeys", page, $"passkey-{id}", new() { ["CredentialId"] = id, ["Action"] = "rename", ["Name"] = name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Laptop", await _factory.WithDbAsync(db => db.OwnerPasskeys.Select(p => p.Name).SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task TheLastPasskeyCannotBeRemoved()
    {
        await OwnerSetup.SignInAsync(_factory, _client, _authenticator);
        var page = await _client.GetPageAsync("/passkeys");
        var id = Assert.Single(page.CredentialIds);

        using var response = await _client.PostFormAsync("/passkeys", page, $"passkey-{id}", new() { ["CredentialId"] = id, ["Action"] = "remove" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.OwnerPasskeys.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ARemovedPasskeyNoLongerSignsIn()
    {
        await OwnerSetup.SignInAsync(_factory, _client, _authenticator);
        (await _client.AddPasskeyAsync(_authenticator, "Phone")).Dispose();
        var page = await _client.GetPageAsync("/passkeys");

        using (var removed = await _client.PostFormAsync("/passkeys", page, $"passkey-{page.CredentialIds[0]}",
            new() { ["CredentialId"] = page.CredentialIds[0], ["Action"] = "remove" }))
        {
            Assert.Equal("/passkeys?status=removed", DashboardClient.RedirectPath(removed));
        }

        var record = await _factory.WithDbAsync(db => db.ChangeRecords.OrderBy(r => r.Id).LastAsync(TestContext.Current.CancellationToken));
        Assert.Equal((ChangeAction.PasskeyRemoved, "Laptop"), (record.Action, record.OldValue));
        using var other = new DashboardClient(_factory);
        using var rejected = await other.SignInAsync(_authenticator, index: 0);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task TenPasskeysIsTheLimit()
    {
        await OwnerSetup.SignInAsync(_factory, _client, _authenticator);
        for (var count = 1; count < 10; count++)
        {
            (await _client.AddPasskeyAsync(_authenticator, $"Passkey {count + 1}")).Dispose();
        }

        var page = await _client.GetPageAsync("/passkeys");
        using var options = await _client.PostOptionsAsync("/passkeys/options", page);

        Assert.Equal(HttpStatusCode.Conflict, options.StatusCode);
        Assert.Equal(10, await _factory.WithDbAsync(db => db.OwnerPasskeys.CountAsync(TestContext.Current.CancellationToken)));
        Assert.DoesNotContain("value=\"add-passkey\"", page.Html, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _authenticator.Dispose();
        await _factory.DisposeAsync();
    }
}
