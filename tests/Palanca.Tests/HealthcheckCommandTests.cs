using Palanca.Commands;

namespace Palanca.Tests;

public sealed class HealthcheckCommandTests(PalancaFactory factory) : IClassFixture<PalancaFactory>
{
    [Theory]
    [InlineData(null, "http://127.0.0.1:8080/health/live")]
    [InlineData("", "http://127.0.0.1:8080/health/live")]
    [InlineData("8081", "http://127.0.0.1:8081/health/live")]
    [InlineData(" 9000 ; 9001", "http://127.0.0.1:9000/health/live")]
    public void LivenessUriUsesTheFirstConfiguredHttpPort(string? httpPorts, string expected)
    {
        Assert.Equal(new Uri(expected), HealthcheckCommand.LivenessUri(httpPorts));
    }

    [Fact]
    public async Task SucceedsWhenTheLivenessEndpointAnswers()
    {
        using var client = factory.CreateClient();

        var exitCode = await HealthcheckCommand.RunAsync(client, new Uri(client.BaseAddress!, "/health/live"), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task FailsWhenTheEndpointAnswersWithAnError()
    {
        using var client = factory.CreateClient();

        var exitCode = await HealthcheckCommand.RunAsync(client, new Uri(client.BaseAddress!, "/no-such-route"), TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task FailsWhenTheServerIsUnreachable()
    {
        using var client = new HttpClient(new UnreachableHandler());

        var exitCode = await HealthcheckCommand.RunAsync(client, HealthcheckCommand.LivenessUri(null), TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused");
    }
}
