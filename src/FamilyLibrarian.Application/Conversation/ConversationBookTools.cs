using System.Text.RegularExpressions;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Conversation;

/// <summary>The caller has no authenticated Family Librarian identity in this scope.</summary>
public sealed class ConversationUnauthenticatedException() : Exception("No authenticated user for this conversation operation.");

public sealed record ConversationSearchResult(
    IReadOnlyList<ConversationChoiceRef> Choices, bool SomeSourcesUnavailable, IReadOnlyList<BookCandidate> Candidates);

public sealed record ConversationLibraryResult(
    string Title, string Authors, IReadOnlyList<string> OwnedFormats, bool SourcesUnavailable);

/// <summary>The complete action surface of a conversation. See <see cref="ConversationBookTools"/>.</summary>
public interface IConversationTools
{
    Task<ConversationSearchResult> SearchBooksAsync(string text, int maxChoices, CancellationToken cancellationToken);

    Task<string?> GetBookDetailsAsync(ConversationChoiceRef choice, CancellationToken cancellationToken);

    Task<IReadOnlyList<BookRequestView>> GetMyRequestsAsync(CancellationToken cancellationToken);

    Task<ConversationLibraryResult?> CheckLibraryAsync(string text, CancellationToken cancellationToken);

    Task<Guid?> ResolveWorkAsync(ConversationChoiceRef choice, CancellationToken cancellationToken);

    Task<CreateBookRequestResult> CreateBookRequestAsync(
        Guid workId, IReadOnlyList<RequestMediaType> formats, CancellationToken cancellationToken);
}

