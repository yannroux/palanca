using Microsoft.AspNetCore.Mvc.Testing;

namespace Palanca.Tests;

public sealed class SecurityHeadersTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/no-such-route")]
    public async Task EveryResponseCarriesTheSecurityHeaders(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        var headers = response.Headers;
        Assert.Equal("nosniff", Assert.Single(headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(headers.GetValues("Referrer-Policy")));
        Assert.Equal("same-origin", Assert.Single(headers.GetValues("Cross-Origin-Opener-Policy")));
        var policy = Assert.Single(headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self';", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none';", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-", policy, StringComparison.Ordinal);
    }
}
