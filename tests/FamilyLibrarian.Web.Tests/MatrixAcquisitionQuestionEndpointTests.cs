using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class MatrixAcquisitionQuestionEndpointTests
{
    [TestMethod]
    [DataRow("take", "$question", null, true, true)]
    [DataRow("TAKE IT", null, null, true, true)]
    [DataRow("keep", null, null, true, false)]
    [DataRow("Keep Looking", "$question", "$wrong", true, false)]
    [DataRow("1", "$question", "$question", true, true)]
    [DataRow("2", "$question", "$question", true, false)]
    [DataRow("1", "$question", null, false, false)]
    [DataRow("2", "$question", "$another-recipient", false, false)]
    [DataRow("1", null, "$question", false, false)]
    [DataRow("1", null, null, false, false)]
    [DataRow("take it please", "$question", null, false, false)]
    public Task KeywordsAndExactlyBoundNumbersUseTheExistingAnswerPath(string body, string? storedEvent, string? replyEvent, bool answers, bool take) =>
        ExerciseAsync(body, storedEvent, replyEvent, answers, take);

    [TestMethod]
    public Task ALinkedNonparticipantCannotAnswerEvenWithAQuestionCommunication() =>
        ExerciseAsync("take", "$question", null, false, false, nonparticipant: true);

    [TestMethod]
    public Task AReplyToAnEarlierQuestionCannotAuthorizeAReopenedQuestion() =>
        ExerciseAsync("2", "$question", "$question", true, false, reopen: true);

    private static async Task ExerciseAsync(string body, string? storedEvent, string? replyEvent, bool answers, bool take, bool nonparticipant = false, bool reopen = false)
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var matrix = new QuestionMatrixClient();
        var conversation = new QuestionConversation();
        await using var harness = await WaitingHarness.CreateAsync(fixture, configure: services =>
        {
            services.RemoveAll<IMatrixClient>(); services.AddSingleton<IMatrixClient>(matrix);
            services.RemoveAll<IConversationTurnExecutor>(); services.AddSingleton<IConversationTurnExecutor>(conversation);
            services.RemoveAll<ConversationOptions>(); services.AddSingleton(new ConversationOptions { Enabled = true, MatrixEnabled = true });
        });
        var request = await harness.RequestHobbitAsync();
        var collection = WaitingRecheckClient.Collection("collection");
        harness.Client.Script = _ => [collection with { Work = collection.Work with { Title = "The Hobbit / Another Novel" } }];
        await harness.RecheckAsync();
        await using (var scope = harness.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ask = await db.OutboundCommunications.SingleAsync(c => c.CommunicationType == "request.acquisition_question");
            ask.RecordMatrixEventId(storedEvent);
            var settings = new MatrixSettings(DateTimeOffset.UtcNow);
            settings.SetSettings("https://matrix.example.test", "@bot:example.test", null, DateTimeOffset.UtcNow);
            var protector = scope.ServiceProvider.GetRequiredService<ICredentialProtector>();
            settings.SetAccessToken(protector.Protect(CommunicationSecretPurposes.MatrixAccessToken, "test-token"), protector.FormatVersion, null, DateTimeOffset.UtcNow);
            settings.SetEnabled(true, null, DateTimeOffset.UtcNow);
            db.MatrixSettings.Add(settings);
            var linkedUser = nonparticipant ? (await db.Users.SingleAsync(u => u.Email == WebTestFixture.UserEmail)).Id : ask.RecipientUserId;
            if (nonparticipant)
                db.OutboundCommunications.Add(new(linkedUser, "request.acquisition_question", "choice", null, "BookRequest", request.Id, null, DateTimeOffset.UtcNow));
            var destination = new UserMatrixDestination(linkedUser, DateTimeOffset.UtcNow);
            destination.RequestVerification("@reader:example.test", "!reader:example.test", "123456", DateTimeOffset.UtcNow);
            destination.Verify(DateTimeOffset.UtcNow);
            db.UserMatrixDestinations.Add(destination);
            await db.SaveChangesAsync();
        }
        await using (var scope = harness.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<MatrixInboundRouter>().HandleMessageAsync(
                new("!reader:example.test", "@reader:example.test", body, replyEvent, "$incoming"), CancellationToken.None);
        Assert.AreEqual(answers || nonparticipant ? 0 : 1, conversation.Calls);
        Assert.AreEqual(answers && take ? 1 : 0, harness.Client.SubmittedCandidates.Count);
        var state = await harness.ReadAsync(db => db.BookRequests.SingleAsync(r => r.Id == request.Id));
        Assert.AreEqual(answers ? null : AcquisitionQuestionKind.CollectionContaining, state.QuestionKind);
        if (answers)
        {
            StringAssert.Contains(matrix.LastReply, take ? "fetching" : "keep looking");
            await using var scope = harness.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<MatrixInboundRouter>().HandleMessageAsync(
                new("!reader:example.test", "@reader:example.test", "take", EventId: "$stale"), CancellationToken.None);
            StringAssert.Contains(matrix.LastReply, "already answered");
            Assert.AreEqual(take ? 1 : 0, harness.Client.SubmittedCandidates.Count);
        }
        if (reopen)
        {
            await using (var scope = harness.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var current = await db.BookRequests.Include(r => r.Formats).Include(r => r.ReviewCandidates).SingleAsync(r => r.Id == request.Id);
                current.AskQuestion(AcquisitionQuestionKind.CollectionContaining, current.Formats.Single().Id, "Another choice?",
                    [new(current.Formats.Single().Id, "waiting-source", "new-copy", "New collection", "Example Author", "en", null, null, null, false)], DateTimeOffset.UtcNow);
                await db.SaveChangesAsync();
            }
            await using (var scope = harness.Factory.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<MatrixInboundRouter>().HandleMessageAsync(
                    new("!reader:example.test", "@reader:example.test", "1", "$question", "$old-question-reply"), CancellationToken.None);
            Assert.AreEqual(AcquisitionQuestionKind.CollectionContaining,
                await harness.ReadAsync(async db => (await db.BookRequests.SingleAsync(r => r.Id == request.Id)).QuestionKind));
            Assert.IsEmpty(harness.Client.SubmittedCandidates);
        }
    }
}

internal sealed class QuestionConversation : IConversationTurnExecutor
{
    public int Calls { get; private set; }
    public Task<ConversationTurnResult?> RunMatrixTurnAsync(MatrixInboundMessage message, string text, CancellationToken cancellationToken)
    { Calls++; return Task.FromResult<ConversationTurnResult?>(null); }
    public Task MarkDeliveredAsync(ConversationEventKey key, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class QuestionMatrixClient : IMatrixClient
{
    public string? LastReply { get; private set; }
    public Task<SendResult> SendMessageAsync(MatrixSettings settings, string accessToken, string roomId, string text, CancellationToken cancellationToken)
    { LastReply = text; return Task.FromResult(SendResult.Success()); }
    public Task<ConnectionTestOutcome> TestConnectionAsync(MatrixSettings settings, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<MatrixRoomResult> GetOrCreateDirectRoomAsync(MatrixSettings settings, string accessToken, string matrixUserId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<MatrixSyncResult> SyncAsync(MatrixSettings settings, string accessToken, string? since, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<MatrixSendResult> SendRichMessageAsync(MatrixSettings settings, string accessToken, string roomId, string plainBody, string htmlBody, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SendResult> EditMessageAsync(MatrixSettings settings, string accessToken, string roomId, string eventId, string plainBody, string htmlBody, CancellationToken cancellationToken) => throw new NotSupportedException();
}
