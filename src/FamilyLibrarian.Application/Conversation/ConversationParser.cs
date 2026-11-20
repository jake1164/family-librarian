using System.Globalization;
using System.Text.RegularExpressions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Conversation;

public enum ConversationCommandKind
{
    Empty,
    Help,
    Cancel,
    Status,
    Pick,
    Details,
    Request,
    Format,
    Library,
    Isbn,
    Series,
    OutOfDomain,
    Search,

    /// <summary>"it", "that one": an action on the one book the conversation is about. <see cref="ParsedConversationMessage.Action"/> says which.</summary>
    Reference,

    /// <summary>A question about unreleased books, release dates or release years: things the catalog sources cannot answer.</summary>
    Release,

    /// <summary>The edition titles of the record the conversation is about. <see cref="ParsedConversationMessage.Number"/> picks which one.</summary>
    Editions,

    /// <summary>"what has X written", "books by X": an author question. <see cref="ParsedConversationMessage.Action"/> says works or series.</summary>
    Author,

    /// <summary>"more": the next page of the answer the conversation is about. Never a new question.</summary>
    More
}

/// <summary>The <see cref="ParsedConversationMessage.Action"/> values for <see cref="ConversationCommandKind.Reference"/> and <see cref="ConversationCommandKind.Release"/>.</summary>
public static class ConversationActions
{
    public const string Request = "request";
    public const string Availability = "availability";
    public const string Details = "details";

    /// <summary>For a reference: what follows the book in focus. For a release question: the next release of a series.</summary>
    public const string Next = "next";

    public const string Upcoming = "upcoming";
    public const string Year = "year";
    public const string When = "when";

    /// <summary>For an author question: their known works.</summary>
    public const string Works = "works";

    /// <summary>For an author question: the series they are credited on.</summary>
    public const string Series = "series";
}

/// <summary>A parsed message. Only the fields that apply to <see cref="Kind"/> are set.</summary>
public sealed record ParsedConversationMessage(
    ConversationCommandKind Kind,
    string? Text = null,
    int? Number = null,
    IReadOnlyList<RequestMediaType>? Formats = null,
    bool RequestIntent = false,
    string? SeriesName = null,
    decimal? SeriesPosition = null,
    string? SeriesAfterTitle = null,
    string? Action = null);

/// <summary>
/// Rule-based reading of a message, with no model involved. Everything here is a fixed grammar: commands, numbered picks,
/// ISBNs, series positions and a small book-domain gate. Anything it does not recognize as out of scope is treated as a
/// book search, because a title can be any ordinary words ("Weather" is a novel).
/// </summary>
/// <remarks>
/// The domain gate is a courtesy, not a security control: it only decides which canned reply to give. What a user can
/// actually do is limited by the small set of actions <see cref="ConversationBookTools"/> exposes.
/// </remarks>
public static partial class ConversationParser
{
    public const int MaxChoiceNumber = 20;

