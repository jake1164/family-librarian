using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Accounts;

namespace FamilyLibrarian.Infrastructure.Tests.Accounts;

[TestClass]
public sealed class QuietHoursPreferenceServiceTests
{
    [TestMethod]
    public async Task GetReturnsNullWhenNoOneIsSignedIn()
    {
        var service = Create(new FakeAccountStore(), userId: null);

        var quietHours = await service.GetAsync(CancellationToken.None);

        Assert.IsNull(quietHours);
    }

    [TestMethod]
    public async Task GetReturnsNullWhenTheAccountHasNoQuietHoursSet()
    {
        var store = new FakeAccountStore();
        var userId = store.Seed(null);
        var service = Create(store, userId);

        var quietHours = await service.GetAsync(CancellationToken.None);

        Assert.IsNull(quietHours);
    }

    [TestMethod]
    public async Task SetFailsWhenNoOneIsSignedIn()
    {
        var service = Create(new FakeAccountStore(), userId: null);

        var result = await service.SetAsync("America/New_York", 60, 120, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task SetRejectsAPartiallySpecifiedWindow()
    {
        var store = new FakeAccountStore();
        var userId = store.Seed(null);
        var service = Create(store, userId);

        var result = await service.SetAsync("America/New_York", 60, null, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error, "all be set together");
    }

    [TestMethod]
    public async Task SetRejectsAnInvalidTimeZone()
    {
        var store = new FakeAccountStore();
        var userId = store.Seed(null);
        var service = Create(store, userId);

        var result = await service.SetAsync("Not/A_Real_Zone", 60, 120, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task SetPersistsAValidWindowAndAuditsIt()
    {
        var store = new FakeAccountStore();
        var userId = store.Seed(null);
        var audit = new RecordingAuditWriter();
        var service = Create(store, userId, audit);

        var result = await service.SetAsync("America/New_York", 60, 120, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        var stored = store.StoredQuietHours(userId);
        Assert.IsNotNull(stored);
        Assert.AreEqual("America/New_York", stored.TimeZoneId);
        Assert.AreEqual(60, stored.StartMinute);
        Assert.AreEqual(120, stored.EndMinute);
        Assert.HasCount(1, audit.Writes);
    }

    [TestMethod]
    public async Task SetWithAllNullsClearsAnExistingWindow()
    {
        var store = new FakeAccountStore();
        var userId = store.Seed(new QuietHours("America/New_York", 60, 120));
        var service = Create(store, userId);

        var result = await service.SetAsync(null, null, null, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(store.StoredQuietHours(userId));
    }

    private static QuietHoursPreferenceService Create(IUserAccountStore store, Guid? userId, IAuditWriter? audit = null) =>
        new(store, new StubCurrentUser(userId), audit ?? new RecordingAuditWriter());

    private sealed class StubCurrentUser(Guid? userId) : ICurrentUser
    {
        public Guid? UserId => userId;

        public string? DisplayName => "Family member";
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<(string Action, string SubjectType, string? SubjectId)> Writes { get; } = [];

        public Task WriteAsync(
            string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken)
        {
            Writes.Add((action, subjectType, subjectId));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAccountStore : IUserAccountStore
    {
        private readonly Dictionary<Guid, QuietHours?> _quietHoursByUserId = [];

        public Guid Seed(QuietHours? quietHours)
        {
            var userId = Guid.NewGuid();
            _quietHoursByUserId[userId] = quietHours;
            return userId;
        }

        public QuietHours? StoredQuietHours(Guid userId) => _quietHoursByUserId[userId];

        public Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UserAccount?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UserAccount?> FindByEmailAsync(string email, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> CountAdminsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AccountOperationResult> CreateAsync(
            string email, string displayName, string password, UserStatus status, bool isAdmin,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AccountOperationResult> SetStatusAsync(
            Guid userId, UserStatus status, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AccountOperationResult> SetPasswordAsync(
            Guid userId, string password, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AccountOperationResult> SetAdminAsync(
            Guid userId, bool isAdmin, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AccountOperationResult> SetAudiobookNarrationPreferenceAsync(
            Guid userId, AudiobookNarrationPreference preference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AdminAccountSummary>> ListActiveAdminsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<QuietHours?> GetQuietHoursAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(_quietHoursByUserId.GetValueOrDefault(userId));

        public Task<AccountOperationResult> SetQuietHoursAsync(
            Guid userId, QuietHours? quietHours, CancellationToken cancellationToken)
        {
            _quietHoursByUserId[userId] = quietHours;
            return Task.FromResult(AccountOperationResult.Success(userId));
        }
    }
}
