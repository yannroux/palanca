using System.Net;
using System.Text.Json;

namespace Palanca.Tests.Management;

/// <summary>The management API's OpenAPI document: public, and about the management endpoints only (FLG-088, AC-021).</summary>
public sealed class ManagementOpenApiTests(PalancaFactory factory) : IClassFixture<PalancaFactory>
{
    private const string Base = "/api/manage/v1";

    [Fact]
    public async Task TheDocumentIsPublicAndCoversOnlyTheManagementApi()
    {
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        using var response = await client.GetAsync($"{Base}/openapi.json", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = document.RootElement;
        Assert.StartsWith("3.", root.GetProperty("openapi").GetString(), StringComparison.Ordinal);
        Assert.Equal("Palanca management API", root.GetProperty("info").GetProperty("title").GetString());
        string[] expected =
        [
            $"GET {Base}/applications",
            $"GET {Base}/applications/{{app}}/flags",
            $"POST {Base}/applications/{{app}}/flags",
            $"GET {Base}/applications/{{app}}/flags/{{flag}}",
            $"PATCH {Base}/applications/{{app}}/flags/{{flag}}",
            $"PUT {Base}/applications/{{app}}/flags/{{flag}}/values/{{env}}",
            $"GET {Base}/applications/{{app}}/history",
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), Operations(root).Select(operation => $"{operation.Method} {operation.Path}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task EveryOperationNeedsAManagementToken()
    {
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync($"{Base}/openapi.json", Ct));
        var root = document.RootElement;

        var scheme = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("managementToken");
        Assert.Equal(("http", "bearer"), (scheme.GetProperty("type").GetString(), scheme.GetProperty("scheme").GetString()));
        Assert.All(Operations(root), operation =>
            Assert.Contains(operation.Body.GetProperty("security").EnumerateArray(), requirement => requirement.TryGetProperty("managementToken", out _)));
    }

    [Fact]
    public async Task WritesDescribeTheirBodyAndResponses()
    {
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync($"{Base}/openapi.json", Ct));

        var create = Operations(document.RootElement).Single(o => o.Method == "POST").Body;
        var schema = create.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Equal(["description", "key"], schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(["description", "key", "values"], schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["201", "400", "401", "403", "404", "409", "415", "429"],
            create.GetProperty("responses").EnumerateObject().Select(response => response.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("/openapi/openapi.json")]
    [InlineData("/openapi/v1.json")]
    [InlineData($"{Base}/v1.json")]
    public async Task NoOtherDocumentIsServedAnonymously(string path)
    {
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        using var response = await client.GetAsync(path, Ct);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    private static List<(string Method, string Path, JsonElement Body)> Operations(JsonElement document) =>
        [.. document.GetProperty("paths").EnumerateObject().SelectMany(path => path.Value.EnumerateObject()
            .Where(operation => operation.Name is "get" or "put" or "post" or "delete" or "patch" or "head" or "options")
            .Select(operation => (operation.Name.ToUpperInvariant(), path.Name, operation.Value)))];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