/// <summary>
/// The whole set of things a conversation may do, and nothing else: search the catalog, read a book's details, look up
/// series, check the shared library, list the caller's own requests, and create an ordinary book request. Each operation
/// requires an authenticated principal in the current scope and uses the same Application services the web pages use.
/// </summary>
/// <remarks>
/// There is intentionally no operation here for approving or discarding files, overriding security holds, publishing,
/// manual import, acquisition, account or settings changes. A test pins this class's dependencies to the allow-list so
/// adding such a capability is a deliberate, reviewed change, never a side effect.
/// </remarks>
public sealed partial class ConversationBookTools(
    ICurrentUser currentUser,
    ICatalogSearchService search,
    IActiveMetadataProviderResolver providers,
    CatalogWorkResolver works,
    BookRequestService requests,
    ICandidateAvailabilityService availability) : IConversationTools
{
    private const int MaxDescriptionLength = 400;

    public async Task<ConversationSearchResult> SearchBooksAsync(string text, int maxChoices, CancellationToken cancellationToken)
    {
        RequireUser();
        var query = new BookSearchQuery(text.Length > CatalogSearchLimits.MaximumQueryLength ? text[..CatalogSearchLimits.MaximumQueryLength] : text);
        if (!CatalogSearchLimits.IsValidQueryText(query.Text))
        {
            return new ConversationSearchResult([], false, []);
        }

        var outcome = await search.SearchAsync(query, cancellationToken);
        var candidates = outcome.Candidates.Take(maxChoices).ToArray();
        var choices = candidates
            .Where(candidate => candidate.ExternalId.Length is > 0 and <= ConversationState.MaxFieldLength)
            .Select(candidate => new ConversationChoiceRef(
                candidate.ProviderId,
                candidate.ExternalId,
                ConversationState.Clip(candidate.Title),
                ConversationState.Clip(string.Join(", ", candidate.Authors.Take(3))),
                ConversationState.Clip(DescribeCandidate(candidate))))
            .ToArray();
        return new ConversationSearchResult(choices, outcome.Providers.Any(provider => !provider.Succeeded), candidates);
    }

    public async Task<string?> GetBookDetailsAsync(ConversationChoiceRef choice, CancellationToken cancellationToken)
    {
        RequireUser();
        var provider = await providers.FindActiveProviderAsync(choice.ProviderId, cancellationToken);
        if (provider is null)
        {
            return null;
        }

        BookCandidate? candidate;
        try
        {
            candidate = await provider.GetDetailsAsync(choice.ExternalId, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }

        return candidate is null ? null : DescribeDetails(candidate);
    }

    /// <summary>The caller's own requests only, newest activity first (the repository scopes the query to the principal).</summary>
    public async Task<IReadOnlyList<BookRequestView>> GetMyRequestsAsync(CancellationToken cancellationToken)
    {
        RequireUser();
        return await requests.ListMineAsync(cancellationToken);
    }

    /// <summary>Whether the household library already has the closest catalog match, per format. Household-shared (D-10).</summary>
    public async Task<ConversationLibraryResult?> CheckLibraryAsync(string text, CancellationToken cancellationToken)
    {
        RequireUser();
        var found = await SearchBooksAsync(text, 1, cancellationToken);
        if (found.Candidates.Count == 0)
        {
            return null;
        }

        var candidate = found.Candidates[0];
        var identity = new BookIdentity(
            candidate.Title,
            candidate.Authors.Count > 0 ? candidate.Authors[0] : null,
            candidate.Editions.Select(edition => edition.Isbn13).OfType<string>().Distinct().ToArray());
        var result = await availability.GetAvailabilityAsync(identity, cancellationToken);
        var owned = new List<string>();
        if (result.Ebook.Any(option => option.OptionKind == OptionKind.Owned)) owned.Add("ebook");
        if (result.Audiobook.Any(option => option.OptionKind == OptionKind.Owned)) owned.Add("audiobook");
        return new ConversationLibraryResult(candidate.Title, string.Join(", ", candidate.Authors.Take(3)), owned, found.SomeSourcesUnavailable);
    }

    /// <summary>Resolves a search result to a catalog Work, exactly as the web page's "request" action does.</summary>
    public async Task<Guid?> ResolveWorkAsync(ConversationChoiceRef choice, CancellationToken cancellationToken)
    {
        RequireUser();
        try
        {
            return (await works.ResolveAsync(choice.ProviderId, choice.ExternalId, cancellationToken)).Work.Id;
        }
        catch (Exception exception) when (exception is UnknownMetadataProviderException or CatalogCandidateNotFoundException or ArgumentException
            or HttpRequestException or System.Text.Json.JsonException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>
    /// Creates an ordinary Family Librarian request through <see cref="BookRequestService"/>: the same validation, duplicate and
    /// owned-library checks, readiness checks and approval workflow as the web UI. Never confirms a duplicate or owned warning.
    /// </summary>
    public Task<CreateBookRequestResult> CreateBookRequestAsync(
        Guid workId, IReadOnlyList<RequestMediaType> formats, CancellationToken cancellationToken)
    {
        RequireUser();
        return requests.CreateAsync(workId, formats, note: null, confirmDuplicate: false, confirmOwned: false, cancellationToken);
    }

    private Guid RequireUser() => currentUser.UserId ?? throw new ConversationUnauthenticatedException();

    public static string DescribeCandidate(BookCandidate candidate)
    {
        var parts = new List<string>();
        if (candidate.PublicationDate is { } date) parts.Add(date.Year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var series = candidate.Series.FirstOrDefault(entry => entry.IsPrimary) ?? (candidate.Series.Count > 0 ? candidate.Series[0] : null);
        if (series is not null)
        {
            parts.Add(string.IsNullOrWhiteSpace(series.PositionLabel) ? series.Name : $"{series.Name} #{series.PositionLabel}");
        }

        if (!string.IsNullOrWhiteSpace(candidate.Language)) parts.Add(candidate.Language);
        return string.Join(" · ", parts);
    }

    private static string DescribeDetails(BookCandidate candidate)
    {
        var lines = new List<string> { $"{candidate.Title} by {string.Join(", ", candidate.Authors.Take(3))}" };
        var facts = DescribeCandidate(candidate);
        if (facts.Length > 0) lines.Add(facts);
        var publisher = new List<string>();
        if (!string.IsNullOrWhiteSpace(candidate.Publisher)) publisher.Add(candidate.Publisher);
        if (candidate.PageCount is > 0) publisher.Add($"{candidate.PageCount} pages");
        if (publisher.Count > 0) lines.Add(string.Join(", ", publisher));
        if (PlainText(candidate.Description) is { Length: > 0 } description) lines.Add(description);
        return string.Join("\n", lines);
    }

    /// <summary>Provider descriptions may carry markup. Chat shows plain text only, bounded.</summary>
    public static string PlainText(string? markup)
    {
        if (string.IsNullOrWhiteSpace(markup))
        {
            return string.Empty;
        }

        var text = TagPattern().Replace(markup, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = ConversationParser.Normalize(text);
        return text.Length <= MaxDescriptionLength ? text : text[..MaxDescriptionLength].TrimEnd() + "…";
    }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex TagPattern();
}
