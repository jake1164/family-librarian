using FamilyLibrarian.Application.Acquisition;

namespace FamilyLibrarian.Web.Acquisition;

/// <summary>
/// Binds and validates <see cref="ProviderInteractionAlertOptions"/> at startup —
/// see <c>.ai_docs/human-acq-1-matrix-authorize-plan.md</c> WP2. Lives in
/// <c>FamilyLibrarian.Web</c>, not Infrastructure, because the cross-check against
/// <c>RemoteView:AllowedOrigins</c> and the http-only-in-Development allowance both
/// need host wiring (<see cref="IWebHostEnvironment"/>) that Infrastructure's
/// <c>AddInfrastructure</c> is not given.
/// </summary>
public static class ProviderInteractionAlertStartup
{
    /// <summary>
    /// Binds <c>Interaction</c> configuration and registers it as a singleton.
    /// Throws if <see cref="ProviderInteractionAlertOptions.PublicOrigin"/> is set
    /// but is not a bare absolute origin, is not <c>https</c> (except in
    /// Development), or is not also listed in <c>RemoteView:AllowedOrigins</c> —
    /// the alert's magic link opens a WebSocket to this same host, so an origin the
    /// WebSocket upgrade would reject can never actually work.
    /// </summary>
    public static IServiceCollection AddProviderInteractionAlertOptions(
        this IServiceCollection services, IConfiguration configuration, bool isDevelopment)
    {
        var options = new ProviderInteractionAlertOptions();
        configuration.GetSection(ProviderInteractionAlertOptions.SectionName).Bind(options);

        if (!string.IsNullOrWhiteSpace(options.PublicOrigin))
        {
            ValidatePublicOrigin(options.PublicOrigin, isDevelopment, configuration);
        }

        return services.AddSingleton(options);
    }

    private static void ValidatePublicOrigin(string publicOrigin, bool isDevelopment, IConfiguration configuration)
    {
        if (!Uri.TryCreate(publicOrigin, UriKind.Absolute, out var uri) ||
            uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new InvalidOperationException(
                $"{ProviderInteractionAlertOptions.SectionName}:PublicOrigin ('{publicOrigin}') must be a bare " +
                "origin with no path, query, or fragment (e.g. https://fl.example.com).");
        }

        var schemeIsAllowed = uri.Scheme == Uri.UriSchemeHttps || (isDevelopment && uri.Scheme == Uri.UriSchemeHttp);
        if (!schemeIsAllowed)
        {
            throw new InvalidOperationException(
                $"{ProviderInteractionAlertOptions.SectionName}:PublicOrigin ('{publicOrigin}') must use https " +
                "(http is only accepted in Development).");
        }

        var allowedOrigins = configuration.GetSection("RemoteView:AllowedOrigins").Get<string[]>() ?? [];
        var origin = uri.GetLeftPart(UriPartial.Authority);
        if (!allowedOrigins.Any(allowed => string.Equals(allowed.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"{ProviderInteractionAlertOptions.SectionName}:PublicOrigin ('{origin}') must also be listed in " +
                "RemoteView:AllowedOrigins, or the magic link's remote-view WebSocket upgrade would be rejected.");
        }
    }

    /// <summary>Logs a warning, once at startup, if Matrix verification alerts are disabled.</summary>
    public static void WarnIfInteractionAlertsAreDisabled(this IServiceProvider services)
    {
        var options = services.GetRequiredService<ProviderInteractionAlertOptions>();
        if (!options.IsEnabled)
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("FamilyLibrarian.Interaction");
            ProviderInteractionAlertLog.AlertsDisabled(logger);
        }
    }
}

internal static partial class ProviderInteractionAlertLog
{
    [LoggerMessage(
        EventId = 3101,
        Level = LogLevel.Warning,
        Message = "Matrix verification alerts are disabled: Interaction:PublicOrigin is not set.")]
    internal static partial void AlertsDisabled(ILogger logger);
}
