using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
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

    // StorageOptions.RootPath defaults to /data/family-librarian — a path that
    // only exists because the Dockerfile creates it and Compose mounts a volume
    // there. Nothing provisions it for a bare `dotnet test` run, on this
    // machine or any other, so every fixture gets its own throwaway directory
    // instead of inheriting the container-only default.
    private readonly string _storageRootPath = Path.Combine(
        Path.GetTempPath(), "family-librarian-tests", Guid.NewGuid().ToString("N"));

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
        ["MetadataProviders__GoogleBooks__ApiKey"] = null,
        ["Storage__RootPath"] = _storageRootPath
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

            // Same posture again: an ordinary test must never depend on CWA or
            // Audiobookshelf being configured, enabled, and passing a test just
            // to create a request. A test that specifically exercises the
            // format-readiness gate overrides this via configureTestServices.
            services.RemoveAll<IFormatReadinessService>();
            services.AddSingleton<IFormatReadinessService, AlwaysReadyFormatReadinessService>();

            // SMTP is configured and probed by individual admin tests. Keep the
            // default probe in-process so the shared fixture never depends on
            // a reachable mail server.
            services.RemoveAll<ISmtpTestSender>();
            services.AddSingleton<ISmtpTestSender, AlwaysSucceedsSmtpTestSender>();

            // Same posture for the two publishing destinations: no ordinary test
            // depends on a reachable CWA or Audiobookshelf instance. Nothing calls
            // these unless a test explicitly configures and enables the
            // destination first, but they're registered unconditionally for the
            // same reason the scanner fake is — a test that does configure one
            // and doesn't care about deep publish behavior gets a safe default
            // rather than a real network/filesystem call.
            services.RemoveAll<ICwaIngestTransportFactory>();
            services.AddSingleton<ICwaIngestTransportFactory, AlwaysSucceedsCwaIngestTransportFactory>();
            services.RemoveAll<ICwaCatalogClient>();
            services.AddSingleton<ICwaCatalogClient, AlwaysEmptyCwaCatalogClient>();
            // Enabling CWA requires a passing connection test for the saved
            // configuration (docs/01 §12.1.1) -- this default-safe double lets an
            // ordinary test reach that state without a reachable CWA instance. A
            // test that specifically exercises a failing/rejected connection test
            // overrides this via configureTestServices, same as the others below.
            services.RemoveAll<ICwaConnectionTester>();
            services.AddSingleton<ICwaConnectionTester, AlwaysSucceedsCwaConnectionTester>();
            services.RemoveAll<IAudiobookshelfApiClient>();
            services.AddSingleton<IAudiobookshelfApiClient, AlwaysEmptyAudiobookshelfApiClient>();

            // The local Project Gutenberg provider is enabled by default, so
            // always replace it in ordinary tests. This lets a test that
            // exercises direct acquisition control the result deterministically.
            services.RemoveAll<IDirectAcquisitionProvider>();
            services.AddSingleton<IDirectAcquisitionProvider, AlwaysEmptyDirectAcquisitionProvider>();

            // OIDC (M6.5): no ordinary test depends on a reachable identity
            // provider. A test that specifically exercises Test Connection can
            // still override this via configureTestServices.
            services.RemoveAll<IOidcDiscoveryTester>();
            services.AddSingleton<IOidcDiscoveryTester, AlwaysSucceedsOidcDiscoveryTester>();

            // External providers (M13): same posture — no ordinary test depends
            // on a reachable external-provider process, even the in-repo sample
            // one. A test that specifically exercises the external-provider
            // acquisition path swaps this via configureTestServices.
            services.RemoveAll<IExternalProviderClient>();
            services.AddSingleton<IExternalProviderClient, AlwaysEmptyExternalProviderClient>();

            // Same posture for repository catalogs: no ordinary test depends on
            // a reachable catalog URL. A test that specifically exercises a
            // catalog fetch swaps this via configureTestServices.
            services.RemoveAll<IProviderCatalogFetcher>();
            services.AddSingleton<IProviderCatalogFetcher, AlwaysFailsProviderCatalogFetcher>();
        });

        if (configureTestServices is not null)
        {
            builder.ConfigureServices(configureTestServices);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            try
            {
                if (Directory.Exists(_storageRootPath))
                {
                    Directory.Delete(_storageRootPath, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort: a leftover temp directory is harmless.
            }
        }
    }
}
