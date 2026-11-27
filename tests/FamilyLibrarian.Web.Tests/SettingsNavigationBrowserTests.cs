using FamilyLibrarian.Domain.Delivery;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
[TestCategory("Release")]
public sealed class SettingsNavigationBrowserTests
{
    [TestMethod]
    public async Task AnAdministratorTurnsChatOnFromSettingsAndTheMenuFollowsWithoutAReload()
    {
        if (Environment.GetEnvironmentVariable("FAMILY_LIBRARIAN_SETTINGS_BROWSER_TESTS") != "1")
            Assert.Inconclusive("Set FAMILY_LIBRARIAN_SETTINGS_BROWSER_TESTS=1 for the isolated settings navigation browser regression.");
        await using var fixture = await WebTestFixture.CreateAsync();
        var available = WebTestFixture.Require(fixture);
        await using var original = new FamilyLibrarianAppFactory(available.ConnectionString);
        await using var factory = original.WithWebHostBuilder(builder => builder.UseStaticWebAssets());
        factory.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            ExecutablePath = Environment.GetEnvironmentVariable("FAMILY_LIBRARIAN_E2E_CHROMIUM_EXECUTABLE")
        });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(15000);
        await page.GotoAsync(new Uri(client.BaseAddress!, "/login").ToString());
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(FamilyLibrarianAppFactory.AdminEmail);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(FamilyLibrarianAppFactory.AdminPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");

        // Found from the AI models page, which is where someone who just set up a model is likely to look.
        await page.GotoAsync(new Uri(client.BaseAddress!, "/admin/model-gateway").ToString());
        await page.Locator(".mud-main-content a[href='settings/chat']").ClickAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/settings/chat");
        var chatLink = page.Locator(".mud-drawer a[href='chat']");
        await Assertions.Expect(page.GetByText("Web chat is off", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(chatLink).ToHaveCountAsync(0);

        await page.GetByLabel("Web chat", new() { Exact = true }).ClickAsync();

        await Assertions.Expect(page.GetByText("Web chat is on", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(chatLink).ToHaveCountAsync(1);
        await chatLink.ClickAsync();
        await Assertions.Expect(page.GetByLabel("Message to the librarian", new() { Exact = true })).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task ARefusedSwitchStaysOffAndARiskyOneSavesOnlyAfterConfirmation()
    {
        if (Environment.GetEnvironmentVariable("FAMILY_LIBRARIAN_SETTINGS_BROWSER_TESTS") != "1")
            Assert.Inconclusive("Set FAMILY_LIBRARIAN_SETTINGS_BROWSER_TESTS=1 for the isolated settings navigation browser regression.");
        await using var fixture = await WebTestFixture.CreateAsync();
        var available = WebTestFixture.Require(fixture);
        await using var original = new FamilyLibrarianAppFactory(available.ConnectionString);
        await using var factory = original.WithWebHostBuilder(builder => builder.UseStaticWebAssets());
        factory.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            ExecutablePath = Environment.GetEnvironmentVariable("FAMILY_LIBRARIAN_E2E_CHROMIUM_EXECUTABLE")
        });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(15000);
        await page.GotoAsync(new Uri(client.BaseAddress!, "/login").ToString());
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(FamilyLibrarianAppFactory.AdminEmail);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(FamilyLibrarianAppFactory.AdminPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");

        // Nothing is set up, so the host refuses. The switch must not be left looking on, which is what happened before.
        await page.GotoAsync(new Uri(client.BaseAddress!, "/settings/communications").ToString());
        var matrix = page.GetByLabel("Matrix bot connection", new() { Exact = true });
        await matrix.ClickAsync();
        await Assertions.Expect(page.GetByText("A Matrix homeserver URL is required.", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(matrix).Not.ToBeCheckedAsync();
        await page.ReloadAsync();
        await Assertions.Expect(matrix).Not.ToBeCheckedAsync();

        // A destination that cannot be turned on opens its settings, so what needs fixing is on screen.
        await page.GotoAsync(new Uri(client.BaseAddress!, "/settings/publishing").ToString());
        var cwa = page.GetByLabel("CWA", new() { Exact = true });
        await cwa.ClickAsync();
        await Assertions.Expect(page.GetByLabel("Local ingest directory", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(cwa).Not.ToBeCheckedAsync();

        // A permission asks first. Cancelling saves nothing; accepting saves at once, with no Save button.
        await page.GotoAsync(new Uri(client.BaseAddress!, "/admin/model-gateway").ToString());
        var release = page.GetByLabel("Allow AI validation to release narrowly eligible identity holds", new() { Exact = true });
        await release.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Assertions.Expect(release).Not.ToBeCheckedAsync();
        await release.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Allow release", Exact = true }).ClickAsync();
        // The switch shows the click while saving, so wait for the host to confirm before reloading.
        await Assertions.Expect(page.GetByText("Releasing identity holds turned on.", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(release).ToBeEnabledAsync();
        await Assertions.Expect(release).ToBeCheckedAsync();
        await page.ReloadAsync();
        await Assertions.Expect(release).ToBeCheckedAsync();
        await release.ClickAsync();
        await Assertions.Expect(page.GetByText("Releasing identity holds turned off.", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(release).ToBeEnabledAsync();
        await Assertions.Expect(release).Not.ToBeCheckedAsync();
        await page.ReloadAsync();
        await Assertions.Expect(release).Not.ToBeCheckedAsync();
    }

    [TestMethod]
    public async Task SettingsEditsWarnOnInternalBackAndReloadAndSuccessfulSavesClearOnlyTheirOwnSection()
    {
        if (Environment.GetEnvironmentVariable("FAMILY_LIBRARIAN_SETTINGS_BROWSER_TESTS") != "1")
            Assert.Inconclusive("Set FAMILY_LIBRARIAN_SETTINGS_BROWSER_TESTS=1 for the isolated settings navigation browser regression.");
        await using var fixture = await WebTestFixture.CreateAsync();
        var available = WebTestFixture.Require(fixture);
        await using var original = new FamilyLibrarianAppFactory(available.ConnectionString);
        await using var factory = original.WithWebHostBuilder(builder => builder.UseStaticWebAssets());
        factory.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        // The Kindle address field is read-only until the delivery service is set up
        // or the account already has a saved address; this account gets the latter.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var adminId = (await db.Users.SingleAsync(user => user.Email == FamilyLibrarianAppFactory.AdminEmail)).Id;
            db.DeliveryTargets.Add(new DeliveryTarget(
                adminId, DeliveryTargetProvider.CwaKindleEmail, "Kindle", "admin@kindle.com", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            ExecutablePath = Environment.GetEnvironmentVariable("FAMILY_LIBRARIAN_E2E_CHROMIUM_EXECUTABLE")
        });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(15000);
        await page.GotoAsync(new Uri(client.BaseAddress!, "/login").ToString());
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(FamilyLibrarianAppFactory.AdminEmail);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(FamilyLibrarianAppFactory.AdminPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");
        var dialogs = new List<string>();
        var accept = false;
        TaskCompletionSource? dialogHandled = null;
        page.Dialog += async (_, dialog) =>
        {
            dialogs.Add(dialog.Type);
            if (accept) await dialog.AcceptAsync();
            else await dialog.DismissAsync();
            dialogHandled?.TrySetResult();
        };
        var home = page.GetByRole(AriaRole.Link, new() { Name = "Family Librarian", Exact = true });

        // Real settings pages, typed without a blur, must all participate in the shared guard.
        foreach (var (path, label) in new[] {
            ("/admin/model-gateway", "Endpoint URL"),
            ("/settings/authentication", "Button label"),
            ("/settings/communications", "SMTP host"),
            ("/settings/publishing", "Local ingest directory"),
            ("/settings/ebook-delivery", "Kindle email address"),
            ("/settings/sources", "Base URL") })
        {
            await page.GotoAsync(new Uri(client.BaseAddress!, path).ToString());
            if (path == "/settings/publishing")
                await page.GetByLabel("CWA", new() { Exact = true }).ClickAsync(); // refused until set up, which opens its settings
            var field = page.GetByLabel(label, new() { Exact = true });
            var before = await field.InputValueAsync();
            await field.FillAsync("unsaved-edit");
            var count = dialogs.Count;
            await home.ClickAsync();
            Assert.AreEqual(count + 1, dialogs.Count, path);
            Assert.AreEqual(path, new Uri(page.Url).AbsolutePath);
            await Assertions.Expect(field).ToHaveValueAsync("unsaved-edit");
            await field.FillAsync(before);
            await home.ClickAsync();
            Assert.AreEqual(count + 1, dialogs.Count, $"Reverting {path} must clear the warning.");
            try { await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/", new() { Timeout = 3000 }); }
            catch (TimeoutException) { Assert.Fail($"Reverting {path} left URL {page.Url}; dialogs {dialogs.Count}, expected {count + 1}."); }
            Assert.AreEqual(count + 1, dialogs.Count, "Reverting the edit must clear the warning.");
        }

        await page.GotoAsync(new Uri(client.BaseAddress!, "/admin/model-gateway").ToString());
        await page.GetByRole(AriaRole.Tab, new() { Name = "Test models", Exact = true }).ClickAsync();
        await page.GetByLabel("Primary local model", new() { Exact = true }).FillAsync("unsaved/model");
        dialogHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.EvaluateAsync("location.reload()");
        await dialogHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dialogHandled = null;
        Assert.AreEqual("beforeunload", dialogs.Last(), "Typed model text is protected even before blur or selection.");
        await page.GetByRole(AriaRole.Tab, new() { Name = "Connection", Exact = true }).ClickAsync();
        await page.GetByLabel("Endpoint URL", new() { Exact = true }).FillAsync("https://models.example.test/v1");
        var nestedCount = dialogs.Count;
        await home.ClickAsync();
        Assert.AreEqual(nestedCount + 1, dialogs.Count, "Multiple dirty tabs must produce one confirmation.");
        accept = true;
        await home.ClickAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");
        accept = false;

        await page.GotoAsync(new Uri(client.BaseAddress!, "/settings/authentication").ToString());
        await page.GetByLabel("Button label", new() { Exact = true }).FillAsync("Unsaved sign-in");
        var dialogCount = dialogs.Count;
        dialogHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.EvaluateAsync("history.back()");
        await dialogHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dialogHandled = null;
        Assert.AreEqual(dialogCount + 1, dialogs.Count);
        Assert.AreEqual("/settings/authentication", new Uri(page.Url).AbsolutePath);
        dialogHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.EvaluateAsync("location.reload()");
        await dialogHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dialogHandled = null;
        Assert.AreEqual("beforeunload", dialogs.Last());
        await Assertions.Expect(page.GetByLabel("Button label", new() { Exact = true })).ToHaveValueAsync("Unsaved sign-in");

        // A failed save keeps the draft protected; a successful save stores everything typed, the secret included.
        await page.RouteAsync("**/api/v1/admin/authentication/oidc/", route => route.FulfillAsync(new()
        {
            Status = 400, ContentType = "application/problem+json", Body = "{\"errors\":{\"settings\":[\"Test rejection\"]}}"
        }));
        await page.GetByRole(AriaRole.Button, new() { Name = "Save settings", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Test rejection", new() { Exact = true })).ToBeVisibleAsync();
        dialogCount = dialogs.Count;
        await home.ClickAsync();
        Assert.AreEqual(dialogCount + 1, dialogs.Count, "Failed saves must not accept the draft.");
        await page.UnrouteAsync("**/api/v1/admin/authentication/oidc/");
        await page.GetByLabel("Issuer (Authority) URL", new() { Exact = true }).FillAsync("https://identity.example.test");
        await page.GetByLabel("Client ID", new() { Exact = true }).FillAsync("settings-browser-fixture");
        await page.GetByLabel("Replace client secret", new() { Exact = true }).FillAsync("unsaved-fixture-secret");
        var savedResponse = page.WaitForResponseAsync(response => response.Url.EndsWith("/api/v1/admin/authentication/oidc/", StringComparison.Ordinal) && response.Request.Method == "PUT");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save settings", Exact = true }).ClickAsync();
        Assert.AreEqual(200, (await savedResponse).Status);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Save settings", Exact = true })).ToBeEnabledAsync();
        await Assertions.Expect(page.GetByLabel("Replace client secret", new() { Exact = true })).ToHaveValueAsync(string.Empty);
        dialogCount = dialogs.Count;
        await home.ClickAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");
        Assert.AreEqual(dialogCount, dialogs.Count, "A successful save leaves nothing unsaved.");

        await page.GotoAsync(new Uri(client.BaseAddress!, "/settings/authentication").ToString());
        await page.GetByLabel("Button label", new() { Exact = true }).FillAsync("Discard this change");
        accept = true;
        await home.ClickAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");
    }
}
