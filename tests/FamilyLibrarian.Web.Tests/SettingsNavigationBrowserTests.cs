using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Playwright;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
[TestCategory("Release")]
public sealed class SettingsNavigationBrowserTests
{
    [TestMethod]
    public async Task SettingsEditsWarnOnInternalBackAndReloadAndSuccessfulSavesClearOnlyTheirOwnSection()
    {
        if (Environment.GetEnvironmentVariable("FAMILY_LIBRARIAN_SETTINGS_BROWSER_TESTS") != "1")
            Assert.Inconclusive("Set FAMILY_LIBRARIAN_SETTINGS_BROWSER_TESTS=1 for the isolated settings navigation browser regression.");
        await using var fixture = await WebTestFixture.CreateAsync();
        var available = WebTestFixture.Require(fixture);
        await using var original = new FamilyLibrarianAppFactory(available.ConnectionString);
        await using var factory = original.WithWebHostBuilder(builder => builder.UseStaticWebAssets());
        factory.UseKestrel(0);
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
                await page.GetByLabel("Disabled", new() { Exact = true }).First.ClickAsync();
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

        // A failed save keeps the draft protected; a successful save leaves a different credential draft protected.
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
        dialogCount = dialogs.Count;
        await home.ClickAsync();
        Assert.AreEqual(dialogCount + 1, dialogs.Count);
        await page.GetByLabel("Replace client secret", new() { Exact = true }).FillAsync(string.Empty);
        dialogCount = dialogs.Count;
        await home.ClickAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");
        Assert.AreEqual(dialogCount, dialogs.Count, "Successful settings save plus empty credential draft is clean.");

        await page.GotoAsync(new Uri(client.BaseAddress!, "/settings/authentication").ToString());
        await page.GetByLabel("Button label", new() { Exact = true }).FillAsync("Discard this change");
        accept = true;
        await home.ClickAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");
    }
}
