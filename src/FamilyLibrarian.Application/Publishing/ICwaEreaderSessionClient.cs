using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Application.Publishing;

/// <summary>
/// Signs in to CWA's own web application as a dedicated service account and
/// invokes its real "send to e-reader" route -- CWA's send-to-Kindle feature
/// is a stateful, CSRF-protected Flask-Login session flow, not a simple
/// authenticated API call (unlike <see cref="ICwaCatalogClient"/>'s OPDS Basic
/// Auth or <c>ICwaIngestTransport</c>'s filesystem/SFTP handoff). Every call
/// performs its own fresh login -- no session is cached across calls -- which
/// trades a little overhead for trivial concurrency safety: two deliveries in
/// flight at once never share cookie/CSRF state.
/// </summary>
public interface ICwaEreaderSessionClient
{
    /// <summary>
    /// Signs in as <paramref name="serviceAccountUsername"/> and asks CWA to
    /// email <paramref name="cwaBookId"/> to <paramref name="recipientEmail"/>.
    /// Never throws for an ordinary failure -- see <see cref="CwaEreaderSendResult"/>.
    /// </summary>
    Task<CwaEreaderSendResult> SendSelectedAsync(
        CwaSettings settings,
        string serviceAccountUsername,
        string serviceAccountPassword,
        string cwaBookId,
        string bookFormat,
        bool convert,
        string recipientEmail,
        CancellationToken cancellationToken);

    /// <summary>
    /// A login-only check -- proves the service account can authenticate and
    /// reach an authenticated page, without ever calling the send route.
    /// Used by "Test Kindle Delivery."
    /// </summary>
    Task<CwaEreaderLoginResult> TestLoginAsync(
        CwaSettings settings,
        string serviceAccountUsername,
        string serviceAccountPassword,
        CancellationToken cancellationToken);
}

public enum CwaEreaderSendStatus
{
    Success,

    /// <summary>The service account could not sign in (bad credentials, or an unexpected response).</summary>
    LoginFailed,

    /// <summary>
    /// CWA responded HTTP 200 with a <c>type: "danger"</c> JSON body -- it
    /// understood the request and declined it (e.g. its own SMTP isn't
    /// configured, or the format/convert combination isn't sendable).
    /// </summary>
    SendRejected,

    /// <summary>Network/timeout/unexpected-status/unparseable-response failure.</summary>
    TransportFailure
}

/// <summary>
/// <paramref name="CwaMessage"/> is CWA's own vocabulary (its JSON <c>message</c>
/// field), confined to this session-client layer -- the caller decides what,
/// if anything, of that text survives into the delivery-layer outcome it maps to.
/// </summary>
public sealed record CwaEreaderSendResult(CwaEreaderSendStatus Status, string? CwaMessage);

public sealed record CwaEreaderLoginResult(bool Succeeded, string Message);
