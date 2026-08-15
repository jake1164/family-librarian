using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Feedback;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain;
using FamilyLibrarian.Infrastructure.Acquisition;
using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Infrastructure.Integrations;
using FamilyLibrarian.Infrastructure.Metadata;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Infrastructure.Providers;
using FamilyLibrarian.Infrastructure.Security;
using FamilyLibrarian.Infrastructure.Time;
using System.Net.Http.Headers;
using System.Net.Mail;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("FamilyLibrarian")
            ?? throw new InvalidOperationException(
                "Connection string 'FamilyLibrarian' is required.");

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "identity")));

        services.AddFamilyLibrarianDataProtection(configuration);

        services
            .AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddSignInManager()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        services
            .AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddIdentityCookies();

        // The browser app is a Blazor WebAssembly SPA: there is no server-rendered
        // login page to redirect to, and MapFallbackToFile would answer any such
        // redirect with index.html, so the client would parse HTML as JSON. Always
        // answer auth failures with status codes instead.
        services.ConfigureApplicationCookie(options =>
        {
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };

            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

        // Revalidate the security stamp on every authenticated request, so
        // disabling an account, revoking its Admin role, or resetting its
        // password takes effect immediately rather than whenever the cookie
        // happens to be re-checked.
        //
        // Identity's default is 30 minutes, which would leave a disabled family
        // member signed in for up to half an hour after an administrator removed
        // their access. The cost is one indexed lookup per request; at household
        // scale that is not a trade worth making, and "disabled" meaning
        // "disabled eventually" is not what an administrator clicking Disable
        // expects. Revisit only if this ever serves enough users for the read
        // volume to matter.
        services.Configure<SecurityStampValidatorOptions>(options =>
            options.ValidationInterval = TimeSpan.Zero);

        services.AddAuthorizationBuilder()
            .AddPolicy(
                "Admin",
                policy => policy.RequireRole(RoleNames.Admin));

        services.AddScoped<IClock, SystemClock>();
        services.AddScoped<ICatalogRepository, CatalogRepository>();
        services.AddScoped<CatalogWorkResolver>();
        services.AddScoped<IWorkFormatAvailabilityService, WorkFormatAvailabilityService>();
        services.AddScoped<IWorkFulfillmentOptionsService, WorkFulfillmentOptionsService>();

        services.AddScoped<IRequestRepository, RequestRepository>();
        services.AddScoped<BookRequestService>();

        services.AddScoped<IUserWorkFeedbackRepository, UserWorkFeedbackRepository>();
        services.AddScoped<UserWorkFeedbackService>();

        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.RootPath),
                $"{StorageOptions.SectionName}:RootPath is required.")
            .ValidateOnStart();
        services.AddScoped<IAssetStagingStore, FileSystemAssetStagingStore>();
        services.AddScoped<IAcquisitionRepository, AcquisitionRepository>();

        // Mirrors InvitationPolicy above: a plain settings object, since the
        // Application layer that consumes it takes no dependency on the options
        // packages.
        var manualImportPolicy = new ManualImportPolicy();
        configuration.GetSection(ManualImportPolicy.SectionName).Bind(manualImportPolicy);
        if (!manualImportPolicy.IsValid)
        {
            throw new InvalidOperationException(
                $"{ManualImportPolicy.SectionName} configuration is invalid: MaxUploadSizeBytes must be " +
                "positive and at least one extension must be allowed for each media type.");
        }

        services.AddSingleton(manualImportPolicy);
        services.AddScoped<ManualImportService>();

        services.AddOptions<ClamAvScannerOptions>()
            .Bind(configuration.GetSection(ClamAvScannerOptions.SectionName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Host),
                $"{ClamAvScannerOptions.SectionName}:Host is required.")
            .Validate(
                options => options.Port is > 0 and <= 65_535,
                $"{ClamAvScannerOptions.SectionName}:Port must be a valid port number.")
            .Validate(
                options => options.TimeoutSeconds is >= 1 and <= 300,
                $"{ClamAvScannerOptions.SectionName}:TimeoutSeconds must be between 1 and 300.")
            .ValidateOnStart();
        services.AddSingleton<IMalwareScanner, ClamAvMalwareScanner>();

        services.AddScoped<IAcquisitionBoundaryGuard, AcquisitionBoundaryGuard>();
        services.AddScoped<ISecurityEvaluationRepository, SecurityEvaluationRepository>();
        services.AddScoped<SecurityEvaluationService>();
        services.AddScoped<ApprovalService>();

        // Bound and validated at startup, so a bad LifetimeDays fails the deploy
        // rather than silently issuing invitations that never expire or expire
        // instantly. Registered as the plain policy type because the application
        // layer takes no dependency on the options packages.
        var invitationPolicy = new InvitationPolicy();
        configuration.GetSection(InvitationPolicy.SectionName).Bind(invitationPolicy);
        if (!invitationPolicy.IsValid)
        {
            throw new InvalidOperationException(
                $"{InvitationPolicy.SectionName}:LifetimeDays must be between " +
                $"{InvitationPolicy.MinimumLifetimeDays} and {InvitationPolicy.MaximumLifetimeDays}, " +
                "and RedemptionAttemptsPerMinute must be between 1 and 10000.");
        }

        services.AddSingleton(invitationPolicy);

        services.AddScoped<IInvitationRepository, InvitationRepository>();
        services.AddScoped<IUserAccountStore, IdentityUserAccountStore>();
        services.AddSingleton<IInvitationTokenGenerator, InvitationTokenGenerator>();
        services.AddScoped<InvitationService>();
        services.AddScoped<AccountAdminService>();

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

        // Admin Integrations wiring. The registry is the allowlist of installed
        // providers; enablement and credentials are runtime state, so every
        // provider below is registered unconditionally and filtered per request
        // by ActiveMetadataProviderResolver.
        services.AddSingleton<IProviderRegistry, ProviderRegistry>();
        services.AddSingleton<ICredentialProtector, DataProtectionCredentialProtector>();
        services.AddScoped<IProviderSettingsStore, ProviderSettingsStore>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<MetadataCredentialSource>();
        services.AddSingleton<IMetadataCredentialAccessor, ScopedMetadataCredentialAccessor>();
        services.AddScoped<IActiveMetadataProviderResolver, ActiveMetadataProviderResolver>();
        services.AddScoped<ProviderAdminService>();
        services.AddScoped<MetadataProviderConnectionTester>();

        services.AddSingleton<IBookMetadataProvider, DemoBookMetadataProvider>();

        services.AddOptions<OpenLibraryMetadataOptions>()
            .Bind(configuration.GetSection(OpenLibraryMetadataOptions.SectionName))
            .Validate(options => options.MaxResults is >= 1 and <= 40,
                "Open Library MaxResults must be between 1 and 40.")
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 60,
                "Open Library TimeoutSeconds must be between 1 and 60.")
            .Validate(options => options.RequestsPerSecond is >= 1 and <= 3,
                "Open Library RequestsPerSecond must be between 1 and 3.")
            .Validate(options => string.IsNullOrWhiteSpace(options.ContactEmail) ||
                MailAddress.TryCreate(options.ContactEmail, out _),
                "Open Library ContactEmail must be a valid email address when configured.")
            .Validate(options => options.RequestsPerSecond == 1 ||
                !string.IsNullOrWhiteSpace(options.ContactEmail),
                "Open Library requires ContactEmail when RequestsPerSecond is greater than 1.")
            .ValidateOnStart();

        services.AddSingleton<OpenLibraryRequestGate>();
        services.AddTransient<OpenLibraryRateLimitHandler>();
        services.AddHttpClient<OpenLibraryBookMetadataProvider>((serviceProvider, client) =>
            {
                var options = serviceProvider
                    .GetRequiredService<IOptions<OpenLibraryMetadataOptions>>()
                    .Value;

                client.BaseAddress = new Uri("https://openlibrary.org/");
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.UserAgent.Add(
                    new ProductInfoHeaderValue("FamilyLibrarian", "0.1"));

                if (!string.IsNullOrWhiteSpace(options.ContactEmail))
                {
                    client.DefaultRequestHeaders.UserAgent.Add(
                        new ProductInfoHeaderValue($"({options.ContactEmail})"));
                }
            })
            .AddHttpMessageHandler<OpenLibraryRateLimitHandler>();
        services.AddTransient<IBookMetadataProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<OpenLibraryBookMetadataProvider>());

        services.AddOptions<GoogleBooksMetadataOptions>()
            .Bind(configuration.GetSection(GoogleBooksMetadataOptions.SectionName))
            .Validate(options => options.MaxResults is >= 1 and <= 40,
                "Google Books MaxResults must be between 1 and 40.")
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 60,
                "Google Books TimeoutSeconds must be between 1 and 60.")
            // No longer "required when enabled": the key may instead be stored
            // encrypted in the database through the Admin Integrations page, and
            // enablement itself is now runtime state rather than configuration.
            // ActiveMetadataProviderResolver refuses to query a credentialed
            // provider that has no key from either source.
            .ValidateOnStart();

        services.AddTransient<GoogleBooksApiKeyHandler>();
        // AddLogger<T> resolves T from DI, so it must be registered.
        services.AddSingleton<QueryRedactingHttpClientLogger>();
        services.AddHttpClient<GoogleBooksBookMetadataProvider>((serviceProvider, client) =>
            {
                var options = serviceProvider
                    .GetRequiredService<IOptions<GoogleBooksMetadataOptions>>()
                    .Value;

                client.BaseAddress = new Uri("https://www.googleapis.com/books/v1/");
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.UserAgent.Add(
                    new ProductInfoHeaderValue("FamilyLibrarian", "0.1"));
            })
            .AddHttpMessageHandler<GoogleBooksApiKeyHandler>()
            // By this point the request URI carries ?key=<api key>. The stock
            // loggers already redact query values, but this client is the one
            // holding a secret, so the guarantee is made explicit rather than
            // inherited; see QueryRedactingHttpClientLogger.
            .RemoveAllLoggers()
            .AddLogger<QueryRedactingHttpClientLogger>();
        services.AddTransient<IBookMetadataProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<GoogleBooksBookMetadataProvider>());

        return services;
    }
}
