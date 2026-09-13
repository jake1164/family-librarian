using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Catalog;

/// <summary>
/// Reports whether a Work's ebook is already present in the configured CWA
/// library, reusing the same OPDS lookup <c>CwaPublishingService</c> uses to
/// verify a just-published file landed.
/// </summary>
/// <remarks>
/// Read-only: this never triggers a publish, never mutates anything. "Not
/// found" (wrong media type, CWA not configured/enabled, or no catalog
/// match) is always an empty list, never an error — matching the rest of
/// this pipeline's "not found is a normal outcome" posture.
/// </remarks>
public sealed class CwaOwnedLibraryProvider(
    ICwaSettingsStore settingsStore,
    ICwaCatalogClient catalogClient,
    IWorkLookup workLookup) : IOwnedLibraryProvider
{
    public string Id => "cwa";

    public async Task<IReadOnlyList<FulfillmentOption>> FindOwnedMatchesAsync(
        Guid workId, RequestMediaType mediaType, CancellationToken cancellationToken)
    {
        if (mediaType != RequestMediaType.Ebook)
        {
            return [];
        }

        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is null || !settings.IsEnabled || string.IsNullOrWhiteSpace(settings.OpdsBaseUrl))
        {
            return [];
        }

        var work = await workLookup.FindAsync(workId, cancellationToken);
        if (work is null)
        {
            return [];
        }

        var identity = new BookIdentity(work.Title, work.PrimaryAuthor, work.Isbn13s);
        var options = await MatchAsync(identity, settings, cancellationToken);
        return options.Select(option => option with { WorkId = workId }).ToArray();
    }

    public async Task<IReadOnlyList<FulfillmentOption>> FindOwnedMatchesAsync(
        BookIdentity identity, RequestMediaType mediaType, CancellationToken cancellationToken)
    {
        if (mediaType != RequestMediaType.Ebook)
        {
            return [];
        }

        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is null || !settings.IsEnabled || string.IsNullOrWhiteSpace(settings.OpdsBaseUrl))
        {
            return [];
        }

        return await MatchAsync(identity, settings, cancellationToken);
    }

    private async Task<IReadOnlyList<FulfillmentOption>> MatchAsync(
        BookIdentity identity, Domain.Publishing.CwaSettings settings, CancellationToken cancellationToken)
    {
        // No specific accepted format is in play for a generic Work-level
        // ownership check, so the ordinary English-or-unspecified filter
        // applies unmodified -- see IBookMatcher.ResolveUnique.
        var result = await catalogClient.FindBookIdAsync(
            identity.Title, identity.Author, identity.Isbn13Candidates, cancellationToken, acceptedLanguage: null);

        if (result.Decision == BookMatchDecision.Match)
        {
            var bookId = result.MatchedId!;
            return
            [
                new FulfillmentOption(
                    ProviderId: Id,
                    ProviderResultId: bookId,
                    WorkId: Guid.Empty,
                    EditionId: null,
                    MediaType: RequestMediaType.Ebook,
                    OptionKind: OptionKind.Owned,
                    AcquisitionMethod: AcquisitionMethod.OwnedImport,
                    Format: null,
                    Language: null,
                    Quality: null,
                    Availability: null,
                    Cost: null,
                    Currency: null,
                    LicenseOrUsageStatus: null,
                    DrmStatus: null,
                    ExternalActionUri: ExternalLibraryLinks.BuildCwaBookLink(settings, bookId),
                    ProviderData: null,
                    MatchBasis: result.Basis)
            ];
        }

        if (result.Decision is BookMatchDecision.Ambiguous or BookMatchDecision.LanguageExcluded)
        {
            // Found something, but not confidently enough to call it "owned"
            // outright -- surfaced as informational candidates instead of a
            // silently empty list (P2 in alpha2-review-2026-09-12.md), so an
            // admin looking at this Work's fulfillment options can at least
            // see what CWA already has. RequiresLanguageConfirmation flags a
            // LanguageExcluded result specifically -- neither ever
            // auto-acquires, since this pipeline is display-only.
            return result.Candidates
                .Select(candidate => new FulfillmentOption(
                    ProviderId: Id,
                    ProviderResultId: candidate.ExternalId,
                    WorkId: Guid.Empty,
                    EditionId: null,
                    MediaType: RequestMediaType.Ebook,
                    OptionKind: OptionKind.Owned,
                    AcquisitionMethod: AcquisitionMethod.OwnedImport,
                    Format: null,
                    Language: candidate.Language,
                    Quality: null,
                    Availability: null,
                    Cost: null,
                    Currency: null,
                    LicenseOrUsageStatus: null,
                    DrmStatus: null,
                    ExternalActionUri: ExternalLibraryLinks.BuildCwaBookLink(settings, candidate.ExternalId),
                    ProviderData: null,
                    MatchBasis: result.Basis,
                    RequiresLanguageConfirmation: result.Decision == BookMatchDecision.LanguageExcluded,
                    Title: candidate.Title,
                    Author: candidate.Author))
                .ToArray();
        }

        return [];
    }
}
