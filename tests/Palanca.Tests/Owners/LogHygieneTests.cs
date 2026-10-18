using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Palanca.Applications;
using Palanca.Data;
using Palanca.SdkKeys;

namespace Palanca.Tests.Owners;

/// <summary>Logs and error responses never carry an enrollment link, a passkey response, a cookie, or an SDK key (AC-011).</summary>
public sealed class LogHygieneTests
{
    [Fact]
    public async Task SignInFlowsLeaveNoSecretInTheLogs()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new PalancaFactory
        {
            Logs = logs,
            Settings = new Dictionary<string, string?>
            {
                // Every category, ASP.NET Core's and EF Core's included, which the defaults keep quieter.
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Trace",
            },
        };
        using var client = new DashboardClient(factory);
        using var authenticator = new SoftwareAuthenticator();

        var token = await OwnerSetup.IssueLinkAsync(factory);
        var secrets = new List<string> { token };
        var enrollPage = await client.GetPageAsync("/enroll");
        secrets.Add(enrollPage.FormToken);
        using (var options = await client.PostOptionsAsync("/enroll/options", enrollPage, new { token }))
        {
            var credential = authenticator.Create(await options.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            secrets.Add(ClientDataOf(credential));
            (await client.PostFormAsync("/enroll", enrollPage, "enroll", new()
            {
                ["Input.Token"] = token,
                ["Input.Name"] = "Laptop",
                ["Input.Credential.CredentialJson"] = credential,
            })).Dispose();
        }

        (await client.SignInAsync(authenticator)).Dispose();
        (await client.AddPasskeyAsync(authenticator, "Phone")).Dispose();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var applications = scope.ServiceProvider.GetRequiredService<ApplicationCommands>();
            var owner = new ChangeActor(ChangeSource.Dashboard, OwnerSetup.Email);
            await applications.CreateAsync("hogarhero", "HogarHero", owner, TestContext.Current.CancellationToken);
            await applications.CreateEnvironmentAsync("hogarhero", "staging", "Staging", false, owner, TestContext.Current.CancellationToken);
        }

        var keysPage = await client.GetPageAsync("/apps/hogarhero/keys");
        using (var created = await client.PostFormAsync("/apps/hogarhero/keys", keysPage, "create-staging", new() { ["EnvironmentKey"] = "staging", ["KeyName"] = "web" }))
        {
            var shownPage = await client.GetPageAsync(DashboardClient.RedirectPath(created)!);
            var sdkKey = Regex.Match(shownPage.Html, "palanca_sdk_[A-Za-z0-9_-]{43}").Value;
            Assert.NotEmpty(sdkKey);
            secrets.Add(sdkKey);
            using var api = factory.CreateClient();
            api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sdkKey);
            (await api.GetAsync("/api/v1/flags", TestContext.Current.CancellationToken)).Dispose();
        }

        secrets.Add(client.Cookie("__Host-Palanca.Session")!.Value);
        secrets.Add(client.Cookie("__Host-Palanca.Antiforgery")!.Value);

        Assert.NotEmpty(logs.Messages);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(logs.Messages, message => message.Contains(secret, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(logs.Messages, message => message.Contains("clientDataJSON", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message => message.Contains("/enroll#", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailureInProductionAnswersWithoutAnErrorPage()
    {
        var ct = TestContext.Current.CancellationToken;
        var logs = new CapturingLoggerProvider();
        await using var factory = new PalancaFactory
        {
            EnvironmentName = "Production",
            Logs = logs,
            Settings = new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Trace",
            },
        };

        // A real server: the test server would rethrow the failure into the test instead of answering.
        factory.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        factory.StartServer();
        var address = factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.Single();
        string key;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var owner = new ChangeActor(ChangeSource.Dashboard, OwnerSetup.Email);
            var applications = scope.ServiceProvider.GetRequiredService<ApplicationCommands>();
            await applications.CreateAsync("hogarhero", "HogarHero", owner, ct);
            await applications.CreateEnvironmentAsync("hogarhero", "staging", "Staging", false, owner, ct);
            key = (await scope.ServiceProvider.GetRequiredService<SdkKeyCommands>().CreateAsync("hogarhero", "staging", "web", owner, ct)).Key!;
        }

        // A real failure inside the app: the key lookup's table is gone.
        await factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE SdkKeys", ct));
        using var api = new HttpClient { BaseAddress = new Uri(address) };
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await api.GetAsync("/api/v1/flags", ct);

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(ct));
        Assert.DoesNotContain(response.Headers.Concat(response.Content.Headers).SelectMany(h => h.Value), value => value.Contains(key, StringComparison.Ordinal));
        Assert.Contains(logs.Messages, message => message.Contains("no such table: SdkKeys", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message => message.Contains(key, StringComparison.Ordinal));
    }

    private static string ClientDataOf(string credentialJson) =>
        JsonNode.Parse(credentialJson)!["response"]!["clientDataJSON"]!.GetValue<string>();

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Messages);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue($"{category}: {formatter(state, exception)} {exception}");
                if (state is IEnumerable<KeyValuePair<string, object?>> values)
                {
                    foreach (var (key, value) in values)
                    {
                        messages.Enqueue($"{category}: {key}={value}");
                    }
                }
            }
        }
    }
}
