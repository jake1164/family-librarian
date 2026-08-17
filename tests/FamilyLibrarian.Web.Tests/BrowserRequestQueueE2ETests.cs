using Microsoft.Playwright;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Runs against a separately started, clean Compose deployment. It is opt-in so
/// the normal suite neither needs a browser download nor credentials for a local
/// administrator. The flow intentionally stays in the browser: it proves the
/// hosted client, cookie session, anti-forgery handling, and admin queue work
/// together rather than only exercising the host endpoints.
/// </summary>
[TestClass]
public sealed class BrowserRequestQueueE2ETests
{
    private const string BaseUrlVariable = "FAMILY_LIBRARIAN_E2E_BASE_URL";
    private const string AdminEmailVariable = "FAMILY_LIBRARIAN_E2E_ADMIN_EMAIL";
    private const string AdminPasswordVariable = "FAMILY_LIBRARIAN_E2E_ADMIN_PASSWORD";
    private const string RequesterPassword = "Browser-E2E!2026";

    [TestMethod]
    public async Task AFamilyMemberCanRequestABookAndAnAdminCanReviewItThroughTheBrowser()
    {
        var settings = ReadSettings();
        var requesterEmail = $"browser-e2e-{Guid.NewGuid():N}@family-librarian.example";

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
        await page.GetByRole(AriaRole.Button, new() { Name = "View details", Exact = true }).First.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Use this book", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Request this book", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Your request is in.", new() { Exact = false })).ToBeVisibleAsync();

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
