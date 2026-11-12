using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Application.Communications;

public interface IAcquisitionQuestionCommunicationLookup
{
    Task<OutboundCommunication?> FindQuestionAsync(Guid userId, string? eventId, CancellationToken cancellationToken);
}

/// <summary>Runs each message in its own authenticated scope, independent of conversation enablement.</summary>
public interface IAcquisitionQuestionMatrixAnswers
{
    Task<string?> AnswerAsync(MatrixInboundMessage message, string body, CancellationToken cancellationToken);
}

public static class AcquisitionQuestionAnswerGrammar
{
    public static bool IsKeyword(string body) => body.Equals("take", StringComparison.OrdinalIgnoreCase) ||
        body.Equals("take it", StringComparison.OrdinalIgnoreCase) || body.Equals("keep", StringComparison.OrdinalIgnoreCase) ||
        body.Equals("keep looking", StringComparison.OrdinalIgnoreCase);
    public static bool IsTake(string body) => body == "1" || body.Equals("take", StringComparison.OrdinalIgnoreCase) || body.Equals("take it", StringComparison.OrdinalIgnoreCase);
}

public sealed class AcquisitionQuestionMatrixAnswerService(IAcquisitionQuestionCommunicationLookup communications,
    IRequestRepository requests, AutomaticRequestFulfillmentService fulfillment, ICurrentUser currentUser)
{
    public async Task<string?> AnswerAsync(MatrixInboundMessage message, string body, CancellationToken cancellationToken)
    {
        var keyword = AcquisitionQuestionAnswerGrammar.IsKeyword(body);
        if (!keyword && body is not ("1" or "2")) return null;
        if (currentUser.UserId is not { } userId) return keyword ? "That question was already answered." : null;
        if (!keyword && string.IsNullOrWhiteSpace(message.ReplyToEventId)) return null;
        var ask = await communications.FindQuestionAsync(userId, keyword ? null : message.ReplyToEventId, cancellationToken);
        if (!keyword && (ask?.MatrixEventId is null ||
            !string.Equals(ask.MatrixEventId, message.ReplyToEventId, StringComparison.Ordinal))) return null;
        if (ask?.RelatedEntityId is not { } requestId) return "That question was already answered.";
        var view = await requests.FindViewAsync(requestId, userId, cancellationToken);
        if (view?.NeedsReview is not { QuestionKind: not null, Candidates.Count: 1 } review)
            return "That question was already answered.";
        if (review.QuestionAskedAtUtc is null || ask.CreatedAtUtc < review.QuestionAskedAtUtc)
            return "That question was already answered.";
        var take = AcquisitionQuestionAnswerGrammar.IsTake(body);
        var outcome = take
            ? await fulfillment.ResolvePreferenceAmbiguityAsync(requestId, review.Candidates[0].CandidateId, view.Version, cancellationToken)
            : await fulfillment.DismissPreferenceAmbiguityAsync(requestId, view.Version, cancellationToken);
        if (outcome == PreferenceAmbiguityResolutionOutcome.NotAsked)
            return "Your saved preference already accepts this choice. Another requester is being asked.";
        return outcome == PreferenceAmbiguityResolutionOutcome.Resolved
            ? take ? $"Got it, fetching {view.WorkTitle}." : "OK, I'll keep looking."
            : "That question was already answered.";
    }
}
