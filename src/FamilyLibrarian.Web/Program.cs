using System.Threading.RateLimiting;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Infrastructure;
using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Infrastructure.Integrations;
using FamilyLibrarian.Infrastructure.Providers;
using FamilyLibrarian.Infrastructure.Security;
using FamilyLibrarian.Web.Acquisition;
using FamilyLibrarian.Web;
using FamilyLibrarian.Web.Endpoints;
using FamilyLibrarian.Web.Publishing;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHostedService<CwaVerificationHostedService>();
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<AutomaticRequestFulfillmentHostedService>();
}
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("postgresql")
    .AddCheck<SecurityScannerHealthCheck>("malware-scanner");

// The forwarded-headers middleware only trusts a caller-supplied X-Forwarded-For
// from an address in KnownProxies/KnownNetworks; both default to loopback only.
// A self-hosted deployment's reverse proxy is rarely loopback, so it must be
// named explicitly — there is deliberately no insecure "trust everything"
// fallback here (that used to be ASPNETCORE_FORWARDEDHEADERS_ENABLED=true in
// the image, which clears both lists and lets any caller spoof its own address
// into RemoteIpAddress). Unconfigured, only the built-in loopback default
// applies — a reverse proxy on another host or container needs this set.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};

foreach (var cidr in builder.Configuration.GetSection("ReverseProxy:TrustedNetworks").Get<string[]>() ?? [])
{
    try
    {
        // Fully qualified: Microsoft.AspNetCore.HttpOverrides also declares an
        // IPNetwork (the now-obsolete type KnownNetworks used), so the bare
        // name is ambiguous with that using directive in scope.
        forwardedHeadersOptions.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
    }
    catch (Exception exception) when (exception is FormatException or ArgumentOutOfRangeException)
    {
        throw new InvalidOperationException(
            $"ReverseProxy:TrustedNetworks entry '{cidr}' is not a valid CIDR network.", exception);
    }
}

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

    // A second, unpartitioned ceiling well above any single caller's own
    // limit. The per-caller policy above is only as strong as its partition
    // key; this one holds regardless of what RemoteIpAddress turns out to be,
    // so it does not depend on the ReverseProxy configuration above being
    // correct for every deployment topology.
    options.AddFixedWindowLimiter(InvitationEndpoints.GlobalRateLimitPolicy, limiterOptions =>
    {
        limiterOptions.PermitLimit = Math.Max(redemptionAttemptsPerMinute * 20, 100);
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
    });
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

// Backs app.UseExceptionHandler() below — writes an RFC 7807 ProblemDetails
// body for any response that reaches the client with an error status and no
// body content yet, unhandled exceptions included.
builder.Services.AddProblemDetails();

builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromHours(1));

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

// F4: a soft warning, not a startup guard — see WarnIfKeyRingIsUnprotected's
// own remarks for why an unconfigured certificate does not fail closed here.
app.Services.WarnIfKeyRingIsUnprotected();

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

// First: everything below — HTTPS redirection, the auth cookie's Secure flag,
// the rate limiter's per-caller partition key — depends on seeing the
// original client address and scheme, not the reverse proxy's.
app.UseForwardedHeaders(forwardedHeadersOptions);

// The parameterless overload writes a ProblemDetails response through
// AddProblemDetails() directly; it does not re-execute the pipeline at a
// route. The previous app.UseExceptionHandler("/error") did exactly that
// against a path nothing ever mapped, so every unhandled exception fell
// through to MapFallbackToFile("index.html") and the client tried to parse
// the SPA's HTML shell as JSON — see F8 in the architecture review.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

app.UseHttpsRedirection();

if (!app.Environment.IsDevelopment())
{
    // A short MaxAge deliberately: HSTS is a one-way commitment per
    // Microsoft's own guidance — once a browser receives it, that browser
    // refuses plain HTTP to this host until the MaxAge expires, even for a
    // LAN deployment that later needs to drop TLS. Raise it only once this
    // deployment is confirmed to always be HTTPS-reachable.
    app.UseHsts();
}

app.UseSecurityHeaders();
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
app.MapAdminTasksEndpoints();
app.MapNotificationEndpoints();
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
app.MapSettingsBackupEndpoints();

app.MapFallbackToFile("index.html");

await app.RunAsync();
