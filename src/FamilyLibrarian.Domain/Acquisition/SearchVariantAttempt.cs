namespace FamilyLibrarian.Domain.Acquisition;

/// <summary>
/// What is remembered about one search variant for one requested format and provider: when it was first and last tried, how
/// many results it returned last time, and whether it found the candidate that was downloaded or put first in review.
/// </summary>
/// <remarks>
/// The memory is "last tried at", not "done forever": a variant that found nothing at release may succeed later, so the
/// next scheduled recheck runs the whole list again. The kind is stored by name (the application's search-variant kinds);
/// the key is the normalized value of the one dimension the variant changed, empty for the base search.
/// </remarks>
public sealed class SearchVariantAttempt
{
    public const int MaxKindLength = 32;
    public const int MaxKeyLength = 256;

    private SearchVariantAttempt()
    {
    }

    public SearchVariantAttempt(
        Guid requestFormatId, string providerId, string variantKind, string variantKey, DateTimeOffset triedAtUtc, int resultCount)
    {
        if (requestFormatId == Guid.Empty) throw new ArgumentException("A request format id is required.", nameof(requestFormatId));
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(variantKind);
        ArgumentNullException.ThrowIfNull(variantKey);
        if (variantKind.Length > MaxKindLength || variantKey.Length > MaxKeyLength)
            throw new ArgumentException("A search variant identifier exceeds its stored length.");
        ArgumentOutOfRangeException.ThrowIfNegative(resultCount);

        RequestFormatId = requestFormatId;
        ProviderId = providerId.Trim().ToLowerInvariant();
        VariantKind = variantKind;
        VariantKey = variantKey;
        FirstTriedAtUtc = triedAtUtc;
        LastTriedAtUtc = triedAtUtc;
        LastResultCount = resultCount;
        TimesTried = 1;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid RequestFormatId { get; private set; }

    public string ProviderId { get; private set; } = null!;

    public string VariantKind { get; private set; } = null!;

    public string VariantKey { get; private set; } = null!;

    public DateTimeOffset FirstTriedAtUtc { get; private set; }

    public DateTimeOffset LastTriedAtUtc { get; private set; }

    /// <summary>The number of results this variant returned the last time it ran, before de-duplication across variants.</summary>
    public int LastResultCount { get; private set; }

    public int TimesTried { get; private set; }

    /// <summary>When the candidate downloaded, or put first in review, came from this variant; null if it never has.</summary>
    public DateTimeOffset? FoundSelectedAtUtc { get; private set; }

    public void RecordTry(DateTimeOffset triedAtUtc, int resultCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(resultCount);
        LastTriedAtUtc = triedAtUtc;
        LastResultCount = resultCount;
        TimesTried++;
    }

    public void MarkFoundSelected(DateTimeOffset atUtc) => FoundSelectedAtUtc = atUtc;
}
