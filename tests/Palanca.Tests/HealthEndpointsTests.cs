using System.Net;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;

namespace Palanca.Tests;

public sealed class HealthEndpointsTests(PalancaFactory factory) : IClassFixture<PalancaFactory>
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpointReportsHealthy(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task NotReadyWhenTheDatabaseIsReadOnly()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Uses Unix file modes.");
        Assert.SkipWhen(Environment.UserName == "root", "File modes do not bind root.");
        await using var readOnlyFactory = new PalancaFactory();
        using var client = readOnlyFactory.CreateClient();
        using (var before = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        foreach (var file in Directory.GetFiles(readOnlyFactory.DataDirectory, "palanca.db*"))
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        SqliteConnection.ClearAllPools();

        using var after = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, after.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", TestContext.Current.CancellationToken)).StatusCode);
    }
}