    public static ParsedConversationMessage Parse(string? text)
    {
        var message = Normalize(text);
        if (message.Length == 0)
        {
            return new(ConversationCommandKind.Empty);
        }

        var lower = message.ToLowerInvariant();
        var bare = lower.TrimEnd('?', '!', '.', ' ');

        if (bare is "help" or "commands" or "what can you do" or "how does this work" or "hi" or "hello" or "hey" || bare == "")
        {
            return new(ConversationCommandKind.Help);
        }

        if (bare is "cancel" or "stop" or "nevermind" or "never mind" or "reset" or "start over" or "clear" or "quit")
        {
            return new(ConversationCommandKind.Cancel);
        }

        if (IsStatusQuestion(bare))
        {
            return new(ConversationCommandKind.Status);
        }

        // Only the whole message: a title that merely contains "more" ("Sense and More") is still a search.
        if (bare is "more" or "show more" or "next page" or "more results" or "see more" or "continue")
        {
            return new(ConversationCommandKind.More);
        }

        if (PickPattern().Match(bare) is { Success: true } pick && TryNumber(pick.Groups[1].Value, out var picked))
        {
            return new(ConversationCommandKind.Pick, Number: picked);
        }

        if (FormatOnlyPattern().Match(bare) is { Success: true } formatOnly)
        {
            return new(ConversationCommandKind.Format, Formats: ParseFormats(formatOnly.Groups[1].Value));
        }

        if (DetailsPattern().Match(bare) is { Success: true } details && TryNumber(details.Groups[1].Value, out var detailNumber))
        {
            return new(ConversationCommandKind.Details, Number: detailNumber);
        }

        if (RequestNumberPattern().Match(bare) is { Success: true } request && TryNumber(request.Groups[1].Value, out var requestNumber))
        {
            var formats = request.Groups[2].Success ? ParseFormats(request.Groups[2].Value) : null;
            return new(ConversationCommandKind.Request, Number: requestNumber, Formats: formats, RequestIntent: true);
        }

        if (ReadReference(bare) is { } reference)
        {
            return reference;
        }

        if (ExtractIsbn(message) is { } isbn)
        {
            return new(ConversationCommandKind.Isbn, Text: isbn);
        }

        if (InjectionPattern().IsMatch(lower))
        {
            return new(ConversationCommandKind.OutOfDomain);
        }

        if (ReadRelease(message.TrimEnd('?', '!', '.', ' ')) is { } release)
        {
            return release;
        }

        if (ReadEditions(bare) is { } editions)
        {
            return editions;
        }

        if (ReadAuthorQuestion(message.TrimEnd('?', '!', '.', ' ')) is { } author)
        {
            return author;
        }

        if (LibraryPattern().Match(message) is { Success: true } library && library.Groups["q"].Value.Trim() is { Length: >= 2 } libraryQuery)
        {
            return new(ConversationCommandKind.Library, Text: libraryQuery);
        }

        var (searchText, requestIntent, searchFormats) = ReadSearch(message);
        if (searchText.Length >= 2 && SeriesParser.TryParse(searchText, out var seriesName, out var seriesPosition, out var seriesAfterTitle))
        {
            return new(ConversationCommandKind.Series, Text: message, Formats: searchFormats, RequestIntent: requestIntent,
                SeriesName: seriesName, SeriesPosition: seriesPosition, SeriesAfterTitle: seriesAfterTitle);
        }

        if (searchText.Length < 2)
        {
            return new(ConversationCommandKind.Help);
        }

        if (IsOutOfDomain(searchText))
        {
            return new(ConversationCommandKind.OutOfDomain);
        }

        return new(ConversationCommandKind.Search, Text: searchText, Formats: searchFormats, RequestIntent: requestIntent);
    }

    /// <summary>
    /// "request it", "is it available as an audiobook", "what comes out next": an action on the book the conversation is about.
    /// Which book that is, or whether there is one, is decided later from the conversation state; this only reads the wording.
    /// </summary>
    private static ParsedConversationMessage? ReadReference(string bare)
    {
        if (RequestItPattern().Match(bare) is { Success: true } requestIt)
        {
            return new(ConversationCommandKind.Reference, Action: ConversationActions.Request, Formats: FormatOf(requestIt), RequestIntent: true);
        }

        if (AvailabilityPattern().Match(bare) is { Success: true } availability)
        {
            return new(ConversationCommandKind.Reference, Action: ConversationActions.Availability, Formats: FormatOf(availability));
        }

        if (AvailabilityFormatPattern().Match(bare) is { Success: true } availabilityFormat)
        {
            return new(ConversationCommandKind.Reference, Action: ConversationActions.Availability, Formats: FormatOf(availabilityFormat));
        }

        if (DetailsItPattern().IsMatch(bare))
        {
            return new(ConversationCommandKind.Reference, Action: ConversationActions.Details);
        }

        return NextPattern().IsMatch(bare) ? new(ConversationCommandKind.Reference, Action: ConversationActions.Next) : null;
    }

    private static IReadOnlyList<RequestMediaType>? FormatOf(Match match) =>
        match.Groups["fmt"].Success ? ParseFormats(match.Groups["fmt"].Value) : null;

