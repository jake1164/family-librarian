namespace FamilyLibrarian.Contracts.Acquisition;

/// <summary>
/// The magic-link page's claim request. The token travels in the URL
/// <em>fragment</em>, never the path or query (HUMAN-ACQ-1 D11) — the page reads
/// it client-side and posts it here as an ordinary JSON body.
/// </summary>
public sealed record ClaimInteractionLinkRequest(string Token);

/// <summary>A successful claim. Carries nothing the browser could not already infer from having the token.</summary>
public sealed record ClaimInteractionLinkResponse(
    Guid JobId,
    string? WorkTitle,
    string ProviderDisplayName,
    DateTimeOffset GrantExpiresAtUtc);

/// <summary>Polled by the magic-link page while it waits for the remote view to become ready.</summary>
public sealed record InteractionLinkStatusResponse(string State, string? Message);
