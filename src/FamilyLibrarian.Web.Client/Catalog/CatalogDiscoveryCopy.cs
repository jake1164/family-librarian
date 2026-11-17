using System.Globalization;
using FamilyLibrarian.Contracts.Catalog;

namespace FamilyLibrarian.Web.Client.Catalog;

/// <summary>What one lookup result says, in plain language, with the weight that fits it. Never provider text.</summary>
public sealed record LookupStatement(string Severity, string Headline, string Detail);

/// <summary>
/// The wording for explicit source lookups. Every sentence is reviewed text keyed by a stable code from the host; an unknown
/// code is skipped, never echoed, so a newer host cannot put unreviewed text on the page. The rules the wording keeps: a lookup
/// says what <em>that source returned for that question</em> and nothing wider, "empty" is never "no such thing exists",
/// a cut or partial result is never presented as complete, and a capability is stated apart from what any one call returned.
/// </summary>
public static class CatalogDiscoveryCopy
{
    public static string Title(string operation, string provider) => operation switch
    {
        "FindSeriesForBook" => $"Series {provider} reports for this book",
        "GetSeriesMembers" => $"Books {provider} lists in this series",
        "ResolveAuthor" => $"People {provider} lists under this name",
        "GetAuthorWorks" => $"Works {provider} lists for this author",
        "GetAuthorSeries" => $"Series found in {provider}'s works for this author",
        _ => $"A lookup in {provider}"
    };

    public static string OfferLabel(string kind, string provider) => kind switch
    {
        "BookSeries" => $"Find this book's series in {provider}",
        "SeriesMembers" => $"List the books {provider} has in this series",
        "ResolveAuthor" => $"Ask {provider} which person this is",
        "AuthorWorks" => $"List this author's works in {provider}",
        "AuthorSeries" => $"Find series in this author's works at {provider}",
        _ => $"Look this up in {provider}"
    };

    /// <summary>Why a lookup cannot be started, or what is still unproven about it. Null for a code with no reviewed text.</summary>
    public static string? ReasonText(string? code, string provider) => code switch
    {
        "source-disabled" => $"{provider} is switched off, so it cannot be asked.",
        "source-removed" => $"{provider} is no longer installed.",
        "not-implemented" => $"This app has not implemented that lookup for {provider}.",
        "series-endpoints-not-key-accessible" => $"{provider} does not offer a series listing at this app's level of access.",
        "no-native-author-entity" => $"{provider} has no author records to look up.",
        "no-author-id-query" => $"{provider} cannot list an author's works by an author id.",
        "no-author-to-series-query" => $"{provider} has no way to list an author's series.",
        "live-probe-not-run-coverage-uneven" => $"This lookup is built but not yet verified against {provider}, and {provider}'s coverage is uneven.",
        "documented-first-party-live-unverified" or "documented-field-live-unverified" =>
            $"{provider}'s own documentation describes this lookup, but it has not been checked against {provider}'s live service.",
        "not-declared" => $"This lookup has not been verified for {provider}.",
        _ => null
    };

    /// <summary>The declared capability, stated apart from any result. A successful call never promotes it.</summary>
    public static string? CapabilityText(string capability, string? reasonCode, string provider) => capability switch
    {
        "Unverified" => ReasonText(reasonCode, provider) ?? $"This lookup has not been verified for {provider}.",
        "Unsupported" => ReasonText(reasonCode, provider) ?? $"{provider} does not offer this lookup.",
        _ => null
    };

    public static string RefusalText(string? code, string provider) => code switch
    {
        "journey-ended" => "These lookups have run out of time or allowance for this search. Search again to start fresh.",
        "no-continuation" => "There is nothing further to load for this result.",
        "source-disabled" or "source-removed" or "not-implemented" => ReasonText(code, provider) ?? "That source cannot be asked right now.",
        "invalid-request" => "That request was not accepted. Reload the page and try again.",
        _ => ReasonText(code, provider) ?? "That lookup could not be started."
    };

    public static string ThrottleText(LookupOutcome outcome, TimeSpan? retryAfter) => outcome switch
    {
        LookupOutcome.Busy => "A lookup is already running. Wait for it to finish, then ask again.",
        _ when retryAfter is { } wait && wait > TimeSpan.Zero =>
            $"Too many lookups in the last minute. You can ask again in about {Math.Ceiling(wait.TotalSeconds).ToString(CultureInfo.InvariantCulture)} seconds.",
        _ => "Too many lookups in the last minute. Wait a little and ask again."
    };

