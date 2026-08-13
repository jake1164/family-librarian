using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Accounts;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Web.Tests.Harness;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// The invitation flow against the real host and a real PostgreSQL database.
/// </summary>
[TestClass]
public sealed class InvitationWorkflowEndpointTests
{
    private const string StrongPassword = "Family-Invitee-Pass1!";

    private static WebTestFixture? _fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext testContext)
    {
        ArgumentNullException.ThrowIfNull(testContext);
        _fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AnInvitedFamilyMemberCanRedeemAndThenSignIn()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await CreateAdminClientAsync(fixture);
        var email = NewEmail();

        var invitation = await CreateInvitationAsync(admin, email);

        Assert.AreEqual(email, invitation.Email);
        Assert.IsFalse(string.IsNullOrWhiteSpace(invitation.Token));
        // The token belongs in the fragment, which browsers never send to a server.
        StringAssert.Contains(invitation.RedeemUrl, $"/invite#{invitation.Token}");

        using var anonymous = fixture.CreateAnonymousClient();
        var redeemed = await anonymous.PostAsJsonAsync(
            "/api/v1/invitations/redeem",
            new RedeemInvitationRequest(invitation.Token, "New Reader", StrongPassword));

        Assert.AreEqual(HttpStatusCode.NoContent, redeemed.StatusCode);

        var signIn = await anonymous.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { Email = email, Password = StrongPassword });

        Assert.AreEqual(HttpStatusCode.NoContent, signIn.StatusCode);

        var me = await anonymous.GetFromJsonAsync<CurrentUserResponse>("/api/v1/me");
        Assert.IsNotNull(me);
        Assert.AreEqual("New Reader", me.DisplayName);
        CollectionAssert.DoesNotContain(me.Roles.ToArray(), FamilyLibrarian.Domain.RoleNames.Admin);
    }

    [TestMethod]
    public async Task AnInvitationTokenWorksOnlyOnce()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await CreateAdminClientAsync(fixture);
        var invitation = await CreateInvitationAsync(admin, NewEmail());

        using var anonymous = fixture.CreateAnonymousClient();
        var first = await RedeemAsync(anonymous, invitation.Token, "First");
        var second = await RedeemAsync(anonymous, invitation.Token, "Second");

        Assert.AreEqual(HttpStatusCode.NoContent, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [TestMethod]
    public async Task AWithdrawnInvitationCannotBeRedeemed()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await CreateAdminClientAsync(fixture);
        var invitation = await CreateInvitationAsync(admin, NewEmail());

        var revoked = await admin.PostAsync(
            $"/api/v1/admin/invitations/{invitation.Id}/revoke",
            content: null);
        Assert.AreEqual(HttpStatusCode.NoContent, revoked.StatusCode);

        using var anonymous = fixture.CreateAnonymousClient();
        var redeemed = await RedeemAsync(anonymous, invitation.Token, "Too Late");

        Assert.AreEqual(HttpStatusCode.BadRequest, redeemed.StatusCode);
    }

    [TestMethod]
    public async Task ANewLinkReplacesALostOneAndTheOldTokenStopsWorking()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await CreateAdminClientAsync(fixture);
        var original = await CreateInvitationAsync(admin, NewEmail());

        var response = await admin.PostAsync(
            $"/api/v1/admin/invitations/{original.Id}/regenerate",
            content: null);
        Assert.AreEqual(
            HttpStatusCode.OK,
            response.StatusCode,
            await response.Content.ReadAsStringAsync());

        var replacement = await response.Content.ReadFromJsonAsync<CreatedInvitationResponse>();
        Assert.IsNotNull(replacement);
        Assert.AreNotEqual(original.Token, replacement.Token);
        Assert.AreEqual(original.Email, replacement.Email);

        using var anonymous = fixture.CreateAnonymousClient();
        var withOldToken = await RedeemAsync(anonymous, original.Token, "Too Late");
        var withNewToken = await RedeemAsync(anonymous, replacement.Token, "New Reader");

        Assert.AreEqual(HttpStatusCode.BadRequest, withOldToken.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, withNewToken.StatusCode);
    }

    [TestMethod]
    public async Task AnAddressKeepsTheCasingItWasInvitedWith()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await CreateAdminClientAsync(fixture);
        var email = $"Mixed.Case-{Guid.NewGuid():N}@Family-Librarian.Example";

        var invitation = await CreateInvitationAsync(admin, email);
        using var anonymous = fixture.CreateAnonymousClient();
        Assert.AreEqual(
            HttpStatusCode.NoContent,
            (await RedeemAsync(anonymous, invitation.Token, "Mixed Case")).StatusCode);

        var accounts = await admin.GetFromJsonAsync<FamilyAccountListResponse>("/api/v1/admin/accounts/");
        Assert.IsNotNull(accounts);

        // Shown back the way it was typed, not shouted in upper case.
        Assert.AreEqual(email, invitation.Email);
        Assert.IsTrue(accounts.Accounts.Any(account =>
            string.Equals(account.Email, email, StringComparison.Ordinal)));

        // Sign-in still ignores casing, because Identity matches on its own
        // normalized column.
        var signIn = await fixture.CreateAnonymousClient().PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { Email = email.ToUpperInvariant(), Password = StrongPassword });
        Assert.AreEqual(HttpStatusCode.NoContent, signIn.StatusCode);
    }

    [TestMethod]
    public async Task ARedeemedInvitationCannotBeRegenerated()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await CreateAdminClientAsync(fixture);
        var invitation = await CreateInvitationAsync(admin, NewEmail());

        using var anonymous = fixture.CreateAnonymousClient();
        Assert.AreEqual(
            HttpStatusCode.NoContent,
            (await RedeemAsync(anonymous, invitation.Token, "Already In")).StatusCode);

        var response = await admin.PostAsync(
            $"/api/v1/admin/invitations/{invitation.Id}/regenerate",
            content: null);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task NoEndpointReturnsAnInvitationTokenAfterItIsCreated()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await CreateAdminClientAsync(fixture);
        var invitation = await CreateInvitationAsync(admin, NewEmail());

        var listed = await admin.GetStringAsync("/api/v1/admin/invitations/");
        var preview = await admin.GetStringAsync(
            $"/api/v1/invitations/preview?token={Uri.EscapeDataString(invitation.Token)}");

        // Only the hash is stored, so the token cannot reappear anywhere.
        StringAssert.DoesNotMatch(listed, new System.Text.RegularExpressions.Regex(
            System.Text.RegularExpressions.Regex.Escape(invitation.Token)));
        StringAssert.DoesNotMatch(preview, new System.Text.RegularExpressions.Regex(
            System.Text.RegularExpressions.Regex.Escape(invitation.Token)));
    }

    [TestMethod]
    public async Task PreviewingAnUnknownTokenRevealsNothing()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var anonymous = fixture.CreateAnonymousClient();

        var response = await anonymous.GetAsync(
            "/api/v1/invitations/preview?token=definitely-not-a-real-token");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task ANonAdminCannotInviteOrManageAccounts()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateUserClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        // A valid anti-forgery token must not substitute for the Admin role.
        var invite = await client.PostAsJsonAsync(
            "/api/v1/admin/invitations/",
            new CreateInvitationRequest(NewEmail(), false));
        var accounts = await client.GetAsync("/api/v1/admin/accounts/");
        var status = await client.PutAsJsonAsync(
            $"/api/v1/admin/accounts/{Guid.NewGuid()}/status",
            new SetAccountStatusRequest("Disabled"));

        Assert.AreEqual(HttpStatusCode.Forbidden, invite.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, accounts.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, status.StatusCode);
    }

    [TestMethod]
    public async Task AnAnonymousCallerCannotReachAccountManagement()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = fixture.CreateAnonymousClient();

        var accounts = await client.GetAsync("/api/v1/admin/accounts/");
        var invitations = await client.GetAsync("/api/v1/admin/invitations/");

        Assert.AreEqual(HttpStatusCode.Unauthorized, accounts.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, invitations.StatusCode);
    }

    [TestMethod]
    public async Task InvitingAnAddressThatAlreadyHasAnAccountIsRefused()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await CreateAdminClientAsync(fixture);

        var response = await admin.PostAsJsonAsync(
            "/api/v1/admin/invitations/",
            new CreateInvitationRequest(WebTestFixture.UserEmail, false));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task ADisabledAccountCannotSignInAndItsOpenSessionStopsWorking()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await CreateAdminClientAsync(fixture);
        var email = NewEmail();
        var invitation = await CreateInvitationAsync(admin, email);

        using var member = fixture.CreateAnonymousClient();
        Assert.AreEqual(
            HttpStatusCode.NoContent,
            (await RedeemAsync(member, invitation.Token, "Soon Disabled")).StatusCode);
        Assert.AreEqual(
            HttpStatusCode.NoContent,
            (await member.PostAsJsonAsync(
                "/api/auth/login",
                new LoginRequest { Email = email, Password = StrongPassword })).StatusCode);

        // The session is live at this point.
        Assert.AreEqual(HttpStatusCode.OK, (await member.GetAsync("/api/v1/me")).StatusCode);

        var accounts = await admin.GetFromJsonAsync<FamilyAccountListResponse>("/api/v1/admin/accounts/");
        Assert.IsNotNull(accounts);
        var account = accounts.Accounts.Single(candidate =>
            string.Equals(candidate.Email, email, StringComparison.OrdinalIgnoreCase));

        var disabled = await admin.PutAsJsonAsync(
            $"/api/v1/admin/accounts/{account.Id}/status",
            new SetAccountStatusRequest("Disabled"));
        Assert.AreEqual(HttpStatusCode.NoContent, disabled.StatusCode);

        // A fresh sign-in is refused...
        using var freshAttempt = fixture.CreateAnonymousClient();
        var signIn = await freshAttempt.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { Email = email, Password = StrongPassword });
        Assert.AreEqual(HttpStatusCode.Unauthorized, signIn.StatusCode);

        // ...and so is the cookie the account was already holding, because
        // disabling rotates the security stamp rather than only gating login.
        var withOldCookie = await member.GetAsync("/api/v1/me");
        Assert.AreEqual(HttpStatusCode.Unauthorized, withOldCookie.StatusCode);
    }

    [TestMethod]
    public async Task TheLastAdministratorCannotBeDisabled()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await CreateAdminClientAsync(fixture);

        var accounts = await admin.GetFromJsonAsync<FamilyAccountListResponse>("/api/v1/admin/accounts/");
        Assert.IsNotNull(accounts);
        var onlyAdmin = accounts.Accounts.Single(account => account.IsAdmin);

        var response = await admin.PutAsJsonAsync(
            $"/api/v1/admin/accounts/{onlyAdmin.Id}/status",
            new SetAccountStatusRequest("Disabled"));

        // Losing the last administrator would leave no route back in: the
        // bootstrap only runs while no administrator exists.
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task AnInvitationMutationWithoutAnAntiforgeryTokenIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var admin = await fixture.CreateAdminClientAsync();

        var response = await admin.PostAsJsonAsync(
            "/api/v1/admin/invitations/",
            new CreateInvitationRequest(NewEmail(), false));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<HttpClient> CreateAdminClientAsync(WebTestFixture fixture)
    {
        var client = await fixture.CreateAdminClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        return client;
    }

    private static async Task<CreatedInvitationResponse> CreateInvitationAsync(
        HttpClient admin,
        string email)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/v1/admin/invitations/",
            new CreateInvitationRequest(email, false));

        Assert.AreEqual(
            HttpStatusCode.OK,
            response.StatusCode,
            await response.Content.ReadAsStringAsync());

        var invitation = await response.Content.ReadFromJsonAsync<CreatedInvitationResponse>();
        Assert.IsNotNull(invitation);
        return invitation;
    }

    private static Task<HttpResponseMessage> RedeemAsync(
        HttpClient client,
        string token,
        string displayName) =>
        client.PostAsJsonAsync(
            "/api/v1/invitations/redeem",
            new RedeemInvitationRequest(token, displayName, StrongPassword));

    /// <summary>A unique address per test, so tests do not collide on the class database.</summary>
    private static string NewEmail() => $"invitee-{Guid.NewGuid():N}@family-librarian.example";
}
