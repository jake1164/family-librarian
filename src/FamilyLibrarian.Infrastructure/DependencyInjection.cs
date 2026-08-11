using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain;
using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Infrastructure.Integrations;
using FamilyLibrarian.Infrastructure.Metadata;
using FamilyLibrarian.Infrastructure.Persistence;
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
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
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

        services.AddAuthorizationBuilder()
            .AddPolicy(
                "Admin",
                policy => policy.RequireRole(RoleNames.Admin));

        services.AddScoped<IClock, SystemClock>();
        services.AddScoped<ICatalogRepository, CatalogRepository>();
        services.AddScoped<CatalogWorkResolver>();

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

        // Admin Integrations wiring. The registry is the allowlist of installed
        // providers; enablement and credentials are runtime state, so every
        // provider below is registered unconditionally and filtered per request
        // by ActiveMetadataProviderResolver.
        services.AddSingleton<IMetadataProviderRegistry, MetadataProviderRegistry>();
        services.AddSingleton<ICredentialProtector, DataProtectionCredentialProtector>();
        services.AddScoped<IMetadataProviderSettingsStore, MetadataProviderSettingsStore>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<MetadataCredentialSource>();
        services.AddSingleton<IMetadataCredentialAccessor, ScopedMetadataCredentialAccessor>();
        services.AddScoped<IActiveMetadataProviderResolver, ActiveMetadataProviderResolver>();
        services.AddScoped<MetadataProviderAdminService>();
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
