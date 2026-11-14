using System.Globalization;
using FamilyLibrarian.Contracts.Catalog;

namespace FamilyLibrarian.Web.Client.Catalog;

/// <summary>Where a browse page is, so a book opened from it can offer a way back.</summary>
public enum BrowseReturnKind
{
    Author,
    Series,
    ObservedAuthor,
    ObservedSeries
}

/// <summary>
/// A typed, local return reference: a page kind plus GUIDs and a page number, never a URL. The only way back to a browse
/// page is <see cref="CatalogBrowseLinks.ReturnHref"/>, which rebuilds the route from these validated parts, so a crafted
/// query string cannot name an external destination.
/// </summary>
/// <param name="Id">The saved author/series id, or the temporary context id for an observed page.</param>
/// <param name="SubjectId">The temporary subject id, set for observed pages only.</param>
public sealed record BrowseReturn(BrowseReturnKind Kind, Guid Id, Guid? SubjectId = null, int Page = 1);

/// <summary>
/// Pure helpers for the author/series browse UI: local routes (one place for encoding), typed return context, and the
/// rules that decide when a temporary navigation reference may be shown. Nothing here calls the server.
/// </summary>
public static class CatalogBrowseLinks
{
    public const string BackParameter = "back";
    public const string BackIdParameter = "bid";
    public const string BackSubjectParameter = "bsub";
    public const string BackPageParameter = "bp";
    public const int MaximumPage = 100;

    // Same limits the host enforces on the cached-book endpoint.
    public const int MaximumProviderIdLength = 128;
    public const int MaximumExternalIdLength = 512;

    // ---- Browse routes -------------------------------------------------------------------------------------------

    public static string AuthorHref(Guid authorId, int page = 1) =>
        WithPage($"authors/{authorId}", page);

    public static string SeriesHref(Guid seriesId, int page = 1) =>
        WithPage($"series/{seriesId}", page);

    public static string ObservedAuthorHref(Guid contextId, Guid subjectId, int page = 1) =>
        WithPage($"browse/authors/{contextId}/{subjectId}", page);

    public static string ObservedSeriesHref(Guid contextId, Guid subjectId, int page = 1) =>
        WithPage($"browse/series/{contextId}/{subjectId}", page);