    /// <summary>The weight and wording of one result: what the source returned, and exactly how far that goes.</summary>
    public static LookupStatement Describe(CatalogDiscoveryResponse result)
    {
        var source = result.ProviderName;
        var cut = result.Completion == "ContextCapacity" || result.Notes.Contains("context-capacity");
        if (cut)
        {
            return new LookupStatement("Warning", "Some results could not be kept",
                $"{source} returned {result.ItemsOffered} item{(result.ItemsOffered == 1 ? string.Empty : "s")}, but the temporary preview could only hold " +
                $"{result.ItemsRetained}. What is shown is a start of the list, not all of it, and nothing further can be loaded from it.");
        }

        return result.Outcome switch
        {
            "Ok" => result.Completion switch
            {
                "MoreAvailable" => new LookupStatement("Info", $"{source} returned a page",
                    "More exists. This page is not the whole list; load the next page to continue."),
                "BudgetTruncated" or "ItemTruncated" => new LookupStatement("Warning", "The lookup stopped at its limit",
                    $"{source} returned these, but the lookup reached its limit, so this is not everything."),
                _ => new LookupStatement("Info", $"{source} returned this",
                    "That is everything it returned for this question. It is not a promise that nothing more exists in the source.")
            },
            "Empty" => new LookupStatement("Info", $"{source} had nothing for this question",
                "That is all it says. It does not mean there is nothing elsewhere, or that the source never lists it under another record."),
            "Ambiguous" => new LookupStatement("Info", "More than one person fits",
                "Nothing was chosen for you. A matching name, or a larger catalog, is not proof of who is meant. Compare the details and pick one."),
            "Partial" => new LookupStatement("Warning", "Only part of the answer could be read",
                "What is shown is real. The rest is unknown, not absent, so do not read this list as complete."),
            "NoUsableItems" => new LookupStatement("Warning", $"{source} answered, but nothing could be used",
                "It sent records that lacked an id or a title. That is different from sending none."),
            "Throttled" => new LookupStatement("Warning", $"{source} asked for a wait",
                "It asked to be tried again later, for longer than this lookup may spend. Nothing was retried early. Try again in a while."),
            "TimedOut" => new LookupStatement("Warning", $"{source} did not answer in time",
                "The lookup stopped at its time limit. Anything already received is shown; the rest is unknown."),
            "Unavailable" => new LookupStatement("Warning", $"{source} could not be asked",
                "The source is not set up, or refused the app's access. This says nothing about whether it holds the information."),
            "Unsupported" => new LookupStatement("Info", $"{source} does not offer this lookup",
                "No request was made."),
            "Unverified" => new LookupStatement("Info", "This lookup could not be settled",
                "It is not yet verified for this source."),
            _ => new LookupStatement("Error", "The lookup could not be completed",
                "Nothing was saved. You can try again.")
        };
    }

    /// <summary>The reviewed sentence for a note code. Unknown codes return null and are skipped by the caller.</summary>
    public static string? NoteText(string code, string provider) => code switch
    {
        "series-name-not-provided" => $"{provider} gave only an id for some series, not a name, so none is shown.",
        "no-native-series-id" => "Some series came without a source id, so they cannot be looked up further.",
        "series-position-conflict" => "The source gave a book more than one position in the same series. Each is shown; none is chosen.",
        "duplicate-dropped" => "Exact repeats were left out.",
        "item-missing-id" => "Some records had no usable id and were left out.",
        "partial-field-error" => "Part of the source's answer could not be read.",
        "series-issues-not-mapped" => "Omnibus contents were not read.",
        "anchor-insufficient" => "A name alone does not identify a person.",
        "list-truncated" => "More people share this name than could be read.",
        "primary-author-only" => "Only series where this person is the primary author are included.",
        "series-canonical-stated" or "canonical-target-not-checked" or "canonical-semantics-unverified" =>
            "The source says a series duplicates another. Both are kept and nothing was merged.",
        "canonical-self" or "canonical-cycle" or "canonical-chain" or "canonical-scope-mismatch" or "canonical-target-missing" =>
            "The source's statement that a series duplicates another could not be verified, so it was not followed.",
        "page-ceiling" => "The page limit for this lookup was reached.",
        "attempt-ceiling" => "The request limit for this lookup was reached.",
        "deadline-reached" => "The time limit for this lookup was reached.",
        "retry-after-too-long" => "The source asked for a longer wait than this lookup may spend.",
        "throttled" => "The source said it was receiving too many requests.",
        "provider-error" => "The source reported an error.",
        "authorization-denied" => "The source refused the app's access.",
        "setup-missing" => "The source is not fully set up.",
        "journey-expired" => "The time allowed for these lookups ended.",
        "subject-not-found" => "The source did not recognise this record.",
        "book-anchor-mismatch" => "The source answered about a different record, so its answer was not used.",
        "provider-mutated-between-pages" => "The source's data changed while paging, so this page may not line up with the last.",
        "continuation-refused" => "More exists, but it cannot be continued from here.",
        "derived-from-author-works" => "Series were worked out from the works that were read, not stated by the source.",
        "no-series-claims-on-page" => "None of the works read carried series information.",
        _ => null
    };

