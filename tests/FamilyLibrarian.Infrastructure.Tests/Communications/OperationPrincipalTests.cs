using System.Security.Claims;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Domain;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Infrastructure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Infrastructure.Tests.Communications;

/// <summary>
/// M2-1: a trusted, scoped, non-HTTP identity. It is derived from stored state (verified link, sender, current account
/// status and role), lives for one dependency-injection scope, and fails closed everywhere else.
/// </summary>
[TestClass]
public sealed class OperationPrincipalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    private const string Room = "!dm:example.test";
    private const string Sender = "@mara:example.test";

    private sealed class FakeDestinations : IUserMatrixDestinationStore
    {
        public Dictionary<string, UserMatrixDestination> ByRoom { get; } = [];

        public Task<UserMatrixDestination?> FindByUserIdAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(ByRoom.Values.FirstOrDefault(destination => destination.UserId == userId));

        public Task<UserMatrixDestination?> FindByRoomIdAsync(string roomId, CancellationToken cancellationToken) =>
            Task.FromResult(ByRoom.Values.FirstOrDefault(destination => destination.RoomId == roomId));

        public Task<UserMatrixDestination> GetOrCreateForUserAsync(Guid userId, DateTimeOffset createdAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeAccounts : IUserAccountStore
    {
        public Dictionary<Guid, UserAccount> Accounts { get; } = [];

        public Task<UserAccount?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(Accounts.GetValueOrDefault(userId));

        public Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UserAccount?> FindByEmailAsync(string email, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CountAdminsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AccountOperationResult> CreateAsync(
            string email, string displayName, string password, UserStatus status, bool isAdmin, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AccountOperationResult> SetStatusAsync(Guid userId, UserStatus status, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AccountOperationResult> SetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AccountOperationResult> SetAdminAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AccountOperationResult> SetAudiobookNarrationPreferenceAsync(
            Guid userId, AudiobookNarrationPreference preference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AdminAccountSummary>> ListActiveAdminsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<QuietHours?> GetQuietHoursAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AccountOperationResult> SetQuietHoursAsync(Guid userId, QuietHours? quietHours, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class World
    {
        public FakeDestinations Destinations { get; } = new();

        public FakeAccounts Accounts { get; } = new();

        /// <summary>Links a verified Matrix account to a new FL account in its own DM room.</summary>
        public UserAccount Link(
            string room = Room, string sender = Sender, UserStatus status = UserStatus.Active, bool isAdmin = false, bool verified = true,
            string displayName = "Mara Quill")
        {
            var account = new UserAccount(Guid.NewGuid(), $"{Guid.NewGuid():N}@example.test", displayName, status, isAdmin, Now, null);
            Accounts.Accounts[account.Id] = account;
            var destination = new UserMatrixDestination(account.Id, Now);
            destination.RequestVerification(sender, room, "123456", Now);
            if (verified) destination.Verify(Now);
            Destinations.ByRoom[room] = destination;
            return account;
        }

        public ServiceProvider Provider()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IUserMatrixDestinationStore>(Destinations);
            services.AddSingleton<IUserAccountStore>(Accounts);
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            AddIdentityServices(services);
            return services.BuildServiceProvider();
        }
    }

    private static void AddIdentityServices(IServiceCollection services)
    {
        services.AddScoped<HttpContextCurrentUser>();
        services.AddScoped<HttpContextCurrentAdministrator>();
        services.AddScoped<OperationPrincipalHolder>();
        services.AddScoped<IOperationPrincipalAccessor>(provider => provider.GetRequiredService<OperationPrincipalHolder>());
        services.AddScoped<ICurrentUser, OperationAwareCurrentUser>();
        services.AddScoped<ICurrentAdministrator, OperationAwareCurrentAdministrator>();
        services.AddScoped<MatrixPrincipalAuthenticator>();
    }

    private static MatrixInboundMessage Message(string room = Room, string sender = Sender) => new(room, sender, "hello");

    private static Task<MatrixAuthenticationResult> AuthenticateAsync(IServiceScope scope, MatrixInboundMessage message) =>
        scope.ServiceProvider.GetRequiredService<MatrixPrincipalAuthenticator>().AuthenticateAsync(message, CancellationToken.None);

    [TestMethod]
    public async Task AVerifiedLinkedSenderBecomesTheStoredAccountAndConsumersSeeIt()
    {
        var world = new World();
        var account = world.Link(isAdmin: true);
        await using var provider = world.Provider();
        using var scope = provider.CreateScope();

        var result = await AuthenticateAsync(scope, Message());

        Assert.IsTrue(result.IsAuthenticated);
        Assert.AreEqual(account.Id, scope.ServiceProvider.GetRequiredService<ICurrentUser>().UserId);
        Assert.AreEqual("Mara Quill", scope.ServiceProvider.GetRequiredService<ICurrentUser>().DisplayName, "The FL account's name, not Matrix's.");
        Assert.IsTrue(scope.ServiceProvider.GetRequiredService<ICurrentAdministrator>().IsAdministrator);
    }

    [TestMethod]
    public async Task AnOrdinaryMemberIsNotAnAdministrator()
    {
        var world = new World();
        world.Link(isAdmin: false);
        await using var provider = world.Provider();
        using var scope = provider.CreateScope();

        await AuthenticateAsync(scope, Message());

        Assert.IsFalse(scope.ServiceProvider.GetRequiredService<ICurrentAdministrator>().IsAdministrator);
    }

    [TestMethod]
    public async Task EveryReasonToDistrustTheMessageLeavesTheScopeUnauthenticated()
    {
        var cases = new (string Name, Func<World, MatrixInboundMessage> Arrange, MatrixAuthenticationOutcome Expected)[]
        {
            ("unknown room", world => { world.Link(); return Message(room: "!other:example.test"); }, MatrixAuthenticationOutcome.UnknownRoom),
            ("link never verified", world => { world.Link(verified: false); return Message(); }, MatrixAuthenticationOutcome.LinkNotVerified),
            ("different sender in the linked room", world => { world.Link(); return Message(sender: "@intruder:example.test"); }, MatrixAuthenticationOutcome.SenderMismatch),
            ("sender differing only by case", world => { world.Link(); return Message(sender: Sender.ToUpperInvariant()); }, MatrixAuthenticationOutcome.SenderMismatch),
            ("display name as sender", world => { world.Link(); return Message(sender: "Mara Quill"); }, MatrixAuthenticationOutcome.SenderMismatch),
            ("account disabled", world => { world.Link(status: UserStatus.Disabled); return Message(); }, MatrixAuthenticationOutcome.AccountNotActive),
            ("account pending approval", world => { world.Link(status: UserStatus.PendingApproval); return Message(); }, MatrixAuthenticationOutcome.AccountNotActive),
            ("account only invited", world => { world.Link(status: UserStatus.Invited); return Message(); }, MatrixAuthenticationOutcome.AccountNotActive),
            ("account deleted", world => { var account = world.Link(); world.Accounts.Accounts.Remove(account.Id); return Message(); }, MatrixAuthenticationOutcome.AccountMissing),
            ("blank sender", world => { world.Link(); return Message(sender: " "); }, MatrixAuthenticationOutcome.UnknownRoom),
        };

        foreach (var (name, arrange, expected) in cases)
        {
            var world = new World();
            var message = arrange(world);
            await using var provider = world.Provider();
            using var scope = provider.CreateScope();

            var result = await AuthenticateAsync(scope, message);

            Assert.AreEqual(expected, result.Outcome, name);
            Assert.IsNull(result.Principal, name);
            Assert.IsNull(scope.ServiceProvider.GetRequiredService<ICurrentUser>().UserId, name);
            Assert.IsNull(scope.ServiceProvider.GetRequiredService<ICurrentUser>().DisplayName, name);
            Assert.IsFalse(scope.ServiceProvider.GetRequiredService<ICurrentAdministrator>().IsAdministrator, name);
        }
    }

    [TestMethod]
    public async Task AnUnlinkedAccountCannotSpeakAgain()
    {
        var world = new World();
        world.Link();
        await using var provider = world.Provider();
        using (var first = provider.CreateScope())
        {
            Assert.IsTrue((await AuthenticateAsync(first, Message())).IsAuthenticated);
        }

        world.Destinations.ByRoom[Room].Unlink(Now);

        using var second = provider.CreateScope();
        Assert.AreEqual(MatrixAuthenticationOutcome.UnknownRoom, (await AuthenticateAsync(second, Message())).Outcome,
            "Unlinking clears the room, so the old room no longer resolves to anyone.");
        Assert.IsNull(second.ServiceProvider.GetRequiredService<ICurrentUser>().UserId);
    }

    [TestMethod]
    public async Task ARoleDowngradeOrDisableTakesEffectOnTheNextOperation()
    {
        var world = new World();
        var account = world.Link(isAdmin: true);
        await using var provider = world.Provider();

        using (var before = provider.CreateScope())
        {
            await AuthenticateAsync(before, Message());
            Assert.IsTrue(before.ServiceProvider.GetRequiredService<ICurrentAdministrator>().IsAdministrator);
        }

        world.Accounts.Accounts[account.Id] = account with { IsAdmin = false };
        using (var downgraded = provider.CreateScope())
        {
            Assert.IsTrue((await AuthenticateAsync(downgraded, Message())).IsAuthenticated);
            Assert.IsFalse(downgraded.ServiceProvider.GetRequiredService<ICurrentAdministrator>().IsAdministrator, "No cached privilege.");
        }

        world.Accounts.Accounts[account.Id] = account with { IsAdmin = false, Status = UserStatus.Disabled };
        using var disabled = provider.CreateScope();
        Assert.IsFalse((await AuthenticateAsync(disabled, Message())).IsAuthenticated);
        Assert.IsNull(disabled.ServiceProvider.GetRequiredService<ICurrentUser>().UserId);
    }

    [TestMethod]
    public async Task AnAuthenticatedScopeCannotBeSwitchedToAnotherIdentity()
    {
        var world = new World();
        world.Link();
        world.Link(room: "!second:example.test", sender: "@other:example.test", displayName: "Other");
        await using var provider = world.Provider();
        using var scope = provider.CreateScope();
        var first = await AuthenticateAsync(scope, Message());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => AuthenticateAsync(scope, Message("!second:example.test", "@other:example.test")));

        Assert.AreEqual(first.Principal!.UserId, scope.ServiceProvider.GetRequiredService<ICurrentUser>().UserId);
    }

    [TestMethod]
    public async Task ScopesWithoutAuthenticationHaveNoUserEvenAfterAnotherScopeAuthenticated()
    {
        var world = new World();
        world.Link(isAdmin: true);
        await using var provider = world.Provider();
        using var authenticated = provider.CreateScope();
        await AuthenticateAsync(authenticated, Message());

        using var bystander = provider.CreateScope();

        Assert.IsNull(bystander.ServiceProvider.GetRequiredService<ICurrentUser>().UserId, "Identity is per scope, never ambient.");
        Assert.IsFalse(bystander.ServiceProvider.GetRequiredService<ICurrentAdministrator>().IsAdministrator);
    }

    [TestMethod]
    public async Task ManyUsersAuthenticatingAtOnceEachSeeOnlyTheirOwnIdentity()
    {
        var world = new World();
        var people = Enumerable.Range(0, 40)
            .Select(index => (Account: world.Link($"!room{index}:example.test", $"@user{index}:example.test", isAdmin: index % 2 == 0, displayName: $"User {index}"), Index: index))
            .ToArray();
        await using var provider = world.Provider();
        using var start = new ManualResetEventSlim();

        var running = people.Select(person => Task.Run(async () =>
        {
            start.Wait();
            using var scope = provider.CreateScope();
            await AuthenticateAsync(scope, Message($"!room{person.Index}:example.test", $"@user{person.Index}:example.test"));
            var seen = new List<(Guid? Id, string? Name, bool Admin)>();
            for (var round = 0; round < 20; round++)
            {
                await Task.Yield();
                var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
                seen.Add((user.UserId, user.DisplayName, scope.ServiceProvider.GetRequiredService<ICurrentAdministrator>().IsAdministrator));
            }

            return (person, seen);
        })).ToArray();
        start.Set();
        var observed = await Task.WhenAll(running);

        foreach (var (person, seen) in observed)
        {
            Assert.IsTrue(seen.All(entry => entry.Id == person.Account.Id && entry.Name == $"User {person.Index}" && entry.Admin == (person.Index % 2 == 0)),
                $"User {person.Index} observed someone else's identity.");
        }
    }

    [TestMethod]
    public async Task HttpBehaviorIsUnchangedWhenNoOperationPrincipalWasEstablished()
    {
        var userId = Guid.NewGuid();
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Email, "reader@example.test"), new Claim(ClaimTypes.Role, RoleNames.Admin)],
            authenticationType: "test");
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } });
        AddIdentityServices(services);
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();

        Assert.AreEqual(userId, user.UserId);
        Assert.AreEqual("reader@example.test", user.DisplayName);
        Assert.IsTrue(scope.ServiceProvider.GetRequiredService<ICurrentAdministrator>().IsAdministrator);
    }

    [TestMethod]
    public async Task AnEstablishedPrincipalWinsOverAnyHttpIdentityAndNeverFallsBackToIt()
    {
        var world = new World();
        var linked = world.Link(isAdmin: false);
        var httpAdmin = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, RoleNames.Admin)], "test");
        var services = new ServiceCollection();
        services.AddSingleton<IUserMatrixDestinationStore>(world.Destinations);
        services.AddSingleton<IUserAccountStore>(world.Accounts);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(httpAdmin) } });
        AddIdentityServices(services);
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        await AuthenticateAsync(scope, Message());

        Assert.AreEqual(linked.Id, scope.ServiceProvider.GetRequiredService<ICurrentUser>().UserId);
        Assert.IsFalse(scope.ServiceProvider.GetRequiredService<ICurrentAdministrator>().IsAdministrator,
            "The linked member's role applies, not the ambient HTTP administrator's.");
    }

    [TestMethod]
    public void TheHolderIsRegisteredPerScopeNotAsASingleton()
    {
        var services = new ServiceCollection();
        AddIdentityServices(services);
        var holder = services.Single(descriptor => descriptor.ServiceType == typeof(OperationPrincipalHolder));
        Assert.AreEqual(ServiceLifetime.Scoped, holder.Lifetime);
        Assert.AreEqual(ServiceLifetime.Scoped, services.Single(descriptor => descriptor.ServiceType == typeof(ICurrentUser)).Lifetime);
        Assert.AreEqual(typeof(OperationAwareCurrentUser), services.Single(descriptor => descriptor.ServiceType == typeof(ICurrentUser)).ImplementationType);
    }
}
