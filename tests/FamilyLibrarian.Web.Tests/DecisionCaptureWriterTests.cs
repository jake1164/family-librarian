using FamilyLibrarian.Application.DecisionCapture;
using FamilyLibrarian.Application.ModelTuning;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

/// <summary>C1-b over a real PostgreSQL schema and the real host wiring: the writer, the recorder and the dependency graph.</summary>
[TestClass]
public sealed class DecisionCaptureWriterTests
{
    private static readonly string[] BothAnswers = ["Search", "GiveUp"];
    private static WebTestFixture? fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (fixture is not null) await fixture.DisposeAsync();
    }

    private static FamilyLibrarianAppFactory Factory() => new(WebTestFixture.Require(fixture).ConnectionString);

    private static CapturedDecision Decision(string subject, string reply = "keep", string key = "c1") => new(
        ModelRole.SearchGuidance,
        new SearchCaseInput(1, new TuningIdentity("The Lantern Road", "Mara Quill", [], [], [], null, null, null, []), [], ["Lantern Road"]),
        subject, null, null, null, false,
        new DecisionCaptureRun(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), false, "local/alias", "provider/real", ModelReasoningMode.Low,
            "rendered-messages/1:" + new string('a', 64), 1000, "search-guidance/1", ModelCallOutcome.Succeeded, null, reply),
        [new CapturedCandidateLink(key, "example-source", "result-" + key)]);

    [TestMethod]
    public async Task TheRecorderFromTheRealHostWritesASnapshotCallAndLinkAndTheFullHashSurvives()
    {
        await using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        var sink = scope.ServiceProvider.GetRequiredService<IDecisionCaptureSink>();
        var subject = "subject-" + Guid.NewGuid().ToString("N");

        await sink.CaptureAsync(Decision(subject), CancellationToken.None);

        await using var verify = factory.Services.CreateAsyncScope();
        var database = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var snapshot = await database.DecisionSnapshots.SingleAsync(s => s.SubjectReference == subject);
        var call = await database.DecisionCalls.SingleAsync(c => c.SnapshotId == snapshot.Id);
        Assert.AreEqual("rendered-messages/1:" + new string('a', 64), call.RenderedMessagesHash, "The 84-character hash is stored whole.");
        Assert.AreEqual("provider/real", call.ReportedModel);
        Assert.AreEqual(ModelReasoningMode.Low, call.Reasoning);
        Assert.AreEqual(1, await database.CandidateLinks.CountAsync(link => link.SnapshotId == snapshot.Id));
        Assert.AreEqual(DecisionCaptureRecorder.StaleRenderMismatch, snapshot.StaleReason,
            "This hand-built decision's hash was never produced by a real prompt, so the round trip honestly reports it.");
    }

    [TestMethod]
    public async Task TheSameDecisionSeenAgainAddsACallAndASightingNotASecondSnapshotOrLink()
    {
        await using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        var sink = scope.ServiceProvider.GetRequiredService<IDecisionCaptureSink>();
        var subject = "subject-" + Guid.NewGuid().ToString("N");

        await sink.CaptureAsync(Decision(subject, reply: "Search"), CancellationToken.None);
        await sink.CaptureAsync(Decision(subject, reply: "GiveUp"), CancellationToken.None);

        await using var verify = factory.Services.CreateAsyncScope();
        var database = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var snapshot = await database.DecisionSnapshots.SingleAsync(s => s.SubjectReference == subject);
        Assert.AreEqual(2, snapshot.TimesSeen);
        Assert.AreEqual(2, await database.DecisionCalls.CountAsync(c => c.SnapshotId == snapshot.Id));
        CollectionAssert.AreEquivalent(BothAnswers, await database.DecisionCalls.Where(c => c.SnapshotId == snapshot.Id).Select(c => c.Answer!).ToListAsync());
        Assert.AreEqual(1, await database.CandidateLinks.CountAsync(link => link.SnapshotId == snapshot.Id));
    }

    [TestMethod]
    public async Task ADifferentSubjectWithTheSameContentIsASeparateDecision()
    {
        await using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        var sink = scope.ServiceProvider.GetRequiredService<IDecisionCaptureSink>();
        var a = "subject-a-" + Guid.NewGuid().ToString("N");
        var b = "subject-b-" + Guid.NewGuid().ToString("N");

        await sink.CaptureAsync(Decision(a), CancellationToken.None);
        await sink.CaptureAsync(Decision(b), CancellationToken.None);

        await using var verify = factory.Services.CreateAsyncScope();
        var database = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(1, await database.DecisionSnapshots.CountAsync(s => s.SubjectReference == a));
        Assert.AreEqual(1, await database.DecisionSnapshots.CountAsync(s => s.SubjectReference == b));
    }

    [TestMethod]
    public async Task ConcurrentIdenticalCapturesNeverFailAndNeverDuplicateTheSnapshot()
    {
        await using var factory = Factory();
        var subject = "subject-" + Guid.NewGuid().ToString("N");

        await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IDecisionCaptureSink>().CaptureAsync(Decision(subject), CancellationToken.None);
        }));

        await using var verify = factory.Services.CreateAsyncScope();
        var database = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(1, await database.DecisionSnapshots.CountAsync(s => s.SubjectReference == subject));
        var snapshot = await database.DecisionSnapshots.SingleAsync(s => s.SubjectReference == subject);
        Assert.AreEqual(6, await database.DecisionCalls.CountAsync(c => c.SnapshotId == snapshot.Id), "Every call is kept; the unique key only prevents a duplicate snapshot.");
    }

    [TestMethod]
    public async Task CaptureOffInTheDatabaseSettingsWritesNothingThroughTheRealHost()
    {
        await using var factory = Factory();
        var subject = "subject-" + Guid.NewGuid().ToString("N");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settings = await database.ModelGatewaySettingsRows.FirstOrDefaultAsync() ?? new ModelGatewaySettings(DateTimeOffset.UtcNow);
            if (database.Entry(settings).State == EntityState.Detached) database.ModelGatewaySettingsRows.Add(settings);
            settings.ConfigureDecisionCapture(false, false, DecisionCaptureLimits.Default, null, DateTimeOffset.UtcNow);
            await database.SaveChangesAsync();
        }

        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IDecisionCaptureSink>().CaptureAsync(Decision(subject), CancellationToken.None);

        await using var verify = factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(0, await db.DecisionSnapshots.CountAsync(s => s.SubjectReference == subject));
        var restore = await db.ModelGatewaySettingsRows.FirstAsync();
        restore.ConfigureDecisionCapture(true, false, DecisionCaptureLimits.Default, null, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
    }
}
