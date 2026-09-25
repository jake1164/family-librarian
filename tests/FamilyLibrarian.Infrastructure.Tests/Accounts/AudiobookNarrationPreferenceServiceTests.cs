using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Accounts;

namespace FamilyLibrarian.Infrastructure.Tests.Accounts;

[TestClass]
public sealed class AudiobookNarrationPreferenceServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task GetReturnsNullWhenNoOneIsSignedIn()
    {
        var service = Create(new FakeAccountStore(), userId: null);

        var preference = await service.GetAsync(CancellationToken.None);

        Assert.IsNull(preference);
    }

    [TestMethod]
    public async Task GetReturnsTheAccountsCurrentPreference()
    {
        var store = new FakeAccountStore();
        var userId = store.Seed(AudiobookNarrationPreference.HumanOnly);
        var service = Create(store, userId);

        var preference = await service.GetAsync(CancellationToken.None);

        Assert.AreEqual(AudiobookNarrationPreference.HumanOnly, preference);
    }

    [TestMethod]
    public async Task ANewAccountDefaultsToPreferHuman()
    {
        var store = new FakeAccountStore();
        var userId = store.Seed(AudiobookNarrationPreference.PreferHuman);
        var service = Create(store, userId);

        var preference = await service.GetAsync(CancellationToken.None);

        Assert.AreEqual(AudiobookNarrationPreference.PreferHuman, preference);
    }

    [TestMethod]
    public async Task SetFailsWhenNoOneIsSignedIn()
    {
        var service = Create(new FakeAccountStore(), userId: null);

        var result = await service.SetAsync(AudiobookNarrationPreference.NoPreference, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task SetRejectsAnUndefinedPreference()
    {
        var store = new FakeAccountStore();
        var userId = store.Seed(AudiobookNarrationPreference.PreferHuman);
        var service = Create(store, userId);

        var result = await service.SetAsync((AudiobookNarrationPreference)99, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task SetPersistsTheNewPreferenceAndAuditsIt()
    {
        var store = new FakeAccountStore();
        var userId = store.Seed(AudiobookNarrationPreference.PreferHuman);
        var audit = new RecordingAuditWriter();
        var service = Create(store, userId, audit);

        var result = await service.SetAsync(AudiobookNarrationPreference.HumanOnly, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(AudiobookNarrationPreference.HumanOnly, store.StoredPreference(userId));
        Assert.HasCount(1, audit.Writes);
    }

    private static AudiobookNarrationPreferenceService Create(
        IUserAccountStore store, Guid? userId, IAuditWriter? audit = null) =>
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
        private readonly Dictionary<Guid, UserAccount> _accounts = [];

        public Guid Seed(AudiobookNarrationPreference preference)
        {
            var account = new UserAccount(
                Guid.NewGuid(), "member@example.test", "Family member", UserStatus.Active, false, Now, null, preference);
            _accounts[account.Id] = account;
            return account.Id;
        }

        public AudiobookNarrationPreference StoredPreference(Guid userId) => _accounts[userId].AudiobookNarrationPreference;

        public Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UserAccount>>(_accounts.Values.ToArray());

        public Task<UserAccount?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(_accounts.GetValueOrDefault(userId));

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
            Guid userId, AudiobookNarrationPreference preference, CancellationToken cancellationToken)
        {
            if (!_accounts.TryGetValue(userId, out var account))
            {
                return Task.FromResult(AccountOperationResult.Failure("That account no longer exists."));
            }

            _accounts[userId] = account with { AudiobookNarrationPreference = preference };
            return Task.FromResult(AccountOperationResult.Success(userId));
        }

        public Task<IReadOnlyList<AdminAccountSummary>> ListActiveAdminsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<QuietHours?> GetQuietHoursAsync(Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AccountOperationResult> SetQuietHoursAsync(
            Guid userId, QuietHours? quietHours, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
