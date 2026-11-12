using FamilyLibrarian.Domain.DecisionCapture;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.DecisionCapture;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Web.Tests;

/// <summary>C1-c eviction, completed: disputed decisions are kept longest and strongly labeled ones are balanced across outcome kinds.</summary>
[TestClass]
public sealed class DecisionRetentionOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DecisionCaptureLimits Cap50 = DecisionCaptureLimits.Default with { SnapshotsPerRole = 50, UnlabeledSnapshotDays = 730 };

    private static Task<string> NewDatabaseAsync()
    {
        if (PostgresFixture.UnavailableReason is not null) Assert.Inconclusive(PostgresFixture.UnavailableReason);
        return PostgresFixture.CreateMigratedDatabaseAsync();
    }

    private static AppDbContext Open(string connection) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(connection, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "identity")).Options);

    private static async Task<DecisionSnapshot> AddAsync(AppDbContext database, int ageDays, params (DecisionOutcomeKind Kind, DecisionOutcomeStrength Strength)[] facts)
    {
        var seen = Now.AddDays(-ageDays);
        var snapshot = new DecisionSnapshot(ModelRole.CandidateTriage, 1, "s-" + Guid.NewGuid().ToString("N"), "d-" + Guid.NewGuid().ToString("N"), "{}",
            null, null, null, false, seen);
        database.DecisionSnapshots.Add(snapshot);
        foreach (var (kind, strength) in facts) database.DecisionOutcomes.Add(new DecisionOutcome(snapshot.Id, null, kind, "test", false, strength, seen));
        await database.SaveChangesAsync();
        return snapshot;
    }

    [TestMethod]
    public async Task ADisputedDecisionSurvivesEvenWhenItIsTheOldest()
    {
        await using var database = Open(await NewDatabaseAsync());
        // The oldest snapshot has both a good and a bad fact attached; 51 plain strongly labeled ones are newer. The cap is 50.
        var disputed = await AddAsync(database, 90,
            (DecisionOutcomeKind.FileApproved, DecisionOutcomeStrength.Strong), (DecisionOutcomeKind.FileRejected, DecisionOutcomeStrength.Strong));
        var plain = new List<DecisionSnapshot>();
        for (var i = 0; i < 51; i++) plain.Add(await AddAsync(database, 60 - i, (DecisionOutcomeKind.FileApproved, DecisionOutcomeStrength.Strong)));

        var removed = await new DecisionCaptureStore(database).RunRetentionAsync(Cap50, Now, CancellationToken.None);

        Assert.AreEqual(2, removed.Snapshots);
        database.ChangeTracker.Clear();
        var kept = (await database.DecisionSnapshots.Select(s => s.Id).ToListAsync()).ToHashSet();
        Assert.IsTrue(kept.Contains(disputed.Id), "A disputed decision is the most useful to keep, so it goes last however old it is.");
        Assert.IsFalse(kept.Contains(plain[0].Id));
        Assert.IsFalse(kept.Contains(plain[1].Id), "The two oldest undisputed ones went.");
    }

    [TestMethod]
    public async Task StrongDecisionsAreEvictedFromTheMostCommonOutcomeKindFirstSoARareKindSurvives()
    {
        await using var database = Open(await NewDatabaseAsync());
        var rare = new List<DecisionSnapshot>();
        for (var i = 0; i < 10; i++) rare.Add(await AddAsync(database, 100 - i, (DecisionOutcomeKind.VerificationFailed, DecisionOutcomeStrength.Strong)));
        var common = new List<DecisionSnapshot>();
        for (var i = 0; i < 50; i++) common.Add(await AddAsync(database, 60 - i, (DecisionOutcomeKind.FileApproved, DecisionOutcomeStrength.Strong)));

        // 60 over a cap of 50. Oldest-first would take the ten rare ones; balancing takes the ten oldest of the common kind.
        var removed = await new DecisionCaptureStore(database).RunRetentionAsync(Cap50, Now, CancellationToken.None);

        Assert.AreEqual(10, removed.Snapshots);
        database.ChangeTracker.Clear();
        var kept = (await database.DecisionSnapshots.Select(s => s.Id).ToListAsync()).ToHashSet();
        Assert.IsTrue(rare.All(s => kept.Contains(s.Id)), "The rare outcome kind is kept in full.");
        Assert.IsTrue(common.Take(10).All(s => !kept.Contains(s.Id)), "The ten oldest of the common kind went.");
        Assert.AreEqual(50, kept.Count);
    }

    [TestMethod]
    public async Task BalancingKeepsRemovingFromWhicheverKindIsLargestAsTheGroupsShrink()
    {
        await using var database = Open(await NewDatabaseAsync());
        var kindA = new List<DecisionSnapshot>();
        var kindB = new List<DecisionSnapshot>();
        for (var i = 0; i < 30; i++) kindA.Add(await AddAsync(database, 100 - i, (DecisionOutcomeKind.FileApproved, DecisionOutcomeStrength.Strong)));
        for (var i = 0; i < 28; i++) kindB.Add(await AddAsync(database, 100 - i, (DecisionOutcomeKind.FileRejected, DecisionOutcomeStrength.Strong)));

        // 58 over 50: eight must go. A (30) gives two to reach 28, then A and B alternate while tied.
        await new DecisionCaptureStore(database).RunRetentionAsync(Cap50, Now, CancellationToken.None);

        database.ChangeTracker.Clear();
        var keptA = await database.DecisionOutcomes.CountAsync(o => o.Kind == DecisionOutcomeKind.FileApproved);
        var keptB = await database.DecisionOutcomes.CountAsync(o => o.Kind == DecisionOutcomeKind.FileRejected);
        Assert.AreEqual(50, keptA + keptB);
        Assert.IsTrue(Math.Abs(keptA - keptB) <= 2, $"The two kinds stay balanced ({keptA} vs {keptB}).");
    }
}
