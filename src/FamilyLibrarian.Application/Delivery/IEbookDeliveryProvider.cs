using FamilyLibrarian.Application.Publishing;

namespace FamilyLibrarian.Application.Delivery;

/// <summary>
/// Delivers an already-catalogued ebook to an arbitrary recipient address (a
/// family member's e-reader) via a publishing destination's own reader-send
/// capability. Distinct from <see cref="Catalog.IOwnedLibraryProvider"/>,
/// which only reports whether a copy already exists -- this actually ships
/// the file. Nothing wires this into the automatic request-fulfillment flow
/// yet (that is KINDLE-5); today it is reachable directly and via the
/// "Test Kindle Delivery" action.
/// </summary>
public interface IEbookDeliveryProvider
{
    /// <summary>Stable id matching this provider's destination -- "cwa" for the CWA provider.</summary>
    string Id { get; }

    /// <summary>
    /// Whether this provider has enough saved configuration to attempt a
    /// delivery right now. Independent of the destination's own
    /// ingest/OPDS "enabled" gate. Never throws.
    /// </summary>
    Task<bool> CanDeliverAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends the book identified by <paramref name="providerBookId"/> (this
    /// provider's own book id -- for CWA, <c>LibraryImport.ExternalBookId</c>)
    /// to <paramref name="recipientEmail"/>. Never throws for an ordinary
    /// delivery failure -- see <see cref="EbookDeliveryOutcome"/>.
    /// </summary>
    Task<EbookDeliveryOutcome> DeliverAsync(
        string providerBookId,
        string bookFormat,
        bool convert,
        string recipientEmail,
        CancellationToken cancellationToken);

    /// <summary>
    /// A login/connectivity check only -- proves the shared service account
    /// can authenticate, without sending a real book (the destination's send
    /// route has no dry-run and no fixture test-book is seeded). Backs the
    /// "Test Kindle Delivery" action.
    /// </summary>
    Task<ConnectionTestOutcome> TestAsync(CancellationToken cancellationToken);
}
