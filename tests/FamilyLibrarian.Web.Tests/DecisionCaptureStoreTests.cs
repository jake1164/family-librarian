using System.Text.Json;
using FamilyLibrarian.Application.DecisionCapture;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.DecisionCapture;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.DecisionCapture;
using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Web.Tests;

/// <summary>A6-TUNER-C1-a over a real PostgreSQL schema: tables, cascade, retention order, purge and the counts-only insight.</summary>
[TestClass]
public sealed class DecisionCaptureStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private const string Sentinel = "SENTINEL-TITLE-must-never-leave-the-database";

    private static Task<string> NewDatabaseAsync()
    {
        if (PostgresFixture.UnavailableReason is not null) Assert.Inconclusive(PostgresFixture.UnavailableReason);
        return PostgresFixture.CreateMigratedDatabaseAsync();
    }

    private static AppDbContext Open(string connection) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(connection, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "identity")).Options);

    private static DecisionSnapshot Snapshot(
        ModelRole role, DateTimeOffset seen, string? subject = null, Guid? requestId = null, Guid? userId = null, string json = "{\"v\":1}") =>
        new(role, 1, subject ?? $"subject-{Guid.NewGuid():N}", $"digest-{Guid.NewGuid():N}", json, requestId, userId, null, false, seen);

    private static async Task<DecisionSnapshot> AddAsync(AppDbContext database, DecisionSnapshot snapshot, DecisionOutcomeStrength? outcome = null)
    {
        database.DecisionSnapshots.Add(snapshot);
        await database.SaveChangesAsync();
        if (outcome is { } strength)
        {
            database.DecisionOutcomes.Add(new DecisionOutcome(
                snapshot.Id, null, DecisionOutcomeKind.FileApproved, "approval-service", false, strength, snapshot.LastSeenAtUtc));
            await database.SaveChangesAsync();
        }

        return snapshot;
    }

    private static DecisionCall Call(Guid snapshotId, DateTimeOffset at) =>
        new(snapshotId, null, null, null, ModelPlacement.Local, "local/alias", "reported/model", ModelReasoningMode.Low,
            "rendered-messages/1:abc", "parser/1", ModelCallOutcome.Succeeded, null, "keep", at);

    private static async Task<Guid> SeedUserAsync(AppDbContext database)
    {
        var user = new AppUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), Email = "reader@example.test", DisplayName = "Reader" };
        database.Users.Add(user);
        await database.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<(Guid UserId, Guid RequestId)> SeedRequestAsync(AppDbContext database)
    {
        var userId = await SeedUserAsync(database);
        var work = new Work("Book", null, null, null, PublicationStatus.Unknown, Now);
        database.Works.Add(work);
        await database.SaveChangesAsync();
        var request = new BookRequest(userId, work.Id, [RequestMediaType.Ebook], null, Now);
        database.BookRequests.Add(request);
        await database.SaveChangesAsync();
        return (userId, request.Id);
    }

    [TestMethod]
    public async Task InsightCountsEveryTableAndRoleAndExposesNoSnapshotContent()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        var triageWithOutcome = await AddAsync(database, Snapshot(ModelRole.CandidateTriage, Now.AddDays(-3), json: $"{{\"title\":\"{Sentinel}\"}}"), DecisionOutcomeStrength.Strong);
        await AddAsync(database, Snapshot(ModelRole.CandidateTriage, Now.AddDays(-2)));
        await AddAsync(database, Snapshot(ModelRole.SearchGuidance, Now.AddDays(-1)));
        database.DecisionCalls.AddRange(Call(triageWithOutcome.Id, Now), Call(triageWithOutcome.Id, Now.AddMinutes(1)));
        database.CandidateLinks.Add(new CandidateLink(triageWithOutcome.Id, "c1", "example-source", "result-1"));
        database.SemanticFindings.Add(new SemanticFinding(SemanticSubjectKind.CandidateTriage, "s", null, null, "schema", "digest", "decision",
            ModelRole.CandidateTriage, "m", false, ModelCallOutcome.Succeeded, "keep", "[]", null, Now));
        database.ModelUsageRecords.Add(new ModelUsageRecord(ModelRole.CandidateTriage, ModelBudgetClass.BackgroundValidation, null, "m", false, 10, Now));
        await database.SaveChangesAsync();

        var insight = await new DecisionCaptureStore(database).GetInsightAsync(Now, CancellationToken.None);

        Assert.AreEqual(6, insight.Roles.Count, "Every captured role is listed, including those with nothing saved.");
        var triage = insight.Roles.Single(role => role.Role == ModelRole.CandidateTriage);
        Assert.AreEqual(2, triage.Snapshots);
        Assert.AreEqual(1, triage.WithOutcomes);
        Assert.AreEqual(1, triage.WithoutOutcomes);
        Assert.AreEqual(Now.AddDays(-3), triage.Oldest);
        Assert.AreEqual(Now.AddDays(-2), triage.Newest);
        Assert.IsTrue(triage.ApproximateBytes > 0);
        Assert.AreEqual(1, insight.Roles.Single(role => role.Role == ModelRole.SearchGuidance).Snapshots);
        Assert.AreEqual(0, insight.Roles.Single(role => role.Role == ModelRole.IntentInterpretation).Snapshots);
        long Rows(string table) => insight.Tables.Single(t => t.Table == table).Rows;
        Assert.AreEqual(3, Rows("Decision snapshots"));
        Assert.AreEqual(2, Rows("Calls against snapshots"));
        Assert.AreEqual(1, Rows("Observed outcomes"));
        Assert.AreEqual(1, Rows("Candidate links"));
        Assert.AreEqual(1, Rows("Findings"));
        Assert.AreEqual(1, Rows("Usage ledger"));
        Assert.IsTrue(insight.SizesAvailable);
        Assert.IsTrue(insight.Tables.All(table => table.ApproximateBytes > 0), "Every table, even a small one, reports its approximate size.");
        Assert.AreEqual(insight.Tables.Sum(table => table.ApproximateBytes), insight.TotalApproximateBytes);
        Assert.IsFalse(JsonSerializer.Serialize(insight).Contains(Sentinel, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OverTheCapUnlabeledGoFirstThenWeakThenStrongOldestFirstAndEachRoleIsCappedSeparately()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        var strong = new List<DecisionSnapshot>();
        var weak = new List<DecisionSnapshot>();
        var unlabeled = new List<DecisionSnapshot>();
        // 60 triage snapshots against a cap of 50: 37 strong (oldest), 20 weak, 3 unlabeled (newest).
        for (var i = 0; i < 37; i++) strong.Add(await AddAsync(database, Snapshot(ModelRole.CandidateTriage, Now.AddDays(-100 + i)), DecisionOutcomeStrength.Strong));
        for (var i = 0; i < 20; i++) weak.Add(await AddAsync(database, Snapshot(ModelRole.CandidateTriage, Now.AddDays(-60 + i)), DecisionOutcomeStrength.Weak));
        for (var i = 0; i < 3; i++) unlabeled.Add(await AddAsync(database, Snapshot(ModelRole.CandidateTriage, Now.AddDays(-10 + i))));
        // A second role under its own cap is untouched however full the first is.
        for (var i = 0; i < 40; i++) await AddAsync(database, Snapshot(ModelRole.SearchGuidance, Now.AddDays(-20 + i % 10)));
        var limits = DecisionCaptureLimits.Default with { SnapshotsPerRole = 50, UnlabeledSnapshotDays = 730 };
        var store = new DecisionCaptureStore(database);

        var preview = await store.CountRetentionAsync(limits, Now, CancellationToken.None);
        Assert.AreEqual(10, preview.Snapshots);
        Assert.AreEqual(100, await database.DecisionSnapshots.CountAsync(), "Counting removes nothing.");

        var removed = await store.RunRetentionAsync(limits, Now, CancellationToken.None);

        Assert.AreEqual(10, removed.Snapshots);
        database.ChangeTracker.Clear();
        var remaining = (await database.DecisionSnapshots.Select(s => s.Id).ToListAsync()).ToHashSet();
        Assert.IsTrue(unlabeled.All(s => !remaining.Contains(s.Id)), "Every unlabeled snapshot goes first, although they are the newest.");
        Assert.IsTrue(weak.Take(7).All(s => !remaining.Contains(s.Id)), "Then the oldest weakly labeled ones.");
        Assert.IsTrue(weak.Skip(7).All(s => remaining.Contains(s.Id)));
        Assert.IsTrue(strong.All(s => remaining.Contains(s.Id)), "Strongly labeled snapshots are kept longest.");
        Assert.AreEqual(50, await database.DecisionSnapshots.CountAsync(s => s.Role == ModelRole.CandidateTriage), "The cap holds exactly.");
        Assert.AreEqual(40, await database.DecisionSnapshots.CountAsync(s => s.Role == ModelRole.SearchGuidance));
        Assert.AreEqual(50, await database.DecisionOutcomes.CountAsync(), "Removed snapshots took their own outcomes with them, and only those.");
    }

    [TestMethod]
    public async Task StrongOnesAreOnlyRemovedOnceEverythingWeakerIsGone()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        var strong = new List<DecisionSnapshot>();
        for (var i = 0; i < 52; i++) strong.Add(await AddAsync(database, Snapshot(ModelRole.ArtifactValidationAssessment, Now.AddDays(-60 + i)), DecisionOutcomeStrength.Strong));

        await new DecisionCaptureStore(database).RunRetentionAsync(DecisionCaptureLimits.Default with { SnapshotsPerRole = 50 }, Now, CancellationToken.None);

        database.ChangeTracker.Clear();
        var remaining = (await database.DecisionSnapshots.Select(s => s.Id).ToListAsync()).ToHashSet();
        Assert.IsFalse(remaining.Contains(strong[0].Id));
        Assert.IsFalse(remaining.Contains(strong[1].Id));
        Assert.IsTrue(remaining.Contains(strong[2].Id), "The two oldest strong snapshots went, then the cap held.");
        Assert.AreEqual(50, remaining.Count);
    }

    [TestMethod]
    public async Task TheUnlabeledAgeRuleNeverRemovesALabeledSnapshotButAnOptionalLabeledAgeDoes()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        var oldUnlabeled = await AddAsync(database, Snapshot(ModelRole.CandidateTriage, Now.AddDays(-200)));
        var oldLabeled = await AddAsync(database, Snapshot(ModelRole.CandidateTriage, Now.AddDays(-200)), DecisionOutcomeStrength.Weak);
        var young = await AddAsync(database, Snapshot(ModelRole.CandidateTriage, Now.AddDays(-5)));
        var store = new DecisionCaptureStore(database);

        var first = await store.RunRetentionAsync(DecisionCaptureLimits.Default, Now, CancellationToken.None);

        Assert.AreEqual(1, first.Snapshots);
        database.ChangeTracker.Clear();
        var ids = (await database.DecisionSnapshots.Select(s => s.Id).ToListAsync()).ToHashSet();
        Assert.IsFalse(ids.Contains(oldUnlabeled.Id));
        Assert.IsTrue(ids.Contains(oldLabeled.Id), "A labeled snapshot is never evicted by the unlabeled-age rule.");
        Assert.IsTrue(ids.Contains(young.Id));

        var second = await store.RunRetentionAsync(DecisionCaptureLimits.Default with { LabeledSnapshotDays = 90 }, Now, CancellationToken.None);

        Assert.AreEqual(1, second.Snapshots);
        Assert.AreEqual(1, await database.DecisionSnapshots.CountAsync());
    }

    [TestMethod]
    public async Task OnlyTheMostRecentCallsPerSnapshotAreKeptAndOldFindingsAreRemoved()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        var snapshot = await AddAsync(database, Snapshot(ModelRole.SearchGuidance, Now.AddDays(-1)));
        var calls = Enumerable.Range(0, 5).Select(i => Call(snapshot.Id, Now.AddMinutes(i))).ToList();
        database.DecisionCalls.AddRange(calls);
        SemanticFinding Finding(DateTimeOffset at) => new(SemanticSubjectKind.SearchGuidance, "s", null, null, "schema", $"d-{Guid.NewGuid():N}", "decision",
            ModelRole.SearchGuidance, "m", false, ModelCallOutcome.Succeeded, "Search", "[]", null, at);
        database.SemanticFindings.AddRange(Finding(Now.AddDays(-200)), Finding(Now.AddDays(-10)));
        await database.SaveChangesAsync();
        var limits = DecisionCaptureLimits.Default with { CallsPerSnapshot = 3 };
        var store = new DecisionCaptureStore(database);

        var preview = await store.CountRetentionAsync(limits, Now, CancellationToken.None);
        Assert.AreEqual(2, preview.Calls);
        Assert.AreEqual(1, preview.Findings);
        Assert.AreEqual(5, await database.DecisionCalls.CountAsync());

        var removed = await store.RunRetentionAsync(limits, Now, CancellationToken.None);

        Assert.AreEqual(new DecisionCaptureRetentionCounts(0, 2, 1), removed);
        database.ChangeTracker.Clear();
        var kept = (await database.DecisionCalls.Select(c => c.Id).ToListAsync()).ToHashSet();
        Assert.IsTrue(kept.SetEquals(calls.Skip(2).Select(c => c.Id)), "The three newest calls are kept.");
        Assert.AreEqual(1, await database.SemanticFindings.CountAsync());
        Assert.AreEqual(1, await database.DecisionSnapshots.CountAsync());
    }

    [TestMethod]
    public async Task PurgeByRoleRemovesThatRolesSnapshotsCallsOutcomesAndLinksAndNothingElse()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        var triage = await AddAsync(database, Snapshot(ModelRole.CandidateTriage, Now), DecisionOutcomeStrength.Strong);
        var search = await AddAsync(database, Snapshot(ModelRole.SearchGuidance, Now), DecisionOutcomeStrength.Weak);
        database.DecisionCalls.AddRange(Call(triage.Id, Now), Call(triage.Id, Now.AddMinutes(1)), Call(search.Id, Now));
        database.CandidateLinks.AddRange(new CandidateLink(triage.Id, "c1", "example-source", "r1"), new CandidateLink(search.Id, "c1", "example-source", "r2"));
        database.ModelUsageRecords.Add(new ModelUsageRecord(ModelRole.CandidateTriage, ModelBudgetClass.BackgroundValidation, null, "m", false, 10, Now));
        await database.SaveChangesAsync();
        var store = new DecisionCaptureStore(database);

        var purged = await store.PurgeAsync(ModelRole.CandidateTriage, CancellationToken.None);

        Assert.AreEqual(new DecisionCapturePurgeResult(1, 2, 1, 1), purged);
        database.ChangeTracker.Clear();
        Assert.AreEqual(search.Id, (await database.DecisionSnapshots.SingleAsync()).Id);
        Assert.AreEqual(1, await database.DecisionCalls.CountAsync());
        Assert.AreEqual(1, await database.DecisionOutcomes.CountAsync());
        Assert.AreEqual(1, await database.CandidateLinks.CountAsync());
        Assert.AreEqual(1, await database.ModelUsageRecords.CountAsync(), "The usage ledger is not part of a purge.");

        var everything = await store.PurgeAsync(null, CancellationToken.None);

        Assert.AreEqual(new DecisionCapturePurgeResult(1, 1, 1, 1), everything);
        Assert.AreEqual(0, await database.DecisionSnapshots.CountAsync());
    }

    [TestMethod]
    public async Task DeletingARequestOrAUserCascadesToItsSnapshotsCallsOutcomesAndLinks()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        var (_, requestId) = await SeedRequestAsync(database);
        var intentUser = await SeedUserAsync(database);
        var byRequest = await AddAsync(database, Snapshot(ModelRole.CandidateTriage, Now, requestId: requestId), DecisionOutcomeStrength.Strong);
        var byUser = await AddAsync(database, Snapshot(ModelRole.IntentInterpretation, Now, userId: intentUser));
        var kept = await AddAsync(database, Snapshot(ModelRole.SearchGuidance, Now));
        database.DecisionCalls.AddRange(Call(byRequest.Id, Now), Call(byUser.Id, Now), Call(kept.Id, Now));
        database.CandidateLinks.Add(new CandidateLink(byRequest.Id, "c1", "example-source", "r1"));
        await database.SaveChangesAsync();

        await database.BookRequests.Where(request => request.Id == requestId).ExecuteDeleteAsync();
        await database.Users.Where(user => user.Id == intentUser).ExecuteDeleteAsync();

        Assert.AreEqual(kept.Id, (await database.DecisionSnapshots.SingleAsync()).Id);
        Assert.AreEqual(1, await database.DecisionCalls.CountAsync());
        Assert.AreEqual(0, await database.DecisionOutcomes.CountAsync());
        Assert.AreEqual(0, await database.CandidateLinks.CountAsync());
    }

    [TestMethod]
    public async Task ASnapshotKeyIsUniquePerSubjectSoTwoRequestsWithTheSameContentStaySeparate()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        database.DecisionSnapshots.Add(new DecisionSnapshot(ModelRole.CandidateTriage, 1, "request:a", "same-digest", "{}", null, null, null, false, Now));
        database.DecisionSnapshots.Add(new DecisionSnapshot(ModelRole.CandidateTriage, 1, "request:b", "same-digest", "{}", null, null, null, false, Now));
        await database.SaveChangesAsync();

        database.DecisionSnapshots.Add(new DecisionSnapshot(ModelRole.CandidateTriage, 1, "request:a", "same-digest", "{}", null, null, null, false, Now));
        await Assert.ThrowsExactlyAsync<DbUpdateException>(() => database.SaveChangesAsync());
    }

    [TestMethod]
    public async Task ACallRoundTripsItsFullRunIdentity()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        var snapshot = await AddAsync(database, Snapshot(ModelRole.CandidateTriage, Now));
        var settingsId = Guid.NewGuid();
        var call = new DecisionCall(snapshot.Id, Guid.NewGuid(), Guid.NewGuid(), settingsId, ModelPlacement.Cloud, "cloud/alias", "provider/real",
            ModelReasoningMode.Disabled, "rendered-messages/1:" + new string('a', 64), "triage-parser/3", ModelCallOutcome.SchemaRejected,
            ModelFailureReason.InvalidStructure, "veto", Now);
        database.DecisionCalls.Add(call);
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        var loaded = await database.DecisionCalls.SingleAsync();

        Assert.AreEqual(settingsId, loaded.SettingsId);
        Assert.AreEqual(ModelPlacement.Cloud, loaded.Placement);
        Assert.AreEqual("provider/real", loaded.ReportedModel);
        Assert.AreEqual(ModelReasoningMode.Disabled, loaded.Reasoning);
        Assert.AreEqual(call.RenderedMessagesHash, loaded.RenderedMessagesHash);
        Assert.AreEqual(ModelFailureReason.InvalidStructure, loaded.FailureReason);
        Assert.IsFalse(database.Database.HasPendingModelChanges());
    }

    [TestMethod]
    public void ASnapshotLargerThanTheSizeGuardIsRefusedAtConstruction()
    {
        var oversized = "{\"x\":\"" + new string('x', DecisionCaptureLimits.MaxSnapshotBytes) + "\"}";

        Assert.ThrowsExactly<ArgumentException>(() =>
            new DecisionSnapshot(ModelRole.CandidateTriage, 1, "s", "d", oversized, null, null, null, false, Now));
    }
}
