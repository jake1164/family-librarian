namespace FamilyLibrarian.Application.Feedback;

/// <summary>
/// A feedback row together with the Work facts My Reading and Work detail
/// display, so neither has to separately load the catalog graph.
/// </summary>
public sealed record UserWorkFeedbackView(
    Guid WorkId,
    string WorkTitle,
    IReadOnlyList<string> Authors,
    string? CoverUrl,
    DateOnly CompletedOn,
    int Rating,
    uint Version);
