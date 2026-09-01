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

public sealed record FormatReadiness(bool IsReady, string? Reason)
{
    public static FormatReadiness Ready { get; } = new(true, null);

    public static FormatReadiness NotReady(string reason) => new(false, reason);
}

public sealed class FormatReadinessService(
    CwaSettingsService cwaSettings,
    AudiobookshelfSettingsService audiobookshelfSettings,
    IAcquisitionBoundaryGuard boundaryGuard) : IFormatReadinessService
{
    public async Task<FormatReadiness> CheckAsync(RequestMediaType mediaType, CancellationToken cancellationToken)
    {
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
