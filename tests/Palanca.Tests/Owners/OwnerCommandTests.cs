using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Palanca.Data;

namespace Palanca.Tests.Owners;

/// <summary>The host command that creates and recovers the owner account (FLG-046, AC-024).</summary>
public sealed class OwnerCommandTests : IAsyncDisposable
{
    private readonly PalancaFactory _factory = new();

    [Fact]
    public async Task EnrollPrintsALinkToTheEnrollmentPageAndStoresOnlyItsHash()
    {
        var (exitCode, output, _) = await OwnerSetup.RunCommandAsync(_factory, "enroll", "owner@example.com");

        Assert.Equal(0, exitCode);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal("Enrollment link for owner@example.com, valid for 15 minutes and usable once:", lines[0]);
        Assert.StartsWith("https://localhost/enroll#", lines[1], StringComparison.Ordinal);
        var token = lines[1].Split('#')[1];
        Assert.Equal(32, Base64Url.DecodeFromChars(token).Length);

        await _factory.WithDbAsync(async db =>
        {
            var enrollment = await db.OwnerEnrollments.Include(e => e.Owner).SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(SHA256.HashData(Base64Url.DecodeFromChars(token)), enrollment.Hash);
            Assert.Equal("owner@example.com", enrollment.Owner.Email);
            Assert.Null(enrollment.UsedAt);
            Assert.InRange(enrollment.ExpiresAt - DateTime.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
        });
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("Owner <owner@example.com>")]
    public async Task EnrollRefusesSomethingOtherThanAnEmail(string email)
    {
        var (exitCode, _, error) = await OwnerSetup.RunCommandAsync(_factory, "enroll", email);

        Assert.Equal(1, exitCode);
        Assert.Contains("is not an email address", error, StringComparison.Ordinal);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Owners.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task EnrollRefusesASecondOwner()
    {
        await OwnerSetup.RunCommandAsync(_factory, "enroll", "owner@example.com");

        var (exitCode, _, error) = await OwnerSetup.RunCommandAsync(_factory, "enroll", "someone@example.com");

        Assert.Equal(1, exitCode);
        Assert.Contains("already has an owner", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnrollRefusesOnceTheOwnerHasAPasskey()
    {
        using var client = new DashboardClient(_factory);
        using var authenticator = new SoftwareAuthenticator();
        (await client.EnrollAsync(await OwnerSetup.IssueLinkAsync(_factory), "Laptop", authenticator)).Dispose();

        var (exitCode, _, error) = await OwnerSetup.RunCommandAsync(_factory, "enroll", OwnerSetup.Email);

        Assert.Equal(1, exitCode);
        Assert.Contains("owner recover", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoverNeedsAnOwner()
    {
        var (exitCode, _, error) = await OwnerSetup.RunCommandAsync(_factory, "recover");

        Assert.Equal(1, exitCode);
        Assert.Contains("owner enroll", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoverRotatesTheSecurityStamp()
    {
        await OwnerSetup.RunCommandAsync(_factory, "enroll", OwnerSetup.Email);
        var before = await _factory.WithDbAsync(db => db.Owners.Select(o => o.SecurityStamp).SingleAsync(TestContext.Current.CancellationToken));

        var (exitCode, output, _) = await OwnerSetup.RunCommandAsync(_factory, "recover");

        Assert.Equal(0, exitCode);
        Assert.Contains("/enroll#", output, StringComparison.Ordinal);
        var after = await _factory.WithDbAsync(db => db.Owners.Select(o => o.SecurityStamp).SingleAsync(TestContext.Current.CancellationToken));
        Assert.NotEqual(before, after);
        var record = await _factory.WithDbAsync(db => db.ChangeRecords.OrderBy(r => r.Id).LastAsync(TestContext.Current.CancellationToken));
        Assert.Equal((ChangeAction.OwnerRecovered, ChangeSource.Host, "host"), (record.Action, record.Source, record.Actor));
    }

    [Theory]
    [InlineData]
    [InlineData("enroll")]
    [InlineData("recover", "now")]
    [InlineData("delete")]
    public async Task OtherArgumentsPrintUsage(params string[] args)
    {
        var (exitCode, _, error) = await OwnerSetup.RunCommandAsync(_factory, args);

        Assert.Equal(2, exitCode);
        Assert.Contains("owner enroll <email>", error, StringComparison.Ordinal);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
