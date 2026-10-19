using System.Net;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Palanca.Components;
using Palanca.Metrics;
using Palanca.Owners;
using Palanca.Security;
using Palanca.Tests.Metrics;

namespace Palanca.Tests.Owners;

public sealed class OwnerUnitTests
{
    [Theory]
    [InlineData("https://localhost", false, true)]
    [InlineData("https://sub.localhost", false, false)]
    [InlineData("http://localhost", false, false)]
    [InlineData("https://localhost:8443", false, false)]
    [InlineData("https://LOCALHOST", false, false)]
    [InlineData("https://localhost", true, false)]
    public void OnlyTheExactOriginIsAccepted(string origin, bool crossOrigin, bool accepted) =>
        Assert.Equal(accepted, OwnerAuthentication.IsAcceptedOrigin("https://localhost", origin, crossOrigin));

    [Theory]
    [InlineData("https://palanca.hogarhero.com", true, "https://palanca.hogarhero.com", "palanca.hogarhero.com")]
    [InlineData("https://localhost:7180/", true, "https://localhost:7180", "localhost")]
    [InlineData("https://palanca.hogarhero.com/dashboard", false, null, null)]
    [InlineData("ftp://palanca.hogarhero.com", false, null, null)]
    [InlineData("", false, null, null)]
    public void TheDashboardOriginIsAnOriginWithNoPath(string configured, bool valid, string? origin, string? relyingParty)
    {
        var options = new DashboardOptions { Origin = configured };

        Assert.Equal(valid, options.IsValid());
        if (valid)
        {
            Assert.Equal((origin, relyingParty), (options.NormalizedOrigin, options.RelyingPartyId));
        }
    }

    [Fact]
    public void AnEnrollmentTokenHashesToWhatIsStored()
    {
        var (token, hash) = EnrollmentTokens.Create();

        Assert.Equal(43, token.Length);
        Assert.Equal(hash, EnrollmentTokens.Hash(token));
        Assert.NotEqual(hash, EnrollmentTokens.Hash(EnrollmentTokens.Create().Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong")]
    [InlineData("not a token at all")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void AnythingElseIsNotAToken(string? token) => Assert.Null(EnrollmentTokens.Hash(token));

    [Theory]
    [InlineData("Laptop", "Laptop")]
    [InlineData("  Phone  ", "Phone")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("tab\there", null)]
    public void PasskeyNamesAreTrimmedAndChecked(string name, string? expected) =>
        Assert.Equal(expected, OwnerAccounts.NormalizePasskeyName(name));

    [Theory]
    [InlineData("/passkeys", "/passkeys")]
    [InlineData("/passkeys?status=added", "/passkeys?status=added")]
    [InlineData(null, "/")]
    [InlineData("passkeys", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    public void OnlyLocalPathsAreFollowedAfterSignIn(string? url, string expected) => Assert.Equal(expected, LocalUrl.OrHome(url));

    [Fact]
    public void TheLimiterRefusesAfterTenFailuresUntilTheOldestIsAMinuteOld()
    {
        var time = new FakeTimeProvider();
        var limiter = Limiter(time);
        var address = IPAddress.Parse("203.0.113.1");

        for (var failure = 0; failure < 10; failure++)
        {
            Assert.False(limiter.IsLimited(address));
            limiter.RecordFailure(address, FailedCredential.SdkKey);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.True(limiter.IsLimited(address));
        Assert.False(limiter.IsLimited(IPAddress.Parse("203.0.113.2")));

        time.Advance(TimeSpan.FromSeconds(50));
        Assert.False(limiter.IsLimited(address));
    }

    [Fact]
    public void TheLimitCanBeChanged()
    {
        var limiter = Limiter(new FakeTimeProvider(), permitLimit: 3);
        var address = IPAddress.Parse("203.0.113.1");

        for (var failure = 0; failure < 3; failure++)
        {
            limiter.RecordFailure(address, FailedCredential.SdkKey);
        }

        Assert.True(limiter.IsLimited(address));
    }

    [Fact]
    public void AnIPv6ClientIsCountedByItsSlash64()
    {
        var limiter = Limiter(new FakeTimeProvider());

        for (var host = 1; host <= 10; host++)
        {
            limiter.RecordFailure(IPAddress.Parse($"2001:db8:1:2::{host:x}"), FailedCredential.SdkKey);
        }

        Assert.True(limiter.IsLimited(IPAddress.Parse("2001:db8:1:2:ffff::1")));
        Assert.False(limiter.IsLimited(IPAddress.Parse("2001:db8:1:3::1")));
    }

    [Fact]
    public void PrivateIPv6AddressesAreCountedOneByOne()
    {
        // Containers on an IPv6 Docker network share one /64; one misbehaving container must not limit the others.
        var limiter = Limiter(new FakeTimeProvider());

        for (var failure = 0; failure < 10; failure++)
        {
            limiter.RecordFailure(IPAddress.Parse("fd00:dead:beef::2"), FailedCredential.SdkKey);
        }

        Assert.True(limiter.IsLimited(IPAddress.Parse("fd00:dead:beef::2")));
        Assert.False(limiter.IsLimited(IPAddress.Parse("fd00:dead:beef::3")));
    }

    [Fact]
    public void AddressesThatNeverReturnAreForgotten()
    {
        var time = new FakeTimeProvider();
        var limiter = Limiter(time);
        for (var host = 1; host <= 100; host++)
        {
            limiter.RecordFailure(IPAddress.Parse($"198.51.100.{host}"), FailedCredential.SdkKey);
        }

        time.Advance(TimeSpan.FromMinutes(2));
        limiter.RecordFailure(IPAddress.Parse("203.0.113.1"), FailedCredential.SdkKey);

        Assert.Equal(1, limiter.TrackedAddresses);
    }

    private static FailedAuthenticationLimiter Limiter(TimeProvider time, int permitLimit = 10) =>
        new(time, Options.Create(new FailedAuthenticationOptions { PermitLimit = permitLimit }), new ServerMetrics(new TestMeterFactory()));
}
