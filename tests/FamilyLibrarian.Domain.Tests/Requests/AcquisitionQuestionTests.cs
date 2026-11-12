using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Domain.Tests.Requests;

[TestClass]
public sealed class AcquisitionQuestionTests
{
    [TestMethod]
    [DataRow("CollectionContaining", true)]
    [DataRow("CollectionContaining", false)]
    [DataRow("DramatizedOnly", true)]
    [DataRow("AbridgedOnly", true)]
    [DataRow("OtherLanguageOnly", true)]
    [DataRow("PartsOnly", false)]
    public void AnAnswerClosesTheQuestionAndAcceptanceAuthorizesOnlyItsVariant(string kind, bool take)
    {
        var request = new BookRequest(Guid.NewGuid(), Guid.NewGuid(), [RequestMediaType.Ebook], null, DateTimeOffset.UtcNow);
        var format = request.Formats.Single();
        var ask = typeof(BookRequest).GetMethod("AskQuestion");
        Assert.IsNotNull(ask, "The structured question must be part of the aggregate.");
        var kindValue = Enum.Parse(ask.GetParameters()[0].ParameterType, kind);
        RequestReviewCandidateInput[] candidates = [new(format.Id, "example-source", "variant", "Combined volume", "Example Author", "fr", null, null, null, false)];
        ask.Invoke(request, [kindValue, format.Id, "Take it, or keep looking?", candidates, DateTimeOffset.UtcNow]);
        var candidateId = request.ReviewCandidates.Single().Id;
        Assert.AreEqual(kind, typeof(BookRequest).GetProperty("QuestionKind")!.GetValue(request)?.ToString());
        if (take) request.AcceptReviewCandidate(candidateId, request.UserId, DateTimeOffset.UtcNow);
        else request.DismissReviewPreference(request.UserId, DateTimeOffset.UtcNow);
        Assert.IsNull(typeof(BookRequest).GetProperty("QuestionKind")!.GetValue(request));
        Assert.AreEqual(take ? kind : null, typeof(RequestFormat).GetProperty("AcceptedVariantKind")!.GetValue(format)?.ToString());
        Assert.AreEqual(take ? 0 : 1, request.DeclinedCandidates.Count);
        Assert.ThrowsExactly<InvalidOperationException>(() => request.AcceptReviewCandidate(candidateId, request.UserId, DateTimeOffset.UtcNow));
    }
}
