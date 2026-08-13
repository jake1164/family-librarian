using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Accounts;

namespace FamilyLibrarian.Infrastructure.Tests.Accounts;

/// <summary>
/// The rules that keep an administrator from locking the household out.
/// </summary>
/// <remarks>
/// These matter more than they look: the bootstrap administrator is only created
/// while no administrator exists, so an installation that loses its last usable
/// admin has no route back in short of editing the database.
/// </remarks>
[TestClass]
public sealed class AccountAdminServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task AnAdministratorCannotDisableTheirOwnAccount()
    {
        var store = new RecordingAccountStore();
        var me = store.Seed("me@example.test", isAdmin: true);
        store.Seed("other@example.test", isAdmin: true);
        var service = Create(store, me);

        var result = await service.SetStatusAsync(me, UserStatus.Disabled, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, store.StatusChanges.Count);
    }

    [TestMethod]
    public async Task AnAdministratorCannotRemoveTheirOwnAdminRole()
    {
        var store = new RecordingAccountStore();
        var me = store.Seed("me@example.test", isAdmin: true);
        store.Seed("other@example.test", isAdmin: true);
        var service = Create(store, me);

        var result = await service.SetAdminAsync(me, false, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, store.AdminChanges.Count);
    }

    [TestMethod]
    public async Task TheLastAdministratorCannotBeDisabled()
    {
        var store = new RecordingAccountStore();
        var me = store.Seed("me@example.test", isAdmin: true);
        var onlyAdmin = store.Seed("solo@example.test", isAdmin: true);
        store.AdminCount = 1;
        var service = Create(store, me);

        var result = await service.SetStatusAsync(onlyAdmin, UserStatus.Disabled, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, store.StatusChanges.Count);
    }

    [TestMethod]
    public async Task TheLastAdministratorCannotBeDemoted()
    {
        var store = new RecordingAccountStore();
        var me = store.Seed("me@example.test", isAdmin: true);
        var onlyAdmin = store.Seed("solo@example.test", isAdmin: true);
        store.AdminCount = 1;
        var service = Create(store, me);

        var result = await service.SetAdminAsync(onlyAdmin, false, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, store.AdminChanges.Count);
    }

    [TestMethod]
    public async Task AnotherAdministratorCanBeDisabledWhileOneRemains()
    {
        var store = new RecordingAccountStore();
        var me = store.Seed("me@example.test", isAdmin: true);
        var other = store.Seed("other@example.test", isAdmin: true);
        store.AdminCount = 2;
        var service = Create(store, me);

        var result = await service.SetStatusAsync(other, UserStatus.Disabled, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(new[] { other }, store.StatusChanges.Select(c => c.UserId).ToArray());
    }

    [TestMethod]
    public async Task ANonAdministratorCanAlwaysBeDisabled()
    {
        var store = new RecordingAccountStore();
        var me = store.Seed("me@example.test", isAdmin: true);
        var reader = store.Seed("reader@example.test", isAdmin: false);
        store.AdminCount = 1;
        var service = Create(store, me);

        var result = await service.SetStatusAsync(reader, UserStatus.Disabled, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public async Task EnablingIsNeverBlockedByTheLastAdminRule()
    {
        var store = new RecordingAccountStore();
        var me = store.Seed("me@example.test", isAdmin: true);
        var other = store.Seed("other@example.test", isAdmin: true);
        store.AdminCount = 1;
        var service = Create(store, me);

        // Re-enabling cannot reduce access, so the guardrails do not apply.
        var result = await service.SetStatusAsync(other, UserStatus.Active, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public async Task AnUnknownAccountIsReportedRatherThanChanged()
    {
        var store = new RecordingAccountStore();
        var me = store.Seed("me@example.test", isAdmin: true);
        var service = Create(store, me);

        var status = await service.SetStatusAsync(Guid.NewGuid(), UserStatus.Disabled, CancellationToken.None);
        var admin = await service.SetAdminAsync(Guid.NewGuid(), true, CancellationToken.None);
        var password = await service.SetPasswordAsync(Guid.NewGuid(), "Family-Pass1!x", CancellationToken.None);

        Assert.IsFalse(status.Succeeded);
        Assert.IsFalse(admin.Succeeded);
        Assert.IsFalse(password.Succeeded);
    }

    [TestMethod]
    public async Task AnUndefinedStatusIsRefused()
    {
        var store = new RecordingAccountStore();
        var me = store.Seed("me@example.test", isAdmin: true);
        var service = Create(store, me);

        var result = await service.SetStatusAsync(me, (UserStatus)99, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
    }

    private static AccountAdminService Create(IUserAccountStore store, Guid currentUserId) =>
        new(store, new NullAuditWriter(), new StubCurrentUser(currentUserId));

    private sealed class StubCurrentUser(Guid? userId) : ICurrentUser
    {
        public Guid? UserId => userId;

        public string? DisplayName => "Admin";
    }

    private sealed class NullAuditWriter : IAuditWriter
    {
        public Task WriteAsync(
            string action,
            string subjectType,
            string? subjectId,
            object? detail,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingAccountStore : IUserAccountStore
    {
        private readonly List<UserAccount> _accounts = [];

        public List<(Guid UserId, UserStatus Status)> StatusChanges { get; } = [];

        public List<(Guid UserId, bool IsAdmin)> AdminChanges { get; } = [];

        public int AdminCount { get; set; } = 2;

        public Guid Seed(string email, bool isAdmin)
        {
            var account = new UserAccount(
                Guid.NewGuid(), email, email, UserStatus.Active, isAdmin, Now, null);
            _accounts.Add(account);
            return account.Id;
        }

        public Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UserAccount>>(_accounts.ToArray());

        public Task<UserAccount?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(_accounts.SingleOrDefault(account => account.Id == userId));

        public Task<UserAccount?> FindByEmailAsync(string email, CancellationToken cancellationToken) =>
            Task.FromResult(_accounts.SingleOrDefault(account =>
                string.Equals(account.Email, email, StringComparison.OrdinalIgnoreCase)));

        public Task<int> CountAdminsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(AdminCount);

        public Task<AccountOperationResult> CreateAsync(
            string email,
            string displayName,
            string password,
            UserStatus status,
            bool isAdmin,
            CancellationToken cancellationToken) =>
            Task.FromResult(AccountOperationResult.Success(Guid.NewGuid()));

        public Task<AccountOperationResult> SetStatusAsync(
            Guid userId,
            UserStatus status,
            CancellationToken cancellationToken)
        {
            StatusChanges.Add((userId, status));
            return Task.FromResult(AccountOperationResult.Success(userId));
        }

        public Task<AccountOperationResult> SetPasswordAsync(
            Guid userId,
            string password,
            CancellationToken cancellationToken) =>
            Task.FromResult(AccountOperationResult.Success(userId));

        public Task<AccountOperationResult> SetAdminAsync(
            Guid userId,
            bool isAdmin,
            CancellationToken cancellationToken)
        {
            AdminChanges.Add((userId, isAdmin));
            return Task.FromResult(AccountOperationResult.Success(userId));
        }
    }
}
