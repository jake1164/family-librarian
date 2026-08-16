using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain;
using FamilyLibrarian.Domain.Accounts;

namespace FamilyLibrarian.Infrastructure.Tests.Accounts;

[TestClass]
public sealed class ExternalSignInServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
    private const string Issuer = "https://auth.example.test/application/o/family-librarian/";

    [TestMethod]
    public async Task AnExistingLinkedIdentitySignsInDirectly()
    {
        var context = new TestContext();
        var userId = context.Directory.SeedAccount("reader@example.test", UserStatus.Active, isAdmin: false);
        context.Directory.SeedLink(Issuer, "subject-1", userId);

        var result = await context.Service.SignInAsync(
            new ExternalIdentity(Issuer, "subject-1", "reader@example.test", "Reader", IsAdminClaimMatched: false),
            autoCreateAccounts: false,
            CancellationToken.None);

        Assert.AreEqual(ExternalSignInOutcome.SignedIn, result.Outcome);
        Assert.AreEqual(userId, result.UserId);
    }

    [TestMethod]
    public async Task AMatchingEmailLinksAndGrantsTheAdminRole()
    {
        var context = new TestContext();
        var userId = context.Directory.SeedAccount("member@example.test", UserStatus.Active, isAdmin: false);

        var result = await context.Service.SignInAsync(
            new ExternalIdentity(Issuer, "subject-2", "member@example.test", "Member", IsAdminClaimMatched: true),
            autoCreateAccounts: false,
            CancellationToken.None);

        Assert.AreEqual(ExternalSignInOutcome.SignedIn, result.Outcome);
        Assert.IsTrue((await context.Directory.FindAsync(userId, CancellationToken.None))!.IsAdmin);
        Assert.AreEqual(userId, await context.Directory.FindLinkedUserIdAsync(Issuer, "subject-2", CancellationToken.None));
    }

    [TestMethod]
    public async Task AnAbsentAdminClaimRevokesAnAlreadyLinkedAdmin()
    {
        var context = new TestContext();
        var userId = context.Directory.SeedAccount("admin@example.test", UserStatus.Active, isAdmin: true);
        context.Directory.SeedLink(Issuer, "subject-3", userId);
        // A second admin so the "cannot remove the last administrator" guard
        // does not mask the behavior this test actually checks.
        context.Directory.SeedAccount("other-admin@example.test", UserStatus.Active, isAdmin: true);

        var result = await context.Service.SignInAsync(
            new ExternalIdentity(Issuer, "subject-3", "admin@example.test", "Admin", IsAdminClaimMatched: false),
            autoCreateAccounts: false,
            CancellationToken.None);

        Assert.AreEqual(ExternalSignInOutcome.SignedIn, result.Outcome);
        Assert.IsFalse((await context.Directory.FindAsync(userId, CancellationToken.None))!.IsAdmin);
    }

    [TestMethod]
    public async Task AMatchingRedeemableInvitationCreatesAnActiveAccountAndRedeemsIt()
    {
        var context = new TestContext();
        var invitation = new Invitation(
            "invited@example.test", "hash", RoleNames.User, Guid.NewGuid(), Now, Now.AddDays(7));
        context.Invitations.Seed(invitation);

        var result = await context.Service.SignInAsync(
            new ExternalIdentity(Issuer, "subject-4", "invited@example.test", "Invited", IsAdminClaimMatched: false),
            autoCreateAccounts: false,
            CancellationToken.None);

        Assert.AreEqual(ExternalSignInOutcome.SignedIn, result.Outcome);
        Assert.IsTrue(invitation.IsRedeemed);
        var account = await context.Directory.FindAsync(result.UserId!.Value, CancellationToken.None);
        Assert.AreEqual(UserStatus.Active, account!.Status);
    }

    [TestMethod]
    public async Task AnUnmatchedIdentityWithAutoCreateTrueSignsInImmediately()
    {
        var context = new TestContext();

        var result = await context.Service.SignInAsync(
            new ExternalIdentity(Issuer, "subject-5", "new@example.test", "New Person", IsAdminClaimMatched: false),
            autoCreateAccounts: true,
            CancellationToken.None);

        Assert.AreEqual(ExternalSignInOutcome.SignedIn, result.Outcome);
    }

    [TestMethod]
    public async Task AnUnmatchedIdentityWithAutoCreateFalseWaitsForApproval()
    {
        var context = new TestContext();

        var result = await context.Service.SignInAsync(
            new ExternalIdentity(Issuer, "subject-6", "new@example.test", "New Person", IsAdminClaimMatched: false),
            autoCreateAccounts: false,
            CancellationToken.None);

        Assert.AreEqual(ExternalSignInOutcome.NotActive, result.Outcome);
        Assert.IsNull(result.UserId);
        var created = await context.Directory.FindByEmailAsync("new@example.test", CancellationToken.None);
        Assert.AreEqual(UserStatus.PendingApproval, created!.Status);
    }

    [TestMethod]
    public async Task ADisabledAccountIsRefusedRegardlessOfAValidIdentity()
    {
        var context = new TestContext();
        context.Directory.SeedAccount("disabled@example.test", UserStatus.Disabled, isAdmin: false);

        var result = await context.Service.SignInAsync(
            new ExternalIdentity(Issuer, "subject-7", "disabled@example.test", "Disabled", IsAdminClaimMatched: false),
            autoCreateAccounts: false,
            CancellationToken.None);

        Assert.AreEqual(ExternalSignInOutcome.NotActive, result.Outcome);
    }

    [TestMethod]
    public async Task AMissingSubjectClaimIsRejected()
    {
        var context = new TestContext();

        var result = await context.Service.SignInAsync(
            new ExternalIdentity(Issuer, string.Empty, "someone@example.test", "Someone", IsAdminClaimMatched: false),
            autoCreateAccounts: true,
            CancellationToken.None);

        Assert.AreEqual(ExternalSignInOutcome.Rejected, result.Outcome);
    }

    private sealed class TestContext
    {
        public TestContext()
        {
            Directory = new FakeAccountDirectory();
            Invitations = new FakeInvitationRepository();
            var accountAdmin = new AccountAdminService(Directory, new NoOpAuditWriter(), new NoOneSignedIn());
            Service = new ExternalSignInService(
                Directory, Directory, accountAdmin, Invitations, new NoOpAuditWriter(), new FixedClock());
        }

        public FakeAccountDirectory Directory { get; }

        public FakeInvitationRepository Invitations { get; }

        public ExternalSignInService Service { get; }
    }

    private sealed class FakeAccountDirectory : IExternalLoginStore, IUserAccountStore
    {
        private readonly List<Row> rows = [];
        private readonly Dictionary<(string Issuer, string Subject), Guid> links = [];

        public Guid SeedAccount(string email, UserStatus status, bool isAdmin)
        {
            var row = new Row { Id = Guid.NewGuid(), Email = email, DisplayName = email, Status = status, IsAdmin = isAdmin };
            rows.Add(row);
            return row.Id;
        }

        public void SeedLink(string issuer, string subject, Guid userId) => links[(issuer, subject)] = userId;

        public Task<Guid?> FindLinkedUserIdAsync(string issuer, string subject, CancellationToken cancellationToken) =>
            Task.FromResult(links.TryGetValue((issuer, subject), out var id) ? id : (Guid?)null);

        public Task LinkAsync(
            Guid userId, string issuer, string subject, string? providerDisplayName, CancellationToken cancellationToken)
        {
            links[(issuer, subject)] = userId;
            return Task.CompletedTask;
        }

        public Task<Guid> CreatePasswordlessAsync(
            string email, string displayName, UserStatus status, bool isAdmin,
            string issuer, string subject, CancellationToken cancellationToken)
        {
            var row = new Row { Id = Guid.NewGuid(), Email = email, DisplayName = displayName, Status = status, IsAdmin = isAdmin };
            rows.Add(row);
            links[(issuer, subject)] = row.Id;
            return Task.FromResult(row.Id);
        }

        public Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UserAccount?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(rows.FirstOrDefault(row => row.Id == userId) is { } row ? ToAccount(row) : null);

        public Task<UserAccount?> FindByEmailAsync(string email, CancellationToken cancellationToken) =>
            Task.FromResult(
                rows.FirstOrDefault(row => string.Equals(row.Email, email, StringComparison.OrdinalIgnoreCase))
                    is { } row
                    ? ToAccount(row)
                    : null);

        public Task<int> CountAdminsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(rows.Count(row => row.IsAdmin && UserStatuses.CanSignIn(row.Status)));

        public Task<AccountOperationResult> CreateAsync(
            string email, string displayName, string password, UserStatus status, bool isAdmin,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AccountOperationResult> SetStatusAsync(
            Guid userId, UserStatus status, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AccountOperationResult> SetPasswordAsync(
            Guid userId, string password, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AccountOperationResult> SetAdminAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken)
        {
            var row = rows.FirstOrDefault(candidate => candidate.Id == userId);
            if (row is null)
            {
                return Task.FromResult(AccountOperationResult.Failure("That account no longer exists."));
            }

            row.IsAdmin = isAdmin;
            return Task.FromResult(AccountOperationResult.Success(row.Id));
        }

        private static UserAccount ToAccount(Row row) =>
            new(row.Id, row.Email, row.DisplayName, row.Status, row.IsAdmin, DateTimeOffset.UtcNow, null);

        private sealed class Row
        {
            public Guid Id { get; init; }

            public string Email { get; init; } = "";

            public string DisplayName { get; init; } = "";

            public UserStatus Status { get; set; }

            public bool IsAdmin { get; set; }
        }
    }

    private sealed class FakeInvitationRepository : IInvitationRepository
    {
        private readonly List<Invitation> invitations = [];

        public void Seed(Invitation invitation) => invitations.Add(invitation);

        public Task<IReadOnlyList<Invitation>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Invitation?> FindAsync(Guid invitationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Invitation?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Invitation>> FindOutstandingForEmailAsync(
            string normalizedEmail, DateTimeOffset atUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Invitation>>(
                invitations.Where(invitation => invitation.NormalizedEmail == normalizedEmail).ToArray());

        public void Add(Invitation invitation) => invitations.Add(invitation);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<TResult> InTransactionAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken) =>
            operation(cancellationToken);
    }

    private sealed class NoOpAuditWriter : IAuditWriter
    {
        public Task WriteAsync(
            string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NoOneSignedIn : ICurrentUser
    {
        public Guid? UserId => null;

        public string? DisplayName => null;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
