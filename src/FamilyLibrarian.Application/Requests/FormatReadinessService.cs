using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Requests;

/// <summary>
/// Whether a user may request a given format right now: its destination is
/// configured and passing its connection test, and the required security
/// scanner is healthy. Catalog browsing and existing request history do not
/// depend on this — only creating a new request for the format does.
/// </summary>
public interface IFormatReadinessService
{
    Task<FormatReadiness> CheckAsync(RequestMediaType mediaType, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IsEnabled"/> is the admin's on/off switch for the format's
/// destination; <see cref="IsReady"/> additionally requires it to be configured,
/// passing its test, and the scanner to be healthy. A format that is enabled but
/// not ready is temporarily unavailable; one that is not enabled is not offered.
/// </summary>
public sealed record FormatReadiness(bool IsReady, string? Reason, bool IsEnabled = true)
{
    public static FormatReadiness Ready { get; } = new(true, null);

    public static FormatReadiness NotReady(string reason) => new(false, reason);

    public static FormatReadiness Disabled(string reason) => new(false, reason, IsEnabled: false);
}

public sealed class FormatReadinessService(
    CwaSettingsService cwaSettings,
    AudiobookshelfSettingsService audiobookshelfSettings,
    IAcquisitionBoundaryGuard boundaryGuard) : IFormatReadinessService
{
    public async Task<FormatReadiness> CheckAsync(RequestMediaType mediaType, CancellationToken cancellationToken)
    {
        // Enabled is checked first: a format nobody turned on is not offered at
        // all, so it must not be reported as a scanner problem.
        var (isEnabled, disabledReason) = mediaType switch
        {
            RequestMediaType.Ebook => (await cwaSettings.IsEnabledAsync(cancellationToken), "CWA is not enabled."),
            RequestMediaType.Audiobook => (await audiobookshelfSettings.IsEnabledAsync(cancellationToken), "Audiobookshelf is not enabled."),
            _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, "Unknown request media type.")
        };
        if (!isEnabled)
        {
            return FormatReadiness.Disabled(disabledReason);
        }

        if (!await boundaryGuard.CanAcceptNewArtifactAsync(cancellationToken))
        {
            return FormatReadiness.NotReady("The security scanner is currently unavailable.");
        }

        var destinationError = mediaType switch
        {
            RequestMediaType.Ebook => await cwaSettings.GetRequestReadinessErrorAsync(cancellationToken),
            RequestMediaType.Audiobook => await audiobookshelfSettings.GetRequestReadinessErrorAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, "Unknown request media type.")
        };

        return destinationError is null ? FormatReadiness.Ready : FormatReadiness.NotReady(destinationError);
    }
}
