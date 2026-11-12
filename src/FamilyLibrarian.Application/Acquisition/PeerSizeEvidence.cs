using FamilyLibrarian.Application.Catalog;

namespace FamilyLibrarian.Application.Acquisition;

public sealed record PeerSizeEvidence(long MedianSizeBytes, int Count)
{
    /// <summary>The caller supplies the pooled candidates that passed requester review eligibility.</summary>
    public static PeerSizeEvidence? From(FulfillmentOption chosen, IReadOnlyList<FulfillmentOption> reviewablePool)
    {
        if (string.IsNullOrWhiteSpace(chosen.Format)) return null;
        var format = Normalize(chosen.Format);
        var sizes = reviewablePool.Where(peer => peer.ProviderResultId != chosen.ProviderResultId &&
                peer.MediaType == chosen.MediaType && string.Equals(peer.ProviderId, chosen.ProviderId, StringComparison.Ordinal) &&
                peer.SizeBytes is > 0 && string.Equals(Normalize(peer.Format), format, StringComparison.OrdinalIgnoreCase))
            .Select(peer => peer.SizeBytes!.Value).Order().ToArray();
        if (sizes.Length < 3) return null;
        var middle = sizes.Length / 2;
        var median = sizes.Length % 2 == 1 ? sizes[middle] : sizes[middle - 1] + (sizes[middle] - sizes[middle - 1]) / 2;
        return new(median, sizes.Length);
    }

    private static string? Normalize(string? format) => format?.Trim().TrimStart('.');
}
