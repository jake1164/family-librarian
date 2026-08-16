using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Policy;
using FamilyLibrarian.Domain.Policy;

namespace FamilyLibrarian.Infrastructure.Tests.Policy;

[TestClass]
public sealed class AcquisitionPolicyServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task TheEffectiveProfileIsManualChoiceWhenNothingHasEverBeenSaved()
    {
        var context = new TestContext();

        var profileId = await context.Service.GetEffectiveProfileIdAsync(CancellationToken.None);

        Assert.AreEqual(PolicyProfileIds.ManualChoice, profileId);
    }

    [TestMethod]
    public async Task AnUnknownProfileIdIsRejectedWithoutTouchingTheStore()
    {
        var context = new TestContext();

        var result = await context.Service.SetDefaultProfileAsync("not-a-real-profile", CancellationToken.None);

        Assert.AreEqual(AcquisitionPolicyCommandOutcome.Invalid, result.Outcome);
        Assert.AreEqual(0, context.Store.SaveCount);
    }

    [TestMethod]
    public async Task SettingAKnownProfilePersistsAndAudits()
    {
        var context = new TestContext();

        var result = await context.Service.SetDefaultProfileAsync(PolicyProfileIds.LibraryFirst, CancellationToken.None);

        Assert.AreEqual(AcquisitionPolicyCommandOutcome.Success, result.Outcome);
        Assert.AreEqual(PolicyProfileIds.LibraryFirst, result.Status!.DefaultProfileId);
        Assert.AreEqual(1, context.Store.SaveCount);
        Assert.AreEqual(PolicyProfileIds.LibraryFirst, await context.Service.GetEffectiveProfileIdAsync(CancellationToken.None));

        var entry = context.Audit.Entries.Single();
        Assert.AreEqual("acquisition_policy.default_changed", entry.Action);
    }

    private sealed class TestContext
    {
        public TestContext()
        {
            Store = new FakeStore();
            Registry = new FakeRegistry();
            Audit = new RecordingAuditWriter();
            Service = new AcquisitionPolicyService(Store, Registry, Audit, new FixedCurrentUser(), new FixedClock());
        }

        public FakeStore Store { get; }

        public FakeRegistry Registry { get; }

        public RecordingAuditWriter Audit { get; }

        public AcquisitionPolicyService Service { get; }
    }

    private sealed class FakeStore : IAcquisitionPolicySettingsStore
    {
        private AcquisitionPolicySettings? settings;

        public int SaveCount { get; private set; }

        public Task<AcquisitionPolicySettings?> FindAsync(CancellationToken cancellationToken) =>
            Task.FromResult(settings);

        public Task<AcquisitionPolicySettings> GetOrCreateAsync(CancellationToken cancellationToken)
        {
            settings ??= new AcquisitionPolicySettings(Now);
            return Task.FromResult(settings);
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRegistry : IPolicyProfileRegistry
    {
        private static readonly PolicyProfileDescriptor[] Descriptors =
        [
            new PolicyProfileDescriptor(PolicyProfileIds.ManualChoice, "Manual Choice", "No recommendation."),
            new PolicyProfileDescriptor(PolicyProfileIds.LibraryFirst, "Library First", "Prefer owned/borrowed."),
            new PolicyProfileDescriptor(PolicyProfileIds.FreeFirst, "Free First", "Prefer free."),
            new PolicyProfileDescriptor(PolicyProfileIds.LowestCost, "Lowest Cost", "Prefer cheapest.")
        ];

        public IReadOnlyList<PolicyProfileDescriptor> GetProfiles() => Descriptors;

        public PolicyProfileDescriptor? Find(string profileId) =>
            Descriptors.FirstOrDefault(descriptor => descriptor.Id == profileId);
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<(string Action, string SubjectType, string? SubjectId, object? Detail)> Entries { get; } = [];

        public Task WriteAsync(
            string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken)
        {
            Entries.Add((action, subjectType, subjectId, detail));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedCurrentUser : ICurrentUser
    {
        public Guid? UserId => Guid.Parse("11111111-1111-1111-1111-111111111111");

        public string? DisplayName => "Test Admin";
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
