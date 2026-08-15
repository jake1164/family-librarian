using FamilyLibrarian.Application.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>
/// Boots the real host against a throwaway database.
/// </summary>
/// <remarks>
/// Nothing about authentication, authorization, or the anti-forgery pipeline is
/// stubbed. These tests exist to prove the production wiring denies the right
/// callers, so replacing any part of it would test the substitute instead.
/// </remarks>
internal sealed class FamilyLibrarianAppFactory(
    string connectionString,
    Action<IServiceCollection>? configureTestServices = null)
    : WebApplicationFactory<global::Program>
{
    internal const string AdminEmail = "admin@family-librarian.example";
    internal const string AdminPassword = "Bootstrap-Admin-Pass1!";

    /// <summary>
    /// Settings the host must see, as environment variables.
    /// </summary>
    /// <remarks>
    /// Not <c>ConfigureAppConfiguration</c>: those callbacks are applied when the
    /// host is built, but <c>Program.cs</c> reads the connection string while
    /// composing services — before <c>Build()</c> — so they arrive too late and
    /// startup fails with "Connection string 'FamilyLibrarian' is required".
    /// <c>WebApplication.CreateBuilder</c> reads environment variables during
    /// <c>CreateBuilder</c>, which is early enough. A double underscore is the
    /// configuration section separator — except <c>Admin_Email</c>/<c>Admin_Password</c>,
    /// which <c>IdentityInitializer</c> reads as flat keys, not through a nested
    /// options class, so a single underscore is enough.
    /// </remarks>
    private Dictionary<string, string?> HostVariables() => new(StringComparer.Ordinal)
    {
        // Not "Development": that branch calls UseWebAssemblyDebugging, which wants
        // a debugging proxy no test needs.
        ["ASPNETCORE_ENVIRONMENT"] = "Testing",
        ["ConnectionStrings__FamilyLibrarian"] = connectionString,
        ["Authentication__EnableLocal"] = "true",
        ["Admin_Email"] = AdminEmail,
        ["Admin_Password"] = AdminPassword,
        // Every test reaches the host from the same address, so they share one
        // rate-limit bucket. Raised here so the suite exercises the invitation
        // rules rather than the limiter; the limiter's own ceiling is a
        // deployment setting, not behaviour these tests are asserting.
        ["Invitations__RedemptionAttemptsPerMinute"] = "10000",
        // Keep every outbound provider off. A test must never depend on Open
        // Library or Google Books being reachable.
        ["MetadataProviders__Demo__Enabled"] = "true",
        ["MetadataProviders__OpenLibrary__Enabled"] = "false",
        ["MetadataProviders__GoogleBooks__Enabled"] = "false",
        // Leave the deployment-supplied Google Books key unset, so the provider is
        // credential-managed through the admin surface rather than externally
        // managed. Clearing it defends against a developer's own shell exporting one.
        ["MetadataProviders__GoogleBooks__ApiKey"] = null
    };

    /// <summary>
    /// Applies the host settings only for the moment the entry point runs, then
    /// restores them, so one fixture's connection string cannot leak into another's.
    /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var variables = HostVariables();
        var previous = variables.Keys.ToDictionary(
            key => key,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

        foreach (var (key, value) in variables)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            return base.CreateHost(builder);
        }
        finally
        {
            foreach (var (key, value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Ordinary tests must never depend on a reachable ClamAV instance,
        // mirroring how metadata providers default to the safe in-process demo
        // provider above. A test that specifically exercises the security gate
        // (see SecurityGateEndpointTests) overrides this via configureTestServices,
        // which runs after this and so wins.
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMalwareScanner>();
            services.AddSingleton<IMalwareScanner, AlwaysCleanTestMalwareScanner>();
        });

        if (configureTestServices is not null)
        {
            builder.ConfigureServices(configureTestServices);
        }
    }
}
