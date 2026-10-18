using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Palanca.Data;
using Palanca.Security;

namespace Palanca.Owners;

/// <summary>
/// Passkey-only sign-in for the owner with ASP.NET Core Identity (FLG-045 to FLG-048), the dashboard's cookies,
/// and a sign-in requirement on every endpoint that does not opt out (FLG-040).
/// </summary>
internal static class OwnerAuthentication
{
    public const string PasskeyStateCookieName = "__Host-Palanca.Passkey";
    public const string AntiforgeryCookieName = "__Host-Palanca.Antiforgery";
    public const string SignInPath = "/sign-in";
    public const string SignOutPath = "/sign-out";
    public const string DataProtectionKeysSection = "Palanca:DataProtection:KeysDirectory";

    public static IServiceCollection AddOwnerAuthentication(this IServiceCollection services)
    {
        services.AddOptions<DashboardOptions>()
            .BindConfiguration(DashboardOptions.SectionName)
            .Validate(options => options.IsValid(), "Palanca:Dashboard:Origin must be an http or https origin with no path, such as https://palanca.hogarhero.com.")
            .ValidateOnStart();

        services.AddIdentityCore<Owner>()
            .AddUserStore<OwnerStore>()
            .AddSignInManager();
        services.TryAddScoped<IPasskeyHandler<Owner>, PasskeyHandler<Owner>>();

        services.AddOptions<IdentityPasskeyOptions>().Configure<IOptions<DashboardOptions>>((options, dashboard) =>
        {
            var origin = dashboard.Value.NormalizedOrigin;
            options.ServerDomain = dashboard.Value.RelyingPartyId;
            options.UserVerificationRequirement = "required";
            options.ResidentKeyRequirement = "required";

            // Identity's default check also accepts subdomains of the relying party (AC-023).
            options.ValidateOrigin = context => ValueTask.FromResult(IsAcceptedOrigin(origin, context.Origin, context.CrossOrigin));
        });

        services.AddOptions<SecurityStampValidatorOptions>().Configure<TimeProvider>((options, timeProvider) =>
        {
            options.ValidationInterval = TimeSpan.Zero;
            options.TimeProvider = timeProvider;
        });

        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();

        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme).Configure<TimeProvider>((options, timeProvider) =>
        {
            ConfigureHostCookie(options.Cookie, OwnerSession.CookieName);
            options.ExpireTimeSpan = OwnerSession.IdleTimeout;
            options.SlidingExpiration = true;
            options.LoginPath = SignInPath;
            options.LogoutPath = SignOutPath;
            options.ReturnUrlParameter = "returnUrl";
            options.TimeProvider = timeProvider;
            options.Events = new CookieAuthenticationEvents
            {
                OnValidatePrincipal = OwnerSession.ValidatePrincipalAsync,
                OnRedirectToLogin = OwnerSession.RedirectToSignInAsync,
            };
        });

        // Identity keeps the state of a passkey ceremony in this cookie between the options request and the response.
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme).Configure<TimeProvider>((options, timeProvider) =>
        {
            ConfigureHostCookie(options.Cookie, PasskeyStateCookieName);
            options.TimeProvider = timeProvider;
        });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        services.AddAntiforgery(options => ConfigureHostCookie(options.Cookie, AntiforgeryCookieName));

        services.AddDataProtection().SetApplicationName("Palanca");

        // Cookies and passkey state stay valid across restarts only if the keys outlive the container. The
        // directory is read when the options are first used, so configuration added after registration applies.
        services.AddOptions<KeyManagementOptions>().Configure<IConfiguration, IHostEnvironment, ILoggerFactory>(
            (options, configuration, environment, loggerFactory) =>
            {
                if (configuration[DataProtectionKeysSection] is { Length: > 0 } keysDirectory)
                {
                    var directory = new DirectoryInfo(Path.GetFullPath(keysDirectory, environment.ContentRootPath));
                    options.XmlRepository = new FileSystemXmlRepository(directory, loggerFactory);
                }
            });

        services.AddOptions<FailedAuthenticationOptions>()
            .BindConfiguration(FailedAuthenticationOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<FailedAuthenticationLimiter>();
        services.AddScoped<OwnerAccounts>();
        return services;
    }

    /// <summary>Accepts a passkey response only from the dashboard's exact origin, never from a frame (AC-023).</summary>
    public static bool IsAcceptedOrigin(string expectedOrigin, string origin, bool crossOrigin) =>
        !crossOrigin && string.Equals(origin, expectedOrigin, StringComparison.Ordinal);

    // __Host- cookies must be Secure, have Path=/, and carry no Domain.
    private static void ConfigureHostCookie(CookieBuilder cookie, string name)
    {
        cookie.Name = name;
        cookie.HttpOnly = true;
        cookie.SecurePolicy = CookieSecurePolicy.Always;
        cookie.SameSite = SameSiteMode.Strict;
        cookie.Path = "/";
        cookie.Domain = null;
    }
}
