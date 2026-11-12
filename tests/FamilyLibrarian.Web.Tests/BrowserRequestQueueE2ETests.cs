using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Publishing;
using Microsoft.Playwright;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Runs against a separately started, clean Compose deployment. It is opt-in so
/// the normal suite neither needs a browser download nor credentials for a local
/// administrator. The flow intentionally stays in the browser: it proves the
/// hosted client, cookie session, anti-forgery handling, and admin queue work
/// together rather than only exercising the host endpoints.
/// </summary>
// Release tier: needs Chromium (and, for the queue E2E, a deployed Compose
// stack), so checkin CI excludes it and .github/workflows/release-tests.yml
// runs it before a release.
[TestClass]
[TestCategory("Release")]
public sealed class BrowserRequestQueueE2ETests
{
    private const string BaseUrlVariable = "FAMILY_LIBRARIAN_E2E_BASE_URL";
    private const string AdminEmailVariable = "FAMILY_LIBRARIAN_E2E_ADMIN_EMAIL";
    private const string AdminPasswordVariable = "FAMILY_LIBRARIAN_E2E_ADMIN_PASSWORD";
    // The stub feed service from compose.release-e2e.yaml; reachable by this name from the application container.
    private const string OpdsStubUrl = "http://opds-stub";
    private const string RequesterPassword = "Browser-E2E!2026";

    [TestMethod]
    public async Task AFamilyMemberCanRequestABookAndAnAdminCanReviewItThroughTheBrowser()
    {
        var settings = ReadSettings();
        var requesterEmail = $"browser-e2e-{Guid.NewGuid():N}@family-librarian.example";
        await EnableAnEbookDestinationAsync(settings);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        var page = await browser.NewPageAsync();

        await SignInAsync(page, settings.BaseUri, settings.AdminEmail, settings.AdminPassword);
        await page.GotoAsync(new Uri(settings.BaseUri, "/admin/accounts").ToString());
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Family accounts" }))
            .ToBeVisibleAsync();

        await page.GetByLabel("Email address", new() { Exact = true }).FillAsync(requesterEmail);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create invite link", Exact = true }).ClickAsync();
        var invitationUrl = await page.GetByLabel("Invitation link", new() { Exact = true }).InputValueAsync();
        Assert.IsFalse(string.IsNullOrWhiteSpace(invitationUrl));

        await page.GotoAsync(invitationUrl);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Join the family library" }))
            .ToBeVisibleAsync();
        await page.GetByLabel("Your name", new() { Exact = true }).FillAsync("Browser E2E Member");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(RequesterPassword);
        await page.GetByLabel("Confirm password", new() { Exact = true }).FillAsync(RequesterPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create my account", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Your account is ready.", new() { Exact = false })).ToBeVisibleAsync();

