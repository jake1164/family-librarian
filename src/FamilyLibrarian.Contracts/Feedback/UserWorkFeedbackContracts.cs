namespace FamilyLibrarian.Contracts.Feedback;

/// <param name="ExpectedVersion">
/// <see langword="null"/> when recording a Work's first feedback; the
/// previously-loaded <c>Version</c> when correcting existing feedback.
/// </param>
public sealed record SetWorkFeedbackRequest(
    DateOnly CompletedOn,
    uint? ExpectedVersion);

public sealed record RemoveWorkFeedbackRequest(uint ExpectedVersion);

public sealed record WorkFeedbackResponse(
    Guid WorkId,
    string WorkTitle,
    IReadOnlyList<string> Authors,
    string? CoverUrl,
    DateOnly CompletedOn,
    uint Version);

public sealed record WorkFeedbackListResponse(IReadOnlyList<WorkFeedbackResponse> Items);
