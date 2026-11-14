using System.Globalization;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog;

/// <summary>
/// Read-only author and series browsing. Saved pages read the Family Librarian catalog by stable id; observed
/// previews describe records a source already returned to the caller's own search. Nothing here calls a metadata
/// provider, resolves a candidate, or writes.
/// </summary>
/// <remarks>
/// Catalog facts are shared among signed-in users; the temporary observations handed to the preview methods are
/// resolved (and owner-checked) by the host before they arrive. This class owns matching, ordering, grouping,
/// paging and limitation codes so the web layer only maps results to contracts.
/// <para>
/// Matching an observed label to saved names is a navigation aid. It never merges, redirects or attributes a
/// provider record to a saved person or series: every saved namesake is returned as a separate choice.
/// </para>
/// </remarks>
public sealed class CatalogBrowseService(ICatalogBrowseRepository repository, ICurrentUser currentUser)
{
    public const int PageSize = 50;
    public const int MaximumPage = 100;
    public const int MaximumNameChoices = 20;

    public static bool IsValidPage(int page) => page is >= 1 and <= MaximumPage;

    public async Task<CatalogBrowseResult<BrowseAuthorPage>> GetAuthorAsync(
        Guid authorId, int page, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null) return new CatalogBrowseResult<BrowseAuthorPage>(CatalogBrowseStatus.Unauthenticated);
        if (!IsValidPage(page)) return new CatalogBrowseResult<BrowseAuthorPage>(CatalogBrowseStatus.InvalidPage);
        var header = await repository.FindAuthorAsync(authorId, cancellationToken);
        if (header is null) return new CatalogBrowseResult<BrowseAuthorPage>(CatalogBrowseStatus.NotFound);

        var works = await repository.GetAuthorWorksAsync(authorId, (page - 1) * PageSize, PageSize + 1, cancellationToken);
        var hasMore = works.Count > PageSize;
        var shown = works.Take(PageSize).ToArray();

        var entries = new List<(Guid? SeriesId, string Name, BrowseEntry Entry)>();
        var ungrouped = new List<BrowseEntry>();
        foreach (var work in shown)
        {
            var book = ToBook(work);
            if (work.Memberships.Count == 0)
            {
                ungrouped.Add(new BrowseEntry(book, null, null));
                continue;
            }

            foreach (var membership in work.Memberships)
            {
                entries.Add((membership.SeriesId, membership.SeriesName,
                    new BrowseEntry(book, membership.PositionLabel, membership.PositionSort)));
            }
        }

        var groups = entries
            .GroupBy(item => item.SeriesId)
            .Select(group => new BrowseGroup(
                group.Key,
                null,
                group.First().Name,
                SortEntries(group.Select(item => item.Entry))))
            .OrderBy(group => group.Name, StringComparer.Ordinal)
            .ThenBy(group => group.SeriesId)
            .ToArray();