    /// <summary>
    /// Questions the catalog sources cannot answer: books not yet published, release dates, and filtering by release year. They are
    /// recognized so chat can say so, instead of returning a list of unrelated search hits. Short messages are left alone because
    /// a title can be any ordinary words ("Coming Soon").
    /// </summary>
    private static ParsedConversationMessage? ReadRelease(string plain)
    {
        var words = plain.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        if (WhenPattern().Match(plain) is { Success: true } asked && asked.Groups["q"].Value.Trim() is { Length: >= 2 } subject)
        {
            return new(ConversationCommandKind.Release, Text: subject.Trim('"', '“', '”'), Action: ConversationActions.When);
        }

        if (words >= 3 && NextReleasePattern().Match(plain) is { Success: true } next && !AfterPattern().IsMatch(plain))
        {
            return new(ConversationCommandKind.Release, SeriesName: next.Groups["s"].Value.Trim(), Action: ConversationActions.Next);
        }

        if (words >= 4 && ReleaseYearPattern().IsMatch(plain))
        {
            return new(ConversationCommandKind.Release, Action: ConversationActions.Year);
        }

        if (words >= 3 && UpcomingPattern().IsMatch(plain))
        {
            return new(ConversationCommandKind.Release, Text: UpcomingSubject(plain), Action: ConversationActions.Upcoming);
        }

        return null;
    }

    /// <summary>
    /// What an "upcoming" question is about: the trigger words and a trailing "books"/"novels" removed. With nothing left the
    /// question names no subject, and chat asks for one rather than listing releases from every series at once.
    /// </summary>
    private static string? UpcomingSubject(string plain)
    {
        var subject = UpcomingPattern().Replace(plain, " ").Trim();
        subject = UpcomingLeadPattern().Replace(subject, string.Empty).Trim();
        foreach (var prefix in new[] { "books by ", "novels by ", "book by ", "novel by ", "books of ", "novels of ", "books in ", "novels in " })
        {
            if (subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                subject = subject[prefix.Length..].Trim();
                break;
            }
        }

        subject = TrailingBooksPattern().Replace(subject, string.Empty).Trim().Trim(' ', ',', ':', '-');
        subject = WhitespacePattern().Replace(subject, " ").Trim();
        return subject.Length >= 2 && CatalogSearchLimits.IsValidQueryText(subject) ? subject : null;
    }

    /// <summary>
    /// "editions", "edition titles", "editions 2": the edition titles of the record in focus, or of the numbered one. A bare command,
    /// like "details": it names no identity, and what it selects is held on the server.
    /// </summary>
    private static ParsedConversationMessage? ReadEditions(string bare)
    {
        if (EditionsPattern().Match(bare) is not { Success: true } match)
        {
            return null;
        }

        return match.Groups["n"].Success && TryNumber(match.Groups["n"].Value, out var number)
            ? new(ConversationCommandKind.Editions, Number: number)
            : new(ConversationCommandKind.Editions);
    }

    /// <summary>
    /// An author bibliography question, told apart from a title search by its wording only: "what has X written", "books by X",
    /// "what series has X written". Whether X is an author at all is decided later from the catalog, and a name never selects.
    /// </summary>
    private static ParsedConversationMessage? ReadAuthorQuestion(string plain)
    {
        if (AuthorSeriesPattern().Match(plain) is { Success: true } series && UsableAuthor(series.Groups["a"].Value) is { } seriesAuthor)
        {
            return new(ConversationCommandKind.Author, Text: seriesAuthor, Action: ConversationActions.Series);
        }

        if (AuthorWorksPattern().Match(plain) is { Success: true } works && UsableAuthor(works.Groups["a"].Value) is { } worksAuthor)
        {
            return new(ConversationCommandKind.Author, Text: worksAuthor, Action: ConversationActions.Works);
        }

        return null;
    }

    private static string? UsableAuthor(string value)
    {
        var author = value.Trim().Trim('"', '“', '”').Trim();
        return author.Length is >= 2 and <= 120 && CatalogSearchLimits.IsValidQueryText(author) ? author : null;
    }

