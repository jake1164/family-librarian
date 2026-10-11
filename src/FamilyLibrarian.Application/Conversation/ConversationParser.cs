using System.Globalization;
using System.Text.RegularExpressions;
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
    Search
}

/// <summary>A parsed message. Only the fields that apply to <see cref="Kind"/> are set.</summary>
public sealed record ParsedConversationMessage(
    ConversationCommandKind Kind,
    string? Text = null,
    int? Number = null,
    IReadOnlyList<RequestMediaType>? Formats = null,
    bool RequestIntent = false,
    string? SeriesName = null,
    decimal? SeriesPosition = null);

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

        if (ExtractIsbn(message) is { } isbn)
        {
            return new(ConversationCommandKind.Isbn, Text: isbn);
        }

        if (InjectionPattern().IsMatch(lower))
        {
            return new(ConversationCommandKind.OutOfDomain);
        }

        if (LibraryPattern().Match(message) is { Success: true } library && library.Groups["q"].Value.Trim() is { Length: >= 2 } libraryQuery)
        {
            return new(ConversationCommandKind.Library, Text: libraryQuery);
        }

        var (searchText, requestIntent, searchFormats) = ReadSearch(message);
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

        IReadOnlyList<RequestMediaType>? formats = null;
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
}
