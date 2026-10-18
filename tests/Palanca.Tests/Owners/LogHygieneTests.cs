using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Palanca.Applications;
using Palanca.Data;

namespace Palanca.Tests.Owners;

/// <summary>Logs never carry an enrollment link, a passkey response, or a cookie (AC-011).</summary>
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
                ["Logging:LogLevel:Default"] = "Trace",
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