    /// <summary>
    /// Validates the raw <c>page</c> query value of a browse route. The pages bind the query as text, never as a number,
    /// because Blazor throws while converting a non-numeric or overflowing value to <c>int?</c>, before any component code
    /// can show recovery. A missing or empty value means the first page; anything else must be plain ASCII digits that
    /// form a number from 1 to <see cref="MaximumPage"/> (no sign, spaces, decimals or exponent). Returns false for a
    /// rejected value, which callers show as the bad-page state without calling the host.
    /// </summary>
    public static bool TryParsePage(string? raw, out int page)
    {
        if (string.IsNullOrEmpty(raw))
        {
            page = 1;
            return true;
        }

        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out page) &&
            page is >= 1 and <= MaximumPage)
        {
            return true;
        }

        page = 0;
        return false;
    }

    private static string WithPage(string route, int page) =>
        page > 1 ? $"{route}?page={page.ToString(CultureInfo.InvariantCulture)}" : route;

    // ---- Book routes ---------------------------------------------------------------------------------------------

    /// <summary>The existing saved-Work route, optionally carrying the page the user came from.</summary>
    public static string WorkHref(Guid workId, BrowseReturn? back = null) =>
        AppendReturn($"works/{workId}", back);

    /// <summary>
    /// The existing provider-book route (<c>search/{provider}/{external}</c>), unchanged except for the optional return
    /// context. Null when the reference cannot be routed safely: show recovery instead of guessing a route.
    /// </summary>
    public static string? TryBookHref(string? providerId, string? externalId, BrowseReturn? back = null)
    {
        if (!IsRoutableReference(providerId, MaximumProviderIdLength) ||
            !IsRoutableReference(externalId, MaximumExternalIdLength))
        {
            return null;
        }

        return AppendReturn(
            $"search/{Uri.EscapeDataString(providerId!)}/{Uri.EscapeDataString(externalId!)}", back);
    }

    /// <summary>
    /// A reference is routable when it is a non-blank, bounded, control-free value that is not a dot segment (a browser
    /// would collapse <c>.</c> and <c>..</c> out of the path, opening a different route).
    /// </summary>
    public static bool IsRoutableReference(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength) return false;
        if (value is "." or "..") return false;
        foreach (var character in value)
        {
            if (char.IsControl(character)) return false;
        }

        return true;
    }

    // ---- Typed return context ------------------------------------------------------------------------------------

    public static string ReturnQuery(BrowseReturn back)
    {
        var parts = new List<string>(4)
        {
            $"{BackParameter}={KindToken(back.Kind)}",
            $"{BackIdParameter}={back.Id}"
        };
        if (back.SubjectId is { } subject) parts.Add($"{BackSubjectParameter}={subject}");
        if (back.Page > 1) parts.Add($"{BackPageParameter}={back.Page.ToString(CultureInfo.InvariantCulture)}");
        return string.Join('&', parts);
    }

    private static string AppendReturn(string route, BrowseReturn? back) =>
        back is null ? route : $"{route}?{ReturnQuery(back)}";

    /// <summary>
    /// Validates the four query values into a return reference, or null for anything unrecognised: unknown kind, missing or
    /// empty GUIDs, a subject on a saved page (or none on an observed one), or a page outside 1-100.
    /// </summary>
    public static BrowseReturn? TryParseReturn(string? kind, Guid? id, Guid? subjectId, int? page)
    {
        if (id is not { } primary || primary == Guid.Empty) return null;
        var pageNumber = page ?? 1;
        if (pageNumber is < 1 or > MaximumPage) return null;

        BrowseReturnKind parsed;
        switch (kind)
        {
            case "author": parsed = BrowseReturnKind.Author; break;
            case "series": parsed = BrowseReturnKind.Series; break;
            case "oauthor": parsed = BrowseReturnKind.ObservedAuthor; break;
            case "oseries": parsed = BrowseReturnKind.ObservedSeries; break;
            default: return null;
        }

        var observed = parsed is BrowseReturnKind.ObservedAuthor or BrowseReturnKind.ObservedSeries;
        if (observed != (subjectId is { } subject && subject != Guid.Empty)) return null;
        return new BrowseReturn(parsed, primary, observed ? subjectId : null, pageNumber);
    }

    /// <summary>
    /// Parses the raw query-string values a detail page receives. Malformed GUIDs or numbers are simply "no return
    /// context", so a hand-edited URL can never break the book page or name a destination.
    /// </summary>
    public static BrowseReturn? TryParseReturn(string? kind, string? id, string? subjectId, string? page)
    {
        if (!TryParseGuid(id, out var parsedId)) return null;
        Guid? parsedSubject = null;
        if (subjectId is not null)
        {
            if (!TryParseGuid(subjectId, out var subject)) return null;
            parsedSubject = subject;
        }

        int? parsedPage = null;
        if (page is not null)
        {
            if (!int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return null;
            parsedPage = number;
        }

        return TryParseReturn(kind, parsedId, parsedSubject, parsedPage);
    }

    private static bool TryParseGuid(string? value, out Guid guid) =>
        Guid.TryParseExact(value, "D", out guid);

    private static string KindToken(BrowseReturnKind kind) => kind switch
    {
        BrowseReturnKind.Author => "author",
        BrowseReturnKind.Series => "series",
        BrowseReturnKind.ObservedAuthor => "oauthor",
        _ => "oseries"
    };

    /// <summary>The browse page a validated return reference points back to.</summary>
    public static string ReturnHref(BrowseReturn back) => back.Kind switch
    {
        BrowseReturnKind.Author => AuthorHref(back.Id, back.Page),
        BrowseReturnKind.Series => SeriesHref(back.Id, back.Page),
        BrowseReturnKind.ObservedAuthor => ObservedAuthorHref(back.Id, back.SubjectId!.Value, back.Page),
        _ => ObservedSeriesHref(back.Id, back.SubjectId!.Value, back.Page)
    };

    public static string ReturnLabel(BrowseReturn back) =>
        back.Kind is BrowseReturnKind.Author or BrowseReturnKind.ObservedAuthor ? "Back to author" : "Back to series";

    // ---- Candidate navigation ------------------------------------------------------------------------------------

    /// <summary>
    /// The temporary author reference for the author displayed at <paramref name="index"/>, only if the server issued it
    /// for that exact ordinal and label. Never matched by name alone and never rebuilt from a string.
    /// </summary>
    public static BrowseReturn? AuthorReference(CatalogBookCandidateResponse candidate, int index) =>
        Reference(candidate.Navigation, candidate.Navigation?.Authors, index,
            index >= 0 && index < candidate.Authors.Count ? candidate.Authors[index] : null,
            BrowseReturnKind.ObservedAuthor);

    public static BrowseReturn? SeriesReference(CatalogBookCandidateResponse candidate, int index) =>
        Reference(candidate.Navigation, candidate.Navigation?.Series, index,
            index >= 0 && index < candidate.Series.Count ? candidate.Series[index].Name : null,
            BrowseReturnKind.ObservedSeries);

    private static BrowseReturn? Reference(
        CatalogBookNavigationResponse? navigation,
        IReadOnlyList<CatalogNavigationReferenceResponse>? references,
        int index,
        string? displayedLabel,
        BrowseReturnKind kind)
    {
        if (navigation?.ContextId is not { } contextId || references is null || displayedLabel is null) return null;
        var match = references.FirstOrDefault(reference =>
            reference.Ordinal == index && string.Equals(reference.Label, displayedLabel, StringComparison.Ordinal));
        return match is null ? null : new BrowseReturn(kind, contextId, match.SubjectId);
    }

    /// <summary>
    /// Chooses the navigation for a freshly fetched candidate. A cached search card's navigation (usually the richer
    /// run-wide context) is kept only when every reference it holds still names the same ordinal and label as the fresh
    /// record, it is not past expiry, and it links at least as many of the fresh labels as the fresh navigation does.
    /// Otherwise the fresh navigation stands, or none: an old subject id is never attached to changed source facts.
    /// </summary>
    public static CatalogBookCandidateResponse WithBestNavigation(
        CatalogBookCandidateResponse fresh, CatalogBookCandidateResponse? cached, DateTimeOffset now)
    {
        var chosen = Usable(fresh.Navigation, fresh, now) ? fresh.Navigation : null;
        if (cached?.Navigation is { } cachedNavigation &&
            string.Equals(cached.ProviderId, fresh.ProviderId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(cached.ExternalId, fresh.ExternalId, StringComparison.OrdinalIgnoreCase) &&
            Usable(cachedNavigation, fresh, now) &&
            Agrees(cachedNavigation, fresh) &&
            Coverage(cachedNavigation, fresh) >= (chosen is null ? 0 : Coverage(chosen, fresh)) &&
            Coverage(cachedNavigation, fresh) > 0)
        {
            chosen = cachedNavigation;
        }

        return ReferenceEquals(chosen, fresh.Navigation) ? fresh : fresh with { Navigation = chosen };
    }

    private static bool Usable(CatalogBookNavigationResponse? navigation, CatalogBookCandidateResponse book, DateTimeOffset now) =>
        navigation is { ContextId: not null } &&
        (navigation.ExpiresAtUtc is not { } expiry || expiry > now) &&
        Agrees(navigation, book);

    /// <summary>True when no reference points at a label or ordinal the displayed record does not have.</summary>
    private static bool Agrees(CatalogBookNavigationResponse navigation, CatalogBookCandidateResponse book) =>
        navigation.Authors.All(reference =>
            reference.Ordinal >= 0 && reference.Ordinal < book.Authors.Count &&
            string.Equals(book.Authors[reference.Ordinal], reference.Label, StringComparison.Ordinal)) &&
        navigation.Series.All(reference =>
            reference.Ordinal >= 0 && reference.Ordinal < book.Series.Count &&
            string.Equals(book.Series[reference.Ordinal].Name, reference.Label, StringComparison.Ordinal));

    private static int Coverage(CatalogBookNavigationResponse navigation, CatalogBookCandidateResponse book) =>
        navigation.Authors.Count(reference => reference.Ordinal < book.Authors.Count) +
        navigation.Series.Count(reference => reference.Ordinal < book.Series.Count);

    // ---- Cached-book fallback ------------------------------------------------------------------------------------

    /// <summary>
    /// A compact read-only candidate built from the host's cached observation, for when the live provider cannot be
    /// reached. Editions, description and availability stay empty rather than invented. Navigation references are issued
    /// only when the cached record is complete, so each credit/series position equals its original ordinal.
    /// </summary>
    public static CatalogBookCandidateResponse ToPreviewCandidate(
        CatalogObservedBookResponse observed, string providerId, string externalId)
    {
        var book = observed.Book;
        var providerName = book.Sources.FirstOrDefault(source =>
                string.Equals(source.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))?.ProviderName
            ?? providerId;
        var complete = !book.AssociationsTruncated &&
            !observed.Limitations.Contains(CatalogBrowseCopy.TruncatedCode, StringComparer.Ordinal);

        var authors = book.Credits.Select(credit => credit.Name).ToArray();
        var series = book.Memberships
            .Select(membership => new CatalogSeriesResponse(membership.Name, membership.PositionLabel, membership.IsPrimary))
            .ToArray();
        var kind = string.IsNullOrWhiteSpace(book.VersionKind) ? "Unspecified" : book.VersionKind;
        var label = string.IsNullOrWhiteSpace(book.VersionLabel) ? "Version unknown" : book.VersionLabel;

        var navigation = complete
            ? new CatalogBookNavigationResponse(
                observed.ContextId,
                observed.ExpiresAtUtc,
                book.Credits
                    .Select((credit, index) => (credit, index))
                    .Where(item => item.credit.SubjectId is not null)
                    .Select(item => new CatalogNavigationReferenceResponse(item.index, item.credit.Name, item.credit.SubjectId!.Value))
                    .ToArray(),
                book.Memberships
                    .Select((membership, index) => (membership, index))
                    .Where(item => item.membership.SubjectId is not null)
                    .Select(item => new CatalogNavigationReferenceResponse(item.index, item.membership.Name, item.membership.SubjectId!.Value))
                    .ToArray(),
                [])
            : null;

        return new CatalogBookCandidateResponse(
            providerId, providerName, externalId, book.Title, authors, null, null, book.PublicationDate, [], series,
            null, null, [], null,
            VersionKind: kind,
            VersionLabel: label,
            VersionDescription: kind == "Unspecified"
                ? "The source does not clearly identify the version."
                : $"Identified by an earlier look at this source record as: {label}.",
            Navigation: navigation);
    }
}

/// <summary>Wire codes the host sends and the product copy the UI shows for them.</summary>
public static class CatalogBrowseCopy
{
    public const string TruncatedCode = "truncated";
    public const string KnownLocalOnlyCode = "known-local-only";
    public const string SourceSampleCode = "source-sample";
    public const string IdentityUnverifiedCode = "identity-unverified";
    public const string GroupingPageLocalCode = "grouping-page-local";
    public const string SourceFailedCode = "source-failed";

    /// <summary>
    /// Plain-language text for each limitation code. Unknown codes are skipped, never echoed, so a newer host cannot put
    /// unreviewed text on the page.
    /// </summary>
    public static IReadOnlyList<string> Describe(IEnumerable<string> codes, bool observed, string noun) =>
        codes.Distinct(StringComparer.Ordinal)
            .Select(code => code switch
            {
                KnownLocalOnlyCode => $"These are the {noun} Family Librarian already knows about, not a complete bibliography.",
                SourceSampleCode => $"These are only the {noun} seen in your recent search or book view from this source, not everything it holds.",
                IdentityUnverifiedCode => "Matched by exact name only. The source gave a name, not a verified identity, so books here may belong to a different person or series with the same name.",
                GroupingPageLocalCode => "Series groups are built from the books on this page only. A series may continue on another page.",
                TruncatedCode => "Some details were left out to keep this list small, so credits, series or sources shown may be incomplete.",
                SourceFailedCode => "A source did not answer, so this view may be missing books.",
                _ => null
            })
            .OfType<string>()
            .ToArray();
}