    public static IReadOnlyList<string> NoteLines(IEnumerable<string> codes, string provider) =>
        codes.Distinct(StringComparer.Ordinal).Select(code => NoteText(code, provider)).OfType<string>().ToArray();

    /// <summary>A publication date exactly as precisely as the source stated it, with what it describes. Never an invented day.</summary>
    public static string DateText(DateOnly? date, string? precision, string? basis)
    {
        if (date is not { } value) return "Date not stated";
        var text = precision switch
        {
            "Year" => value.Year.ToString(CultureInfo.InvariantCulture),
            "Month" => value.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            _ => value.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)
        };
        var meaning = basis switch
        {
            "Original" => "original publication",
            "EditionDerived" => "date of one edition, not necessarily the original",
            _ => "what this date describes is not stated"
        };
        return $"{text} ({(precision is "Year" ? "year only; " : precision is "Month" ? "month only; " : string.Empty)}{meaning})";
    }

    /// <summary>
    /// Whether a credit may be read as authorship: the source stated no role, or stated "author". Any other stated role
    /// (illustrator, translator, narrator, ...) is shown with that role and never as an unqualified "By" credit.
    /// </summary>
    public static bool IsAuthorRole(string? role) =>
        string.IsNullOrWhiteSpace(role) || string.Equals(role.Trim(), "author", StringComparison.OrdinalIgnoreCase);

    /// <summary>The credit line of one book: authors after "By", every other stated role qualified, nothing invented when empty.</summary>
    public static string CreditLine(IReadOnlyList<CatalogDiscoveryCreditResponse> credits)
    {
        // A role the source stated but that was too long to keep is not "no role": it is shown neutrally, never as authorship.
        static bool IsAuthor(CatalogDiscoveryCreditResponse credit) => !credit.RoleNotRetained && IsAuthorRole(credit.Role);
        var authors = credits.Where(IsAuthor).Select(credit => credit.Label).ToArray();
        var others = credits.Where(credit => !IsAuthor(credit))
            .Select(credit => credit.RoleNotRetained ? $"{credit.Label} (role not retained)" : $"{credit.Label} ({credit.Role!.Trim()})")
            .ToArray();
        if (authors.Length == 0 && others.Length == 0) return "Author not reported";
        if (authors.Length == 0) return "Credited: " + string.Join(", ", others);
        var by = "By " + string.Join(", ", authors);
        return others.Length == 0 ? by : by + " · Also credited: " + string.Join(", ", others);
    }

    public static string PositionText(string? label, decimal? position)
    {
        if (!string.IsNullOrWhiteSpace(label)) return $"position {label}";
        return position is { } value ? $"position {value.ToString("0.##", CultureInfo.InvariantCulture)}" : "position not stated";
    }

    public static string? RelationshipText(string? relationship) => relationship switch
    {
        null or "native" => null,
        _ when relationship.StartsWith("derived:", StringComparison.Ordinal) =>
            "Worked out from the works that were read, not stated by the source.",
        _ => null
    };

    public static string? CanonicalText(string? state) => state switch
    {
        "Verified" => "The source says this series duplicates another one. Both are kept; nothing was merged.",
        "StatedUnverified" => "The source says this series may duplicate another. That was not verified.",
        "Invalid" => "The source's duplicate-series statement could not be verified, so it was not followed.",
        _ => null
    };
}