    /// <summary>True when the message has a plain book cue ("novel", "series", "author", "by"): it is not off topic.</summary>
    public static bool HasBookCue(string? text) => !string.IsNullOrWhiteSpace(text) && BookCuePattern().IsMatch(text.ToLowerInvariant());
    /// <summary>True when the message is about the sender's own requests ("status", "arrived", "my audiobook").</summary>
    public static bool MentionsRequests(string? text) => !string.IsNullOrWhiteSpace(text) && RequestWordPattern().IsMatch(text.ToLowerInvariant());

    /// <summary>Collapses whitespace and strips control characters so later patterns see plain text.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = new string(text.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return WhitespacePattern().Replace(cleaned, " ").Trim();
    }

    public static IReadOnlyList<RequestMediaType> ParseFormats(string word) => word.Trim().ToLowerInvariant() switch
    {
        "ebook" or "e-book" or "book" or "epub" => [RequestMediaType.Ebook],
        "audiobook" or "audio book" or "audio" or "audiobooks" => [RequestMediaType.Audiobook],
        "both" or "ebook and audiobook" or "audiobook and ebook" => [RequestMediaType.Ebook, RequestMediaType.Audiobook],
        _ => []
    };

    private static bool IsStatusQuestion(string bare) =>
        bare is "status" or "my requests" or "requests" or "request status" or "my status" ||
        StatusPattern().IsMatch(bare);

    private static bool TryNumber(string value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number is >= 1 and <= MaxChoiceNumber;

    /// <summary>Pulls a leading "find"/"get"/"request" verb and a trailing format word off a free-text search.</summary>
    private static (string Text, bool RequestIntent, IReadOnlyList<RequestMediaType>? Formats) ReadSearch(string message)
    {
        var text = message;
        var requestIntent = false;
        IReadOnlyList<RequestMediaType>? leadingFormat = null;

        // "I want the audiobook of ...", "I'd like the second ... book", "can I have the ...": a wish for a book is a request.
        // Only taken when an article or a format word follows, so a title that begins with these words ("I Want to Be ...") is
        // still searched as typed.
        if (DesireFormatPattern().Match(text) is { Success: true } desiredFormat)
        {
            leadingFormat = ParseFormats(desiredFormat.Groups["fmt"].Value);
            text = text[desiredFormat.Length..].Trim();
            requestIntent = true;
        }
        else if (DesirePattern().Match(text) is { Success: true } desire)
        {
            text = text[desire.Length..].Trim();
            requestIntent = true;
        }

        for (var guard = 0; guard < 4; guard++)
        {
            var match = LeadingVerbPattern().Match(text);
            if (!match.Success)
            {
                break;
            }

            requestIntent |= match.Groups["req"].Success;
            text = text[match.Length..].Trim();
        }

        IReadOnlyList<RequestMediaType>? formats = leadingFormat;
        if (TrailingFormatPattern().Match(text) is { Success: true } trailing && trailing.Groups["q"].Value.Trim().Length >= 2)
        {
            formats = ParseFormats(trailing.Groups["fmt"].Value);
            text = trailing.Groups["q"].Value.Trim();
        }

        return (text.Trim().Trim('"', '“', '”').Trim(), requestIntent, formats);
    }

    /// <summary>A valid ISBN-10 or ISBN-13 (check digit verified) anywhere in the message, digits only.</summary>
    public static string? ExtractIsbn(string text)
    {
        foreach (Match candidate in IsbnCandidatePattern().Matches(text))
        {
            var digits = new string(candidate.Value.Where(character => char.IsAsciiDigit(character) || character is 'x' or 'X').ToArray()).ToUpperInvariant();
            if (digits.Length == 13 && Isbn13Valid(digits)) return digits;
            if (digits.Length == 10 && Isbn10Valid(digits)) return digits;
        }

        return null;
    }

    private static bool Isbn13Valid(string digits)
    {
        if (!digits.All(char.IsAsciiDigit)) return false;
        var sum = 0;
        for (var index = 0; index < 12; index++)
        {
            sum += (digits[index] - '0') * (index % 2 == 0 ? 1 : 3);
        }

        return (10 - sum % 10) % 10 == digits[12] - '0';
    }

    private static bool Isbn10Valid(string digits)
    {
        var sum = 0;
        for (var index = 0; index < 10; index++)
        {
            var value = digits[index] == 'X' ? (index == 9 ? 10 : -1) : (char.IsAsciiDigit(digits[index]) ? digits[index] - '0' : -1);
            if (value < 0) return false;
            sum += value * (10 - index);
        }

        return sum % 11 == 0;
    }

    /// <summary>
    /// True for text that is plainly not about books: a prompt-style instruction, or a question about a non-book topic with no
    /// book cue ("by", "novel", a quoted title...). Short bare phrases such as "Weather" are titles, not questions.
    /// </summary>
    public static bool IsOutOfDomain(string text)
    {
        var lower = text.ToLowerInvariant();
        if (InjectionPattern().IsMatch(lower))
        {
            return true;
        }

        if (BookCuePattern().IsMatch(lower) || text.Contains('"') || text.Contains('“'))
        {
            return false;
        }

        if (!OffTopicPattern().IsMatch(lower))
        {
            return false;
        }

        var words = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return lower.EndsWith('?') || QuestionLeadPattern().IsMatch(lower) || TimeWordPattern().IsMatch(lower) || words >= 4;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"^(?:#|number\s+|no\.?\s*|option\s+|choice\s+)?(\d{1,2})$")]
    private static partial Regex PickPattern();

    [GeneratedRegex(@"^(?:as\s+(?:an?\s+)?)?(ebook|e-book|epub|audiobook|audio book|audio|both|ebook and audiobook|audiobook and ebook)$")]
    private static partial Regex FormatOnlyPattern();

    [GeneratedRegex(@"^(?:details?|info|information|more about|more on|more|about|show|tell me more about)\s+(?:on\s+)?(?:#|number\s+|no\.?\s*)?(\d{1,2})$")]
    private static partial Regex DetailsPattern();

    [GeneratedRegex(@"^(?:please\s+)?(?:request|get|add|i want|i'd like|i would like)\s+(?:#|number\s+|no\.?\s*)?(\d{1,2})(?:\s+(?:as\s+(?:an?\s+)?)?(ebook|e-book|epub|audiobook|audio book|audio|both))?$")]
    private static partial Regex RequestNumberPattern();

    [GeneratedRegex(@"(?:status of (?:my )?(?:request|book)s?|(?:is|are) my (?:request|book|audiobook|ebook)s?(?: .*)?|where(?:'s| is| are) my (?:request|book|audiobook|ebook)s?|how(?:'s| is| are) my (?:request|book)s?(?: .*)?|did my (?:request|book)(?: .*)?|(?:check|show|list|see) my (?:request|requests|status))$")]
    private static partial Regex StatusPattern();

    [GeneratedRegex(@"^(?:(?:do|did) (?:we|i) (?:already )?(?:have|own)\s+(?<q>.+?)(?:\s+(?:in|at|on) (?:the |our )?(?:library|collection|shelf))?|have (?:we|i) (?:already )?got\s+(?<q>.+?)|(?:is|are)\s+(?<q>.+?)\s+(?:in|on|at) (?:the |our )?(?:library|collection|shelf)|check (?:the )?library for\s+(?<q>.+?))\s*\??$", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPattern();

    [GeneratedRegex(@"^(?:(?:please|pls|can you|could you|would you|will you|i(?:'d| would)? (?:like|want|need)(?: to)?(?: you to)?|i'm looking for|im looking for|looking for)\s+)?(?:(?<req>request|get|order|add)|find|search(?: for)?|look(?:ing)? (?:for|up)|show me|get me)\s+(?:me\s+)?(?:the\s+book\s+)?", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingVerbPattern();

    [GeneratedRegex(@"^(?<q>.+?)\s+(?:as\s+(?:an?\s+)?|in\s+|on\s+)?(?<fmt>ebook|e-book|epub|audiobook|audio book|both)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingFormatPattern();

    [GeneratedRegex(@"\b(?:97[89][\- ]?)?[0-9][0-9\- ]{8,15}[0-9xX]\b")]
    private static partial Regex IsbnCandidatePattern();

    [GeneratedRegex(@"(?:ignore|disregard|forget|override|bypass)\b.{0,40}\b(?:instructions?|rules?|prompts?|guidelines?|polic(?:y|ies)|restrictions?)|\b(?:system|developer|hidden) prompt\b|\bjailbreak\b|\byou are now\b|\bact as (?:an? )?(?!reader|librarian)|\bpretend (?:to be|you)|\breveal (?:your|the) (?:prompt|instructions|secrets?|keys?)|\b(?:api|secret) keys?\b|\bdeveloper mode\b")]
    private static partial Regex InjectionPattern();

    [GeneratedRegex(@"\bby\b|\bbook|\bnovel|\bauthor|\bwrit(?:er|ten)|\bseries|\bisbn|\bsequel|\btrilogy|\bchapter|\bpoem|\bmemoir|\bbiograph|\bstor(?:y|ies)\b|\btitled\b|\bnarrat|\bpublished")]
    private static partial Regex BookCuePattern();

    [GeneratedRegex(@"\b(?:weather|forecast|temperature|stock price|stocks?|bitcoin|crypto|news|headlines|recipe|joke|riddle|lyrics|translate|calculate|math problem|homework|essay|sports? scores?|score of|directions to|traffic|horoscope|movie times|showtimes|restaurant|flight|python|javascript|c#|sql|code|script|password|hack)\b")]
    private static partial Regex OffTopicPattern();

    [GeneratedRegex(@"^(?:what|what's|whats|who|who's|where|when|why|how|which|is|are|do|does|can|could|will|would|should|tell|give|show|write|make|create|explain|translate|calculate|solve|draw|play|sing)\b")]
    private static partial Regex QuestionLeadPattern();

    [GeneratedRegex(@"\b(?:today|tonight|tomorrow|yesterday|right now|this (?:week|weekend)|next (?:week|month))\b")]
    private static partial Regex TimeWordPattern();

    private const string FormatWords = "ebook|e-book|epub|audiobook|audio book|audio";

    private const string DesireOpener =
        @"^(?:please\s+)?(?:i(?:'d| would)?\s+(?:like|want|need)|(?:can|could|would|will)\s+(?:i|we)\s+(?:please\s+)?have|give\s+me)\s+(?:to\s+(?:request|get|have|order|add)\s+)?";

    [GeneratedRegex(DesireOpener + @"(?:(?:the|a|an|my)\s+)?(?<fmt>" + FormatWords + @")\s+(?:version\s+)?(?:of|for)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex DesireFormatPattern();

    [GeneratedRegex(DesireOpener + @"(?=(?:the|a|an|my|some)\s)", RegexOptions.IgnoreCase)]
    private static partial Regex DesirePattern();

    private const string Pronoun = @"(?:it|that one|this one|that book|this book|that|this)";

    private const string OptionalFormat = @"(?:\s+(?:as\s+|in\s+)?(?:an?\s+)?(?<fmt>ebook|e-book|epub|audiobook|audio book|audio|both))?";

    [GeneratedRegex(@"^(?:please\s+)?(?:(?:(?:can|could|may|would|will)\s+(?:i|you|we)\s+(?:please\s+)?)?(?:request|get|add|order)\s+(?:me\s+)?|i(?:'d| would)?\s+(?:like|want)\s+(?:to\s+(?:request|get)\s+)?)" + Pronoun + OptionalFormat + "$")]
    private static partial Regex RequestItPattern();

    [GeneratedRegex(@"^(?:(?:is|are)\s+" + Pronoun + @"\s+(?:available|in\s+(?:the\s+|our\s+)?(?:library|collection)|on\s+the\s+shelf)|(?:do|did)\s+(?:we|i)\s+(?:already\s+)?(?:have|own)\s+" + Pronoun + ")" + OptionalFormat + "$")]
    private static partial Regex AvailabilityPattern();

    [GeneratedRegex(@"^(?:is|are)\s+" + Pronoun + @"\s+(?:available\s+)?(?:as\s+|in\s+)?(?:an?\s+)?(?<fmt>ebook|e-book|epub|audiobook|audio book|audio)$")]
    private static partial Regex AvailabilityFormatPattern();

    [GeneratedRegex(@"^(?:(?:tell me (?:more )?about|more (?:about|on)|details?(?: on| about)?|info(?:rmation)?(?: on| about)?)\s+" + Pronoun + @"|what(?:'s| is)\s+(?:it|that|this)\s+about)$")]
    private static partial Regex DetailsItPattern();

    [GeneratedRegex(@"^(?:and\s+)?(?:(?:what|which)(?:'s| is)?\s+(?:book\s+)?(?:comes?\s+)?(?:out\s+)?(?:next|after\s+(?:it|that|this))|(?:what(?:'s| is)\s+)?(?:the\s+)?next(?:\s+(?:one|book|novel))?)$")]
    private static partial Regex NextPattern();

    [GeneratedRegex(@"^(?:when|what\s+(?:day|date))\s+(?:is|does|will|was|did)\s+(?<q>.+?)\s+(?:(?:coming|come)\s+out|(?:going\s+to\s+)?(?:be\s+)?(?:released|published)|out|due|release)$", RegexOptions.IgnoreCase)]
    private static partial Regex WhenPattern();

    [GeneratedRegex(@"^(?:what(?:'s| is)\s+)?(?:the\s+)?next\s+(?<s>.+?)\s+(?:book|novel)$", RegexOptions.IgnoreCase)]
    private static partial Regex NextReleasePattern();

    [GeneratedRegex(@"\bafter\b", RegexOptions.IgnoreCase)]
    private static partial Regex AfterPattern();

    [GeneratedRegex(@"\b(?:released|published|out|coming out|written|printed)\s+(?:after|before|since|in|during|between|from)\s+(?:the\s+year\s+)?(?:1[5-9]|20)\d\d\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseYearPattern();

    [GeneratedRegex(@"\b(?:upcoming|forthcoming|unreleased|not yet (?:released|published|out)|yet to be (?:released|published)|coming soon|new releases?|pre-?orders?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UpcomingPattern();

    [GeneratedRegex(@"\b(?:requests?|requested|status|progress|orders?|ordered|downloads?|downloaded|ready|arriv\w*|waiting|pending|approv\w*|my (?:books?|audiobooks?|ebooks?))\b")]
    private static partial Regex RequestWordPattern();

    [GeneratedRegex(@"^(?:(?:show|list|see)\s+(?:me\s+)?)?(?:the\s+)?(?:edition titles?|editions)(?:\s+(?:(?:of|for)\s+)?(?:#|number\s+|no\.?\s*)?(?<n>\d{1,2}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex EditionsPattern();

    [GeneratedRegex(@"\s+(?:books?|novels?|titles?|releases?|series)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingBooksPattern();

    [GeneratedRegex(@"^(?:show|list|give|tell|find|see)\s+(?:me\s+)?(?:all\s+(?:the\s+)?)?|^(?:the|any)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex UpcomingLeadPattern();

    [GeneratedRegex(@"^(?:(?:what|which)\s+(?:books|novels|works)\s+(?:has|did)\s+(?<a>.+?)\s+(?:written|write|published)|what\s+(?:else\s+)?has\s+(?<a>.+?)\s+(?:written|published)|books\s+by\s+(?<a>.+?))(?:\s+so\s+far)?$", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorWorksPattern();

    [GeneratedRegex(@"^(?:(?:what|which)\s+series\s+(?:has|did)\s+(?<a>.+?)\s+(?:written|write|published)|series\s+by\s+(?<a>.+))$", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorSeriesPattern();
}
