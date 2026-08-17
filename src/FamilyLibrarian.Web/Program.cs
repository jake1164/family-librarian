using System.Threading.RateLimiting;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Infrastructure;
using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Infrastructure.Providers;
using FamilyLibrarian.Infrastructure.Security;
using FamilyLibrarian.Web;
using FamilyLibrarian.Web.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("postgresql")
    .AddCheck<SecurityScannerHealthCheck>("malware-scanner");

// Invitation redemption is necessarily anonymous, which makes it the one write
// endpoint an unauthenticated caller can reach. The token is 256 bits, so this
// is not what stops a guess succeeding — it stops a client burning host
// resources trying, and it bounds the damage if a token ever leaks into a log.
var redemptionAttemptsPerMinute = builder.Configuration
    .GetSection(InvitationPolicy.SectionName)
    .GetValue("RedemptionAttemptsPerMinute", InvitationPolicy.DefaultRedemptionAttemptsPerMinute);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(InvitationEndpoints.RateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            // Partitioned by caller so one busy address cannot lock everyone
            // else out of redeeming their invitation. Note that a household
            // behind one NAT address shares a bucket, which is why the ceiling
            // is configurable rather than fixed.
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = redemptionAttemptsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

// The WebAssembly client cannot read an HttpOnly cookie, so the request token
// travels in a header it sets explicitly. The cookie half stays HttpOnly.
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = AntiforgeryTokenEndpoint.HeaderName;
    options.Cookie.Name = AntiforgeryTokenEndpoint.CookieName;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    // Secure on HTTPS, absent on plain-HTTP local development, so the same
    // configuration works behind a TLS-terminating proxy and on localhost.
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

var app = builder.Build();

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await app.Services.MigrateDatabaseAsync();
    return;
}

// Refuses to serve at all rather than silently accept every uploaded or
// acquired file as validly formatted — see F1 in the architecture review.
// Not required for --migrate above: a schema-only run never touches the
// security pipeline.
app.Services.EnsureAssetValidatorsAreConfigured();

if (app.Configuration.GetValue<bool>("Authentication:EnableLocal"))
{
    await app.Services.InitializeIdentityAsync(app.Configuration);
}

// Loads whatever OIDC settings are already saved (or the disabled default on
// a fresh install) into the in-memory cache the "oidc" scheme reads from —
// see OidcOptionsConfigurator's remarks for why this can't be read from
// configuration at Build() time the way the connection string is.
await app.Services.InitializeOidcRuntimeCacheAsync();

// Same reasoning as the OIDC cache above, for the private-egress gateway
// PrivateEgressRouteResolver reads from.
await app.Services.InitializeGatewayRuntimeCacheAsync();

app.UseExceptionHandler("/error");

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

app.UseHttpsRedirection();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseRateLimiter();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
    .AllowAnonymous();
app.MapHealthChecks("/health/ready");

// One call per feature area. Each Map* method owns its own route group, its
// authorization and anti-forgery requirements, and its handlers — so the
// authorization posture of an area is readable in one place instead of being
// split between a route table here and handlers hundreds of lines away.
app.MapAuthenticationEndpoints();
app.MapCurrentUserEndpoints();
app.MapCatalogEndpoints();
app.MapRequestEndpoints();
app.MapAdminRequestEndpoints();
app.MapFeedbackEndpoints();
app.MapSecurityQueueEndpoints();
app.MapInvitationEndpoints();
app.MapAccountEndpoints();
app.MapMetadataIntegrationEndpoints();
app.MapCwaSettingsEndpoints();
app.MapAudiobookshelfSettingsEndpoints();
app.MapPublishingQueueEndpoints();
app.MapPolicyEndpoints();
app.MapOidcSettingsEndpoints();
app.MapExternalProviderEndpoints();
app.MapPrivateEgressGatewayEndpoints();
app.MapProviderCatalogEndpoints();

app.MapFallbackToFile("index.html");

await app.RunAsync();