        await SignInAsync(page, settings.BaseUri, requesterEmail, RequesterPassword);
        await page.GotoAsync(new Uri(settings.BaseUri, "/search").ToString());
        await page.GetByLabel("Title, author, or ISBN", new() { Exact = true }).FillAsync("the hobbit");
        await page.GetByLabel("Title, author, or ISBN", new() { Exact = true }).PressAsync("Enter");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Results", Exact = true }))
            .ToBeVisibleAsync();
        await page.Locator("a.search-result-title").First.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Request this book", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Your interest is saved.", new() { Exact = false })).ToBeVisibleAsync();

        await SignOutAsync(page);
        await SignInAsync(page, settings.BaseUri, settings.AdminEmail, settings.AdminPassword);
        await page.GotoAsync(new Uri(settings.BaseUri, "/admin/requests").ToString());
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Request queue", Exact = true }))
            .ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText(requesterEmail, new() { Exact = false })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Review request", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Needs review", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("PendingAcquisition → NeedsReview", new() { Exact = true }))
            .ToBeVisibleAsync();
    }

    /// <summary>
    /// A fresh deployment has no delivery destination, and the request form keeps its button disabled until an ebook
    /// destination passes its connection test. The administrator therefore configures the simplest one (a local ingest
    /// folder plus the stub OPDS feed from compose.release-e2e.yaml) first, exactly as the settings page would.
    /// </summary>
    private static async Task EnableAnEbookDestinationAsync(BrowserE2eSettings settings)
    {
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = settings.BaseUri };
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = settings.AdminEmail, Password = settings.AdminPassword });
        login.EnsureSuccessStatusCode();
        var token = await client.GetFromJsonAsync<AntiforgeryTokenResponse>("/api/v1/antiforgery/token");
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token!.Token);

        var saved = await client.PutAsJsonAsync("/api/v1/admin/publishing/cwa",
            new SetCwaSettingsRequest("Local", "/tmp", null, null, null, null, "PrivateKey", OpdsStubUrl, null, null, null));
        Assert.IsTrue(saved.IsSuccessStatusCode, "Saving the ingest folder: " + await saved.Content.ReadAsStringAsync());
        var tested = await client.PostAsync("/api/v1/admin/publishing/cwa/test", content: null);
        Assert.IsTrue(tested.IsSuccessStatusCode, "Testing the destination: " + await tested.Content.ReadAsStringAsync());
        var enabled = await client.PutAsJsonAsync("/api/v1/admin/publishing/cwa/enabled", new SetPublishingEnabledRequest(true));
        Assert.IsTrue(enabled.IsSuccessStatusCode, "Enabling the destination: " + await enabled.Content.ReadAsStringAsync());
    }

    private static async Task SignInAsync(IPage page, Uri baseUri, string email, string password)
    {
        await page.GotoAsync(new Uri(baseUri, "/login").ToString());
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();

        // "Account menu" alone is not proof this sign-in took effect: the layout
        // shows it for *any* authenticated identity, so a still-valid prior
        // session (e.g. the admin's, earlier in this same test) already
        // satisfies it even if this sign-in silently no-ops. A successful sign-in
        // does a forceLoad navigation to "/", so waiting for that first is what
        // actually proves the new identity replaced the old one.
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/", new() { Timeout = 15_000 });
        await Assertions.Expect(page.GetByLabel("Account menu", new() { Exact = true })).ToBeVisibleAsync();
    }

    private static async Task SignOutAsync(IPage page)
    {
        await page.GetByLabel("Account menu", new() { Exact = true }).ClickAsync();
        // MudMenuItem renders as an unadorned <div>/<p> with no ARIA menuitem
        // role, so the visible text is the only reliable locator.
        await page.GetByText("Sign out", new() { Exact = true }).ClickAsync();
        // The signed-out home page offers "Sign in" twice (the app bar and the
        // anonymous hero CTA); the app bar link is the one that is always there.
        await Assertions.Expect(page.GetByRole(AriaRole.Toolbar).GetByRole(AriaRole.Link, new() { Name = "Sign in", Exact = true }))
            .ToBeVisibleAsync();
    }

    private static BrowserE2eSettings ReadSettings()
    {
        var baseUrl = Environment.GetEnvironmentVariable(BaseUrlVariable);
        var adminEmail = Environment.GetEnvironmentVariable(AdminEmailVariable);
        var adminPassword = Environment.GetEnvironmentVariable(AdminPasswordVariable);

        if (string.IsNullOrWhiteSpace(baseUrl) ||
            string.IsNullOrWhiteSpace(adminEmail) ||
            string.IsNullOrWhiteSpace(adminPassword))
        {
            Assert.Inconclusive(
                $"Browser E2E is opt-in. Start a clean Compose stack and set {BaseUrlVariable}, " +
                $"{AdminEmailVariable}, and {AdminPasswordVariable}.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            !string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail($"{BaseUrlVariable} must be an absolute HTTP or HTTPS URL.");
        }

        return new BrowserE2eSettings(baseUri!, adminEmail!, adminPassword!);
    }

    private sealed record BrowserE2eSettings(Uri BaseUri, string AdminEmail, string AdminPassword);
}
