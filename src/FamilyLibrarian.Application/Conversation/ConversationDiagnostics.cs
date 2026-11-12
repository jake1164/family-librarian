namespace FamilyLibrarian.Application.Conversation;

/// <summary>
/// Where the time goes in a chat turn. Steps carry a name and an outcome word only: never a message, a title or any other
/// content. Optional; the conversation behaves the same without it.
/// </summary>
public interface IConversationDiagnostics
{
    void StepCompleted(string stepName, TimeSpan elapsed, string outcome);
}

/// <summary>Step names used with <see cref="IConversationDiagnostics"/>.</summary>
public static class ConversationSteps
{
    public const string CatalogSearch = "catalog.search";
    public const string LibraryAvailability = "library.availability";
    public const string SeriesLookup = "series.lookup";
    public const string BookDetails = "book.details";
    public const string Interpreter = "interpreter";
    public const string Turn = "turn";
}

/// <summary>
/// The longest a chat turn waits on a slow source before answering without it. A provider that hangs must not leave a person
/// staring at nothing for the length of an HTTP timeout; running out of time is reported, never silently treated as "none".
/// </summary>
public sealed record ConversationTimeBudgets(TimeSpan Search, TimeSpan Availability, TimeSpan Details)
{
    public static ConversationTimeBudgets Default { get; } = new(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10));
}