        return new CatalogBrowseResult<BrowseAuthorPage>(CatalogBrowseStatus.Ok, new BrowseAuthorPage(
            header.Id, header.Name, page, hasMore, groups, ungrouped,
            SavedLimitations(shown, groupingPageLocal: true)));
    }

    public async Task<CatalogBrowseResult<BrowseSeriesPage>> GetSeriesAsync(
        Guid seriesId, int page, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null) return new CatalogBrowseResult<BrowseSeriesPage>(CatalogBrowseStatus.Unauthenticated);
        if (!IsValidPage(page)) return new CatalogBrowseResult<BrowseSeriesPage>(CatalogBrowseStatus.InvalidPage);
        var header = await repository.FindSeriesAsync(seriesId, cancellationToken);
        if (header is null) return new CatalogBrowseResult<BrowseSeriesPage>(CatalogBrowseStatus.NotFound);

        var members = await repository.GetSeriesMembersAsync(seriesId, (page - 1) * PageSize, PageSize + 1, cancellationToken);
        var hasMore = members.Count > PageSize;
        // The repository already returned this page in its stable total order; sorting again here could only
        // disagree with the page boundary.
        var entries = members.Take(PageSize)
            .Select(member => new BrowseEntry(ToBook(member.Work), member.PositionLabel, member.PositionSort))
            .ToArray();

        return new CatalogBrowseResult<BrowseSeriesPage>(CatalogBrowseStatus.Ok, new BrowseSeriesPage(
            header.Id, header.Name, page, hasMore, entries,
            SavedLimitations(members.Select(member => member.Work), groupingPageLocal: false)));
    }

    public async Task<CatalogBrowseResult<ObservedAuthorPreview>> PreviewAuthorAsync(
        ObservedSample sample, int page, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null) return new CatalogBrowseResult<ObservedAuthorPreview>(CatalogBrowseStatus.Unauthenticated);
        if (!IsValidPage(page)) return new CatalogBrowseResult<ObservedAuthorPreview>(CatalogBrowseStatus.InvalidPage);

        sample = SourceScoped(sample);
        var label = sample.Subject.Label;
        // Exact, case-sensitive equality with the full observed credit, within the one source that produced it.
        var matched = sample.ProviderRecords
            .Where(record => record.Credits.Any(credit => string.Equals(credit.Label, label, StringComparison.Ordinal)))
            .OrderBy(record => record.Title, StringComparer.Ordinal)
            .ThenBy(record => record.ExternalId, StringComparer.Ordinal)
            .ToArray();
        var (shown, hasMore) = Page(matched, page);

        var entries = new List<(string Name, Guid? SubjectId, BrowseEntry Entry)>();
        var ungrouped = new List<BrowseEntry>();
        foreach (var record in shown)
        {
            var book = ToBook(record);
            if (record.Memberships.Count == 0)
            {
                ungrouped.Add(new BrowseEntry(book, null, null));
                continue;
            }

            foreach (var membership in record.Memberships)
            {
                entries.Add((membership.Label, membership.SubjectId,
                    new BrowseEntry(book, membership.PositionLabel, membership.PositionSort ?? ParsePosition(membership.PositionLabel))));
            }
        }

        // Group by the full observed label only. Labels that differ at all (including an ordering qualifier) stay apart.
        var groups = entries
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .Select(group => new BrowseGroup(
                null,
                group.Select(item => item.SubjectId).FirstOrDefault(id => id is not null),
                group.Key,
                SortEntries(group.Select(item => item.Entry))))
            .OrderBy(group => group.Name, StringComparer.Ordinal)
            .ToArray();

        var choices = await GetChoicesAsync(ObservedSubjectKind.Author, label, cancellationToken);
        return new CatalogBrowseResult<ObservedAuthorPreview>(CatalogBrowseStatus.Ok, new ObservedAuthorPreview(
            sample.Subject,
            ObservationTime(sample, matched),
            sample.ExpiresAtUtc,
            page,
            hasMore,
            groups,
            ungrouped,
            choices,
            ObservedLimitations(sample, choices, groupingPageLocal: true)));
    }

    public async Task<CatalogBrowseResult<ObservedSeriesPreview>> PreviewSeriesAsync(
        ObservedSample sample, int page, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null) return new CatalogBrowseResult<ObservedSeriesPreview>(CatalogBrowseStatus.Unauthenticated);
        if (!IsValidPage(page)) return new CatalogBrowseResult<ObservedSeriesPreview>(CatalogBrowseStatus.InvalidPage);

        sample = SourceScoped(sample);
        var label = sample.Subject.Label;
        // One entry per matching membership: a record carrying the same label twice keeps both positions.
        var matched = sample.ProviderRecords
            .SelectMany(record => record.Memberships
                .Where(membership => string.Equals(membership.Label, label, StringComparison.Ordinal))
                .Select(membership => (Record: record, Membership: membership)))
            .Select(item => new BrowseEntry(
                ToBook(item.Record),
                item.Membership.PositionLabel,
                item.Membership.PositionSort ?? ParsePosition(item.Membership.PositionLabel)))
            .ToArray();
        var ordered = SortEntries(matched);
        var (shown, hasMore) = Page(ordered, page);

        var choices = await GetChoicesAsync(ObservedSubjectKind.Series, label, cancellationToken);
        var recordsForTime = sample.ProviderRecords
            .Where(record => record.Memberships.Any(membership => string.Equals(membership.Label, label, StringComparison.Ordinal)))
            .ToArray();
        return new CatalogBrowseResult<ObservedSeriesPreview>(CatalogBrowseStatus.Ok, new ObservedSeriesPreview(
            sample.Subject,
            ObservationTime(sample, recordsForTime),
            sample.ExpiresAtUtc,
            page,
            hasMore,
            shown,
            choices,
            ObservedLimitations(sample, choices, groupingPageLocal: false)));
    }

    /// <summary>Defence in depth: whatever the host supplied, only the selected source's own records are ever matched.</summary>
    private static ObservedSample SourceScoped(ObservedSample sample) => sample with
    {
        ProviderRecords = sample.ProviderRecords
            .Where(record => string.Equals(record.ProviderId, sample.Subject.ProviderId, StringComparison.Ordinal))
            .ToArray()
    };

    /// <summary>The number a position label states when it is only a plain decimal; anything else stays unknown.</summary>
    public static decimal? ParsePosition(string? positionLabel)
    {
        if (positionLabel is null || positionLabel.Length is 0 or > 16) return null;
        foreach (var character in positionLabel)
        {
            if (character is not (>= '0' and <= '9') and not '.') return null;
        }

        return decimal.TryParse(positionLabel, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : null;
    }

    private async Task<BrowseNameChoices> GetChoicesAsync(
        ObservedSubjectKind kind, string label, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(label)) return new BrowseNameChoices([], false);
        var normalized = CatalogText.NormalizeForMatch(label);
        if (normalized.Length == 0) return new BrowseNameChoices([], false);

        var found = kind == ObservedSubjectKind.Author
            ? await repository.FindAuthorsByNormalizedNameAsync(normalized, MaximumNameChoices + 1, cancellationToken)
            : await repository.FindSeriesByNormalizedNameAsync(normalized, MaximumNameChoices + 1, cancellationToken);
        return new BrowseNameChoices(found.Take(MaximumNameChoices).ToArray(), found.Count > MaximumNameChoices);
    }

    private static List<string> ObservedLimitations(ObservedSample sample, BrowseNameChoices choices, bool groupingPageLocal)
    {
        // Identity and sample qualifications are always present, even when exactly one saved namesake exists.
        var limitations = new List<string>
        {
            CatalogBrowseLimitations.SourceSample,
            CatalogBrowseLimitations.IdentityUnverified
        };
        if (groupingPageLocal) limitations.Add(CatalogBrowseLimitations.GroupingPageLocal);
        if (sample.ContextTruncated || choices.Truncated || sample.ProviderRecords.Any(record =>
                record.CreditsTruncated || record.MembershipsTruncated || record.FactsTruncated))
        {
            limitations.Add(CatalogBrowseLimitations.Truncated);
        }

        if (sample.ProviderFailed) limitations.Add(CatalogBrowseLimitations.SourceFailed);
        return limitations;
    }

    /// <summary>Saved-page qualifications; a per-Work association or format cap is disclosed, never silently applied.</summary>
    private static List<string> SavedLimitations(IEnumerable<BrowseWork> works, bool groupingPageLocal)
    {
        var limitations = new List<string> { CatalogBrowseLimitations.KnownLocalOnly };
        if (groupingPageLocal) limitations.Add(CatalogBrowseLimitations.GroupingPageLocal);
        if (works.Any(work => work.AssociationsTruncated || work.FactsTruncated))
        {
            limitations.Add(CatalogBrowseLimitations.Truncated);
        }

        return limitations;
    }

    private static DateTimeOffset ObservationTime(ObservedSample sample, ObservedBookRecord[] matched)
    {
        var anchor = sample.ProviderRecords.FirstOrDefault(record =>
            string.Equals(record.ExternalId, sample.Subject.AnchorExternalId, StringComparison.Ordinal));
        if (anchor is not null) return anchor.ObservedAtUtc;
        return matched.Length > 0 ? matched.Min(record => record.ObservedAtUtc) : sample.ExpiresAtUtc;
    }

    private static (T[] Shown, bool HasMore) Page<T>(IReadOnlyList<T> ordered, int page)
    {
        var skip = (page - 1) * PageSize;
        var window = ordered.Skip(skip).Take(PageSize + 1).ToArray();
        return (window.Take(PageSize).ToArray(), window.Length > PageSize);
    }

    /// <summary>
    /// Known numbers ascending (so 2.5 follows 2), then unnumbered entries; ties are kept, never merged, and broken by
    /// position text, title and identity so the order is total and stable.
    /// </summary>
    private static BrowseEntry[] SortEntries(IEnumerable<BrowseEntry> entries) => entries
        .OrderBy(entry => entry.Position is null)
        .ThenBy(entry => entry.Position)
        .ThenBy(entry => entry.PositionLabel, StringComparer.Ordinal)
        .ThenBy(entry => entry.Book.Title, StringComparer.Ordinal)
        .ThenBy(entry => entry.Book.WorkId)
        .ThenBy(entry => entry.Book.ProviderId, StringComparer.Ordinal)
        .ThenBy(entry => entry.Book.ExternalId, StringComparer.Ordinal)
        .ToArray();

    private static BrowseBook ToBook(BrowseWork work) => new(
        work.WorkId,
        null,
        null,
        work.Title,
        work.FirstPublicationDate,
        work.Credits
            .OrderBy(credit => credit.Ordinal)
            .Select(credit => new BrowseBookCredit(credit.AuthorId, null, credit.Name, credit.Role))
            .ToArray(),
        work.Memberships
            .Select(membership => new BrowseBookMembership(
                membership.SeriesId, null, membership.SeriesName, membership.PositionLabel,
                membership.PositionSort, membership.IsPrimary))
            .ToArray(),
        work.Sources.Select(source => new BrowseBookSource(source.ProviderId, null, source.ObservedAtUtc)).ToArray(),
        work.Formats,
        null,
        null,
        work.AssociationsTruncated);

    /// <summary>The compact observed record shaped as a browse book, for lists and the cached-book fallback.</summary>
    public static BrowseBook ToBook(ObservedBookRecord record) => new(
        null,
        record.ProviderId,
        record.ExternalId,
        record.Title,
        record.PublicationDate,
        record.Credits.Select(credit => new BrowseBookCredit(null, credit.SubjectId, credit.Label, null)).ToArray(),
        record.Memberships
            .Select(membership => new BrowseBookMembership(
                null, membership.SubjectId, membership.Label, membership.PositionLabel,
                membership.PositionSort ?? ParsePosition(membership.PositionLabel), membership.IsPrimary))
            .ToArray(),
        [new BrowseBookSource(record.ProviderId, record.ProviderName, record.ObservedAtUtc)],
        record.Formats,
        record.VersionKind,
        record.VersionLabel,
        record.CreditsTruncated || record.MembershipsTruncated);
}
