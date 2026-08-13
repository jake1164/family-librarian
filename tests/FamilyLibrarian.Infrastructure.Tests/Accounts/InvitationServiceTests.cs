using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain;
using FamilyLibrarian.Domain.Accounts;

namespace FamilyLibrarian.Infrastructure.Tests.Accounts;

[TestClass]
public sealed class InvitationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.NewGuid();

    [TestMethod]
    public async Task CreateAsyncReturnsTheTokenOnceAndStoresOnlyItsHash()
    {
        var harness = new Harness();
        var service = harness.Create();

        var result = await service.CreateAsync("reader@example.test", false, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(result.Token);

        var stored = harness.Invitations.Items.Single();
        Assert.AreNotEqual(result.Token, stored.TokenHash);
        Assert.AreEqual(harness.Tokens.Hash(result.Token!), stored.TokenHash);
        Assert.AreEqual(RoleNames.User, stored.Role);
        Assert.AreEqual(Now.AddDays(InvitationPolicy.DefaultLifetimeDays), stored.ExpiresAtUtc);
    }

    [TestMethod]
    public async Task AnInvitationForAnExistingAccountIsRefused()
    {
        var harness = new Harness();
        harness.Accounts.Seed("reader@example.test");
        var service = harness.Create();

        var result = await service.CreateAsync("reader@example.test", false, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, harness.Invitations.Items.Count);
    }

    [TestMethod]
    public async Task ReissuingWithdrawsThePreviousOutstandingInvitation()
    {
        var harness = new Harness();
        var service = harness.Create();

        var first = await service.CreateAsync("reader@example.test", false, CancellationToken.None);
        var second = await service.CreateAsync("reader@example.test", false, CancellationToken.None);

        // Two live tokens for one address would mean a link the administrator
        // thought they had replaced still worked.
        var invitations = harness.Invitations.Items;
        Assert.AreEqual(2, invitations.Count);
        Assert.AreEqual(1, invitations.Count(invitation => invitation.IsRevoked));
        Assert.AreEqual(
            harness.Tokens.Hash(second.Token!),
            invitations.Single(invitation => !invitation.IsRevoked).TokenHash);
        Assert.AreNotEqual(first.Token, second.Token);
    }

    [TestMethod]
    public async Task AnAdminInvitationCarriesTheAdminRole()
    {
        var harness = new Harness();
        var service = harness.Create();

        await service.CreateAsync("second-admin@example.test", true, CancellationToken.None);

        Assert.AreEqual(RoleNames.Admin, harness.Invitations.Items.Single().Role);
    }

    [TestMethod]
    public async Task RedeemingCreatesAnActiveAccountAtTheInvitedAddress()
    {
        var harness = new Harness();
        var service = harness.Create();
        var created = await service.CreateAsync("reader@example.test", false, CancellationToken.None);

        var result = await service.RedeemAsync(
            created.Token!,
            "Reader",
            "Family-Reader-Pass1!",
            CancellationToken.None);

        Assert.AreEqual(RedeemInvitationOutcome.Success, result.Outcome);

        var account = harness.Accounts.Created.Single();
        // The address comes from the invitation, never from the redeemer.
        Assert.AreEqual("reader@example.test", account.Email);
        Assert.AreEqual(UserStatus.Active, account.Status);
        Assert.IsFalse(account.IsAdmin);
        Assert.IsTrue(harness.Invitations.Items.Single().IsRedeemed);
    }

    [TestMethod]
    public async Task RedeemingAnAdminInvitationGrantsTheAdminRole()
    {
        var harness = new Harness();
        var service = harness.Create();
        var created = await service.CreateAsync("boss@example.test", true, CancellationToken.None);

        await service.RedeemAsync(created.Token!, "Boss", "Family-Boss-Pass1!", CancellationToken.None);

        Assert.IsTrue(harness.Accounts.Created.Single().IsAdmin);
    }

    [TestMethod]
    public async Task ASecondRedemptionOfTheSameTokenIsRefused()
    {
        var harness = new Harness();
        var service = harness.Create();
        var created = await service.CreateAsync("reader@example.test", false, CancellationToken.None);
        await service.RedeemAsync(created.Token!, "Reader", "Family-Reader-Pass1!", CancellationToken.None);

        var second = await service.RedeemAsync(
            created.Token!,
            "Impostor",
            "Family-Other-Pass1!",
            CancellationToken.None);

        Assert.AreEqual(RedeemInvitationOutcome.InvalidInvitation, second.Outcome);
        Assert.AreEqual(1, harness.Accounts.Created.Count);
    }

    [TestMethod]
    public async Task AnUnknownTokenAndAWithdrawnOneAnswerIdentically()
    {
        var harness = new Harness();
        var service = harness.Create();
        var created = await service.CreateAsync("reader@example.test", false, CancellationToken.None);
        await service.RevokeAsync(harness.Invitations.Items.Single().Id, CancellationToken.None);

        var revoked = await service.RedeemAsync(
            created.Token!,
            "Reader",
            "Family-Reader-Pass1!",
            CancellationToken.None);
        var unknown = await service.RedeemAsync(
            "not-a-real-token",
            "Reader",
            "Family-Reader-Pass1!",
            CancellationToken.None);

        // Distinguishing them would tell someone probing tokens when they had
        // found a real one.
        Assert.AreEqual(RedeemInvitationOutcome.InvalidInvitation, revoked.Outcome);
        Assert.AreEqual(RedeemInvitationOutcome.InvalidInvitation, unknown.Outcome);
        Assert.AreEqual(0, harness.Accounts.Created.Count);
    }

    [TestMethod]
    public async Task AnExpiredTokenIsRefused()
    {
        var harness = new Harness();
        var service = harness.Create();
        var created = await service.CreateAsync("reader@example.test", false, CancellationToken.None);

        harness.Clock.Now = Now.AddDays(InvitationPolicy.DefaultLifetimeDays).AddSeconds(1);

        var result = await service.RedeemAsync(
            created.Token!,
            "Reader",
            "Family-Reader-Pass1!",
            CancellationToken.None);

        Assert.AreEqual(RedeemInvitationOutcome.InvalidInvitation, result.Outcome);
    }

    [TestMethod]
    public async Task AShortPasswordIsRefusedBeforeTheTokenIsEvenLookedUp()
    {
        var harness = new Harness();
        var service = harness.Create();
        var created = await service.CreateAsync("reader@example.test", false, CancellationToken.None);

        var result = await service.RedeemAsync(created.Token!, "Reader", "short", CancellationToken.None);

        Assert.AreEqual(RedeemInvitationOutcome.Rejected, result.Outcome);
        Assert.IsFalse(harness.Invitations.Items.Single().IsRedeemed);
    }

    [TestMethod]
    public async Task AFailedAccountCreationLeavesTheInvitationUsable()
    {
        var harness = new Harness();
        harness.Accounts.FailCreation = "That password has been seen in a breach.";
        var service = harness.Create();
        var created = await service.CreateAsync("reader@example.test", false, CancellationToken.None);

        var result = await service.RedeemAsync(
            created.Token!,
            "Reader",
            "Family-Reader-Pass1!",
            CancellationToken.None);

        Assert.AreEqual(RedeemInvitationOutcome.Rejected, result.Outcome);
        // Burning the invitation on a rejected password would strand the invitee.
        Assert.IsFalse(harness.Invitations.Items.Single().IsRedeemed);
    }

    [TestMethod]
    public async Task PreviewShowsTheInvitedAddressWithoutRedeeming()
    {
        var harness = new Harness();
        var service = harness.Create();
        var created = await service.CreateAsync("reader@example.test", false, CancellationToken.None);

        var preview = await service.PreviewAsync(created.Token!, CancellationToken.None);

        Assert.IsNotNull(preview);
        Assert.AreEqual("reader@example.test", preview.Email);
        Assert.IsTrue(preview.CanBeRedeemed);
        Assert.IsFalse(harness.Invitations.Items.Single().IsRedeemed);
    }

    [TestMethod]
    public async Task TheInvitedAddressKeepsTheCasingTheAdministratorTyped()
    {
        var harness = new Harness();
        var service = harness.Create();

        await service.CreateAsync("  Reader@Example.Test ", false, CancellationToken.None);

        var stored = harness.Invitations.Items.Single();
        // Display form for the person; upper case only for matching.
        Assert.AreEqual("Reader@Example.Test", stored.Email);
        Assert.AreEqual("READER@EXAMPLE.TEST", stored.NormalizedEmail);
    }

    [TestMethod]
    public async Task ReissuingMatchesRegardlessOfHowTheAddressWasTyped()
    {
        var harness = new Harness();
        var service = harness.Create();

        await service.CreateAsync("reader@example.test", false, CancellationToken.None);
        await service.CreateAsync("READER@Example.TEST", false, CancellationToken.None);

        // Different casing is the same person, so the first link must die.
        Assert.AreEqual(1, harness.Invitations.Items.Count(item => item.IsRevoked));
    }

    [TestMethod]
    public async Task TheConfiguredLifetimeDecidesWhenAnInvitationExpires()
    {
        var harness = new Harness();
        harness.Policy.LifetimeDays = 2;
        var service = harness.Create();

        await service.CreateAsync("reader@example.test", false, CancellationToken.None);

        Assert.AreEqual(Now.AddDays(2), harness.Invitations.Items.Single().ExpiresAtUtc);
    }

    [TestMethod]
    public async Task RegeneratingIssuesANewTokenAndKillsTheOldOne()
    {
        var harness = new Harness();
        var service = harness.Create();
        var original = await service.CreateAsync("reader@example.test", false, CancellationToken.None);
        var originalId = harness.Invitations.Items.Single().Id;

        var replacement = await service.RegenerateAsync(originalId, CancellationToken.None);

        Assert.IsTrue(replacement.Succeeded);
        Assert.AreNotEqual(original.Token, replacement.Token);
        Assert.IsTrue(harness.Invitations.Items.Single(item => item.Id == originalId).IsRevoked);

        // The lost link must stop working the moment its replacement exists.
        var withOldToken = await service.RedeemAsync(
            original.Token!,
            "Reader",
            "Family-Reader-Pass1!",
            CancellationToken.None);
        Assert.AreEqual(RedeemInvitationOutcome.InvalidInvitation, withOldToken.Outcome);

        var withNewToken = await service.RedeemAsync(
            replacement.Token!,
            "Reader",
            "Family-Reader-Pass1!",
            CancellationToken.None);
        Assert.AreEqual(RedeemInvitationOutcome.Success, withNewToken.Outcome);
    }

    [TestMethod]
    public async Task AnExpiredInvitationCanBeRegenerated()
    {
        var harness = new Harness();
        var service = harness.Create();
        await service.CreateAsync("reader@example.test", false, CancellationToken.None);
        var expiredId = harness.Invitations.Items.Single().Id;

        harness.Clock.Now = Now.AddDays(InvitationPolicy.DefaultLifetimeDays).AddDays(1);

        // Expiry is the most likely reason to come back to an invitation.
        var replacement = await service.RegenerateAsync(expiredId, CancellationToken.None);

        Assert.IsTrue(replacement.Succeeded);
        Assert.AreEqual(
            harness.Clock.Now.AddDays(InvitationPolicy.DefaultLifetimeDays),
            harness.Invitations.Items.Single(item => item.Id != expiredId).ExpiresAtUtc);
    }

    [TestMethod]
    public async Task ARedeemedInvitationCannotBeRegenerated()
    {
        var harness = new Harness();
        var service = harness.Create();
        var created = await service.CreateAsync("reader@example.test", false, CancellationToken.None);
        await service.RedeemAsync(created.Token!, "Reader", "Family-Reader-Pass1!", CancellationToken.None);
        var redeemedId = harness.Invitations.Items.Single().Id;

        var result = await service.RegenerateAsync(redeemedId, CancellationToken.None);

        // The account exists; a password reset is the operation that helps.
        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Error);
        Assert.AreEqual(1, harness.Invitations.Items.Count);
    }

    [TestMethod]
    public async Task RegeneratingAnUnknownInvitationIsRefused()
    {
        var result = await new Harness().Create()
            .RegenerateAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task RegeneratingKeepsTheAdminRoleOfTheOriginalInvitation()
    {
        var harness = new Harness();
        var service = harness.Create();
        await service.CreateAsync("boss@example.test", true, CancellationToken.None);
        var originalId = harness.Invitations.Items.Single().Id;

        await service.RegenerateAsync(originalId, CancellationToken.None);

        Assert.AreEqual(
            RoleNames.Admin,
            harness.Invitations.Items.Single(item => item.Id != originalId).Role);
    }

    [TestMethod]
    public async Task PreviewOfAnUnknownTokenRevealsNothing() =>
        Assert.IsNull(await new Harness().Create()
            .PreviewAsync("not-a-real-token", CancellationToken.None));

    private sealed class Harness
    {
        public FakeInvitationRepository Invitations { get; } = new();

        public FakeAccountStore Accounts { get; } = new();

        public FakeTokenGenerator Tokens { get; } = new();

        public MutableClock Clock { get; } = new();

        public InvitationPolicy Policy { get; } = new();

        public InvitationService Create() => new(
            Invitations,
            Accounts,
            Tokens,
            new NullAuditWriter(),
            new StubCurrentUser(Admin),
            Clock,
            Policy);
    }

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset Now { get; set; } = InvitationServiceTests.Now;

        public DateTimeOffset UtcNow => Now;
    }

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

    /// <summary>A deterministic stand-in; the real generator uses a CSPRNG.</summary>
    private sealed class FakeTokenGenerator : IInvitationTokenGenerator
    {
        private int _issued;

        public string CreateToken() => $"token-{++_issued}";

        public string Hash(string token) => $"hash:{token}";
    }

    private sealed class FakeInvitationRepository : IInvitationRepository
    {
        public List<Invitation> Items { get; } = [];

        public Task<IReadOnlyList<Invitation>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Invitation>>(Items.ToArray());

        public Task<Invitation?> FindAsync(Guid invitationId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.SingleOrDefault(item => item.Id == invitationId));

        public Task<Invitation?> FindByTokenHashAsync(
            string tokenHash,
            CancellationToken cancellationToken) =>
            Task.FromResult(Items.SingleOrDefault(item =>
                string.Equals(item.TokenHash, tokenHash, StringComparison.Ordinal)));

        public Task<IReadOnlyList<Invitation>> FindOutstandingForEmailAsync(
            string normalizedEmail,
            DateTimeOffset atUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Invitation>>(Items
                .Where(item => item.NormalizedEmail == normalizedEmail && item.CanBeRedeemedAt(atUtc))
                .ToArray());

        public void Add(Invitation invitation) => Items.Add(invitation);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<TResult> InTransactionAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(operation);
            return operation(cancellationToken);
        }
    }

    private sealed class FakeAccountStore : IUserAccountStore
    {
        private readonly List<UserAccount> _accounts = [];

        public List<UserAccount> Created { get; } = [];

        public string? FailCreation { get; set; }

        public void Seed(string email) => _accounts.Add(new UserAccount(
            Guid.NewGuid(),
            email,
            email,
            UserStatus.Active,
            false,
            InvitationServiceTests.Now,
            null));

        public Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UserAccount>>(_accounts.ToArray());

        public Task<UserAccount?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(_accounts.SingleOrDefault(account => account.Id == userId));

        public Task<UserAccount?> FindByEmailAsync(string email, CancellationToken cancellationToken) =>
            Task.FromResult(_accounts.SingleOrDefault(account =>
                string.Equals(account.Email, email, StringComparison.OrdinalIgnoreCase)));

        public Task<int> CountAdminsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_accounts.Count(account => account.IsAdmin));

        public Task<AccountOperationResult> CreateAsync(
            string email,
            string displayName,
            string password,
            UserStatus status,
            bool isAdmin,
            CancellationToken cancellationToken)
        {
            if (FailCreation is not null)
            {
                return Task.FromResult(AccountOperationResult.Failure(FailCreation));
            }

            var account = new UserAccount(
                Guid.NewGuid(),
                email,
                displayName,
                status,
                isAdmin,
                InvitationServiceTests.Now,
                null);

            _accounts.Add(account);
            Created.Add(account);
            return Task.FromResult(AccountOperationResult.Success(account.Id));
        }

        public Task<AccountOperationResult> SetStatusAsync(
            Guid userId,
            UserStatus status,
            CancellationToken cancellationToken) =>
            Task.FromResult(AccountOperationResult.Success(userId));

        public Task<AccountOperationResult> SetPasswordAsync(
            Guid userId,
            string password,
            CancellationToken cancellationToken) =>
            Task.FromResult(AccountOperationResult.Success(userId));

        public Task<AccountOperationResult> SetAdminAsync(
            Guid userId,
            bool isAdmin,
            CancellationToken cancellationToken) =>
            Task.FromResult(AccountOperationResult.Success(userId));
    }
}
