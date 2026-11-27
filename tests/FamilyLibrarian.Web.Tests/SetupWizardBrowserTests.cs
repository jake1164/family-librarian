using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Setup;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// The first-time setup as a person meets it, on a phone and on a computer. The messaging server is a stand-in; the
/// reply that verifies an account goes through the real inbound router, so the page has to follow along by itself.
/// </summary>
[TestClass]
[TestCategory("Release")]
public sealed class SetupWizardBrowserTests
{
    private const string Sender = "library@example.org";
    private static readonly ViewportSize Phone = new() { Width = 390, Height = 844 };
    private static readonly ViewportSize Desktop = new() { Width = 1280, Height = 900 };

    [TestMethod]
    public async Task ANewMemberIsWalkedThroughEveryStepOnAPhone()
    {
        await using var app = await RunningApp.StartAsync(Phone);
        var page = app.Page;

        await app.SignInAsync(WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/welcome");
        await Assertions.Expect(page.GetByText("Welcome to the family library")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".setup-banner")).ToHaveCountAsync(0);
        await app.AssertNoSidewaysScrollAsync("welcome");
        await app.ClickButtonAsync("Get started");

        await Assertions.Expect(page.GetByText("Step 1 of 3")).ToBeVisibleAsync();
        await page.GetByLabel("Both", new() { Exact = true }).CheckAsync();
        await Assertions.Expect(page.GetByText("Default request format saved.")).ToBeVisibleAsync();
        await app.ClickButtonAsync("Continue");

        await Assertions.Expect(page.GetByText("Step 2 of 3")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText(Sender, new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Open my Amazon settings" }))
            .ToHaveAttributeAsync("href", "https://www.amazon.co.uk/hz/mycd/myx");
        var address = page.GetByLabel("Kindle email address", new() { Exact = true });
        await address.FillAsync("reader@mail.example");
        await Assertions.Expect(page.GetByText("Kindle addresses usually end in @kindle.com", new() { Exact = false }).Last)
            .ToBeVisibleAsync();
        await address.FillAsync("reader@kindle.com");
        await app.AssertNoSidewaysScrollAsync("Kindle step");
        await app.ClickButtonAsync("Save and continue");

        await Assertions.Expect(page.GetByText("Step 3 of 3")).ToBeVisibleAsync();
        // A phone gets buttons, not a code it would have to scan with itself.
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Open Element", Exact = true }))
            .ToHaveAttributeAsync("href", "https://mobile.element.io/element/?account_provider=example.test");
        await Assertions.Expect(page.Locator("svg[aria-label^='QR code']")).ToBeHiddenAsync();
        await Assertions.Expect(page.GetByLabel("Element username", new() { Exact = true })).ToHaveCountAsync(0);
        await app.ClickButtonAsync("I've signed in");
        await page.GetByLabel("Element username", new() { Exact = true }).FillAsync("Reader");
        await app.ClickButtonAsync("Send my code");

        await Assertions.Expect(page.GetByText("accept the newest invitation", new() { Exact = false })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Continue", Exact = true }))
            .ToBeDisabledAsync();
        await app.AssertNoSidewaysScrollAsync("waiting for the code");
        Assert.AreEqual("@reader:example.test", app.Messaging.LastInvited);

        await app.ReplyWithCodeAsync(WebTestFixture.UserEmail);
        await Assertions.Expect(page.GetByText("Connected as reader", new() { Exact = true })).ToBeVisibleAsync();
        await app.ClickButtonAsync("Continue");

        await Assertions.Expect(page.GetByText("You're all set")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".mud-list")).ToContainTextAsync("Kindle delivery: address saved");
        await Assertions.Expect(page.Locator(".mud-list")).ToContainTextAsync("Message the Librarian: connected");
        await app.AssertNoSidewaysScrollAsync("summary");

        await page.GotoAsync(app.Url("/"));
        await Assertions.Expect(page.GetByText("Your family library, thoughtfully organized.")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".setup-banner")).ToHaveCountAsync(0);
        Assert.AreEqual("/", new Uri(page.Url).AbsolutePath);

        await app.WithDatabaseAsync(async database =>
        {
            var user = await database.Users.AsNoTracking().SingleAsync(u => u.Email == WebTestFixture.UserEmail);
            Assert.AreEqual(DefaultRequestFormat.Both, user.DefaultRequestFormat);
            Assert.AreEqual(SetupSteps.Formats | SetupSteps.Kindle | SetupSteps.Messaging, user.SetupCompletedSteps);
            Assert.AreEqual("reader@kindle.com",
                (await database.DeliveryTargets.AsNoTracking().SingleAsync(t => t.UserId == user.Id)).Address);
        });
    }

    [TestMethod]
    public async Task ALinkIsNeverHijackedAndTheWizardOpensOnlyFromTheHomePageOnce()
    {
        await using var app = await RunningApp.StartAsync(Desktop);
        var page = app.Page;

        await page.GotoAsync(app.Url("/login?returnUrl=requests"));
        await app.FillSignInAsync(WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/requests");
        // The reminder offers the setup without taking over the page that was asked for.
        await Assertions.Expect(page.Locator(".setup-banner")).ToContainTextAsync(
            "You can now send books to your Kindle and message the Librarian.");
        Assert.AreEqual("/requests", new Uri(page.Url).AbsolutePath);

        await page.GetByRole(AriaRole.Link, new() { Name = "Family Librarian", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/welcome");

        // Walking away without finishing, even before the wizard has loaded: it does not open again, and the
        // reminder remains.
        await page.GetByRole(AriaRole.Link, new() { Name = "Family Librarian", Exact = true }).ClickAsync();
        // Wait for the home page itself: a click is applied a moment later, and the old screen can still match.
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");
        await Assertions.Expect(page.GetByText("Your family library, thoughtfully organized.")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".setup-banner")).ToBeVisibleAsync();
        await page.ReloadAsync();
        await Assertions.Expect(page.Locator(".setup-banner")).ToBeVisibleAsync();
        Assert.AreEqual("/", new Uri(page.Url).AbsolutePath);
    }

    [TestMethod]
    public async Task AnExistingMemberCanDismissTheReminderAndReopenTheGuideLater()
    {
        await using var app = await RunningApp.StartAsync(Desktop);
        var page = app.Page;
        await app.WithDatabaseAsync(database => database.Users
            .Where(user => user.Email == WebTestFixture.UserEmail)
            .ExecuteUpdateAsync(set => set.SetProperty(user => user.SetupWizardSeenAtUtc, DateTimeOffset.UtcNow)));

        await app.SignInAsync(WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        await Assertions.Expect(page.Locator(".setup-banner")).ToBeVisibleAsync();
        Assert.AreEqual("/", new Uri(page.Url).AbsolutePath);

        await app.ClickButtonAsync("Dismiss");
        await Assertions.Expect(page.Locator(".setup-banner")).ToHaveCountAsync(0);
        await page.ReloadAsync();
        await Assertions.Expect(page.GetByText("Your family library, thoughtfully organized.")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".setup-banner")).ToHaveCountAsync(0);

        await page.Locator("button[aria-label='Settings menu']").ClickAsync();
        await page.GetByText("Setup guide", new() { Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/welcome");
        await app.ClickButtonAsync("Get started");
        await app.ClickButtonAsync("Skip this step");
        await app.ClickButtonAsync("Skip this step");
        // A computer gets the code to scan with a phone.
        await Assertions.Expect(page.Locator("svg[aria-label^='QR code']")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Open Element", Exact = true }))
            .ToBeHiddenAsync();
        await app.ClickButtonAsync("Skip this step");
        await Assertions.Expect(page.Locator(".mud-list")).ToContainTextAsync("Kindle delivery: skipped");
    }

    [TestMethod]
    public async Task AnAdministratorSetsTheGuidanceMembersSee()
    {
        await using var app = await RunningApp.StartAsync(Desktop);
        var page = app.Page;
        await app.SignInAsync(FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/welcome");

        await page.GotoAsync(app.Url("/settings/publishing"));
        var store = page.GetByLabel("Amazon store", new() { Exact = true });
        await Assertions.Expect(store).ToHaveValueAsync("amazon.co.uk");
        await store.FillAsync("https://www.amazon.de/gp/help/customer");
        await app.ClickButtonAsync("Save Kindle help");
        await Assertions.Expect(page.GetByText("Kindle setup help saved.")).ToBeVisibleAsync();
        await Assertions.Expect(store).ToHaveValueAsync("amazon.de");

        await page.GotoAsync(app.Url("/settings/communications"));
        var instructions = page.GetByLabel("Sign-in instructions", new() { Exact = true });
        await instructions.FillAsync("Use your library login.");
        var elementHelp = page.GetByLabel("Walk people through installing Element X", new() { Exact = true });
        await elementHelp.UncheckAsync();
        await Assertions.Expect(page.GetByText("Element X setup help hidden.")).ToBeVisibleAsync();
        // The switch saved on its own and left the unsaved text alone.
        await Assertions.Expect(instructions).ToHaveValueAsync("Use your library login.");
        await app.ClickButtonAsync("Save messaging help");
        await Assertions.Expect(page.GetByText("Messaging setup help saved.")).ToBeVisibleAsync();

        await page.GotoAsync(app.Url("/settings/matrix"));
        await Assertions.Expect(page.GetByText("Use your library login.")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Get Element", new() { Exact = true })).ToHaveCountAsync(0);
        await app.ClickButtonAsync("I've signed in");
        await Assertions.Expect(page.GetByLabel("Messaging username", new() { Exact = true })).ToBeVisibleAsync();

        await page.GotoAsync(app.Url("/settings/ebook-delivery"));
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Open my Amazon settings" }))
            .ToHaveAttributeAsync("href", "https://www.amazon.de/hz/mycd/myx");
    }

    [TestMethod]
    public async Task TheCodeOnScreenReadsBackAsTheSameAddressAsTheOpenButton()
    {
        await using var app = await RunningApp.StartAsync(Desktop);
        var page = app.Page;
        await app.SignInAsync(WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/welcome");
        await page.GotoAsync(app.Url("/settings/matrix"));
        var drawing = page.Locator("svg[aria-label^='QR code']");
        await Assertions.Expect(drawing).ToBeVisibleAsync();
        // The button is for phones and hidden at this width, but it is the address the code has to match.
        var button = await page.Locator("a", new() { HasTextString = "Open Element" }).GetAttributeAsync("href");
        Assert.AreEqual("https://mobile.element.io/element/?account_provider=example.test", button);

        foreach (var theme in new[] { ColorScheme.Light, ColorScheme.Dark })
        {
            await page.EmulateMediaAsync(new() { ColorScheme = theme });
            await drawing.ScrollIntoViewIfNeededAsync();

            // The pixels as displayed, at the size displayed: what a camera would be pointed at.
            Assert.AreEqual(button, await ReadQrCodeAsync(page, await drawing.ScreenshotAsync()), theme.ToString());
        }
    }

    [TestMethod]
    public async Task TheWizardWaitsForAFormatChoiceToBeSaved()
    {
        await using var app = await RunningApp.StartAsync(Desktop);
        var page = app.Page;
        await app.OpenWizardAsync();
        var next = app.Button("Continue");
        var skip = app.Button("Skip this step");
        await Assertions.Expect(next).ToBeEnabledAsync();

        // A slow save: nothing may leave the step until it finishes.
        var release = new TaskCompletionSource();
        await app.InterceptAsync("**/api/v1/me/default-request-format/", "PUT", async route =>
        {
            await release.Task;
            await route.ContinueAsync();
        });
        await page.GetByLabel("Both", new() { Exact = true }).ClickAsync();
        await Assertions.Expect(next).ToBeDisabledAsync();
        await Assertions.Expect(skip).ToBeDisabledAsync();
        await Assertions.Expect(app.Button("Back")).ToBeDisabledAsync();
        release.SetResult();
        await Assertions.Expect(next).ToBeEnabledAsync();

        // A failed save: Continue stays off until it is dealt with, but the step can still be skipped.
        await page.UnrouteAsync("**/api/v1/me/default-request-format/");
        await app.InterceptAsync("**/api/v1/me/default-request-format/", "PUT",
            route => route.FulfillAsync(new() { Status = 500 }));
        await page.GetByLabel("Ebook", new() { Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Your last choice wasn't saved", new() { Exact = false }))
            .ToBeVisibleAsync();
        await Assertions.Expect(next).ToBeDisabledAsync();
        await Assertions.Expect(skip).ToBeEnabledAsync();
        await app.ClickButtonAsync("Keep the saved choices");
        await Assertions.Expect(next).ToBeEnabledAsync();

        // The choice saved, but not the progress: stay on the step.
        await app.InterceptAsync("**/api/v1/me/setup/steps/Formats/completed", "POST",
            route => route.FulfillAsync(new() { Status = 409 }));
        await next.ClickAsync();
        await Assertions.Expect(page.GetByText("Your progress could not be saved.", new() { Exact = false }))
            .ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Step 1 of 3")).ToBeVisibleAsync();
        await page.UnrouteAsync("**/api/v1/me/setup/steps/Formats/completed");
        await next.ClickAsync();
        await Assertions.Expect(page.GetByText("Step 2 of 3")).ToBeVisibleAsync();

        // The same for Kindle, where the address is already saved by then.
        await app.InterceptAsync("**/api/v1/me/setup/steps/Kindle/completed", "POST",
            route => route.FulfillAsync(new() { Status = 409 }));
        await page.GetByLabel("Kindle email address", new() { Exact = true }).FillAsync("reader@kindle.com");
        await app.ClickButtonAsync("Save and continue");
        await Assertions.Expect(next).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Step 2 of 3")).ToBeVisibleAsync();
        await page.UnrouteAsync("**/api/v1/me/setup/steps/Kindle/completed");
        await next.ClickAsync();
        await Assertions.Expect(page.GetByText("Step 3 of 3")).ToBeVisibleAsync();

        await app.WithDatabaseAsync(async database =>
        {
            var user = await database.Users.AsNoTracking().SingleAsync(u => u.Email == WebTestFixture.UserEmail);
            Assert.AreEqual(DefaultRequestFormat.Both, user.DefaultRequestFormat);
            Assert.AreEqual(SetupSteps.Formats | SetupSteps.Kindle, user.SetupCompletedSteps);
        });
    }

    [TestMethod]
    public async Task ChoicesThatCannotBeLoadedBlockContinueButNotSkipping()
    {
        await using var app = await RunningApp.StartAsync(Desktop);
        var page = app.Page;
        await app.OpenWizardAsync(beforeGetStarted: () => app.InterceptAsync(
            "**/api/v1/me/default-request-format/", "GET", route => route.FulfillAsync(new() { Status = 500 })));

        await Assertions.Expect(page.GetByText("We could not load your format preferences.")).ToBeVisibleAsync();
        await Assertions.Expect(app.Button("Continue")).ToBeDisabledAsync();
        await app.ClickButtonAsync("Skip this step");
        await Assertions.Expect(page.GetByText("Step 2 of 3")).ToBeVisibleAsync();

        await app.InterceptAsync("**/api/v1/me/setup/", "GET", route => route.FulfillAsync(new() { Status = 500 }));
        await page.GotoAsync(app.Url("/welcome"));
        await Assertions.Expect(page.GetByText("We could not load your setup.", new() { Exact = false }))
            .ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task WithNothingSetUpTheWizardStaysAwayAndSaysSoIfOpened()
    {
        await using var app = await RunningApp.StartAsync(Desktop, nothingSetUp: true);
        var page = app.Page;

        await app.SignInAsync(WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        await Assertions.Expect(page.GetByText("Your family library, thoughtfully organized.")).ToBeVisibleAsync();
        await app.WaitUntilWizardSeenAsync(WebTestFixture.UserEmail);
        Assert.AreEqual("/", new Uri(page.Url).AbsolutePath);
        await Assertions.Expect(page.Locator(".setup-banner")).ToHaveCountAsync(0);

        await page.GotoAsync(app.Url("/welcome"));
        await Assertions.Expect(page.GetByText("Nothing to set up yet")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task AFeatureTurnedOffHalfwaySaysSoAndCanBeSkipped()
    {
        await using var app = await RunningApp.StartAsync(Desktop);
        var page = app.Page;
        await app.OpenWizardAsync();
        await app.ClickButtonAsync("Continue");
        await Assertions.Expect(page.GetByText("Step 2 of 3")).ToBeVisibleAsync();

        await app.WithDatabaseAsync(database => database.CwaSettings.ExecuteDeleteAsync());
        await page.GetByLabel("Kindle email address", new() { Exact = true }).FillAsync("reader@kindle.com");
        await app.ClickButtonAsync("Save and continue");
        await Assertions.Expect(page.GetByText("Kindle delivery isn't available yet.", new() { Exact = false }))
            .ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Step 2 of 3")).ToBeVisibleAsync();
        await app.ClickButtonAsync("Skip this step");

        await Assertions.Expect(page.GetByText("Step 3 of 3")).ToBeVisibleAsync();
        await app.WithDatabaseAsync(async database =>
        {
            (await database.MatrixSettings.SingleAsync()).SetEnabled(false, null, DateTimeOffset.UtcNow);
            await database.SaveChangesAsync();
        });
        await app.ClickButtonAsync("I've signed in");
        await page.GetByLabel("Element username", new() { Exact = true }).FillAsync("reader");
        await app.ClickButtonAsync("Send my code");
        await Assertions.Expect(page.GetByText("Messaging the Librarian isn't available yet.", new() { Exact = false }))
            .ToBeVisibleAsync();
        Assert.IsNull(app.Messaging.LastInvited);
        await app.ClickButtonAsync("Skip this step");
        await Assertions.Expect(page.GetByText("You're all set")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task ASecondTabNeitherReopensTheWizardNorUndoesFinishedWork()
    {
        await using var app = await RunningApp.StartAsync(Desktop);
        await app.OpenWizardAsync();
        var first = app.Page;
        var second = await first.Context.NewPageAsync();

        await second.GotoAsync(app.Url("/"));
        await Assertions.Expect(second.GetByText("Your family library, thoughtfully organized.")).ToBeVisibleAsync();
        await Assertions.Expect(second.Locator(".setup-banner")).ToBeVisibleAsync();
        Assert.AreEqual("/", new Uri(second.Url).AbsolutePath);

        // The second tab loads the guide before the first finishes a step, then skips it.
        await second.GotoAsync(app.Url("/welcome"));
        await second.GetByRole(AriaRole.Button, new() { Name = "Get started", Exact = true }).ClickAsync();
        await Assertions.Expect(second.GetByText("Step 1 of 3")).ToBeVisibleAsync();
        await app.ClickButtonAsync("Continue");
        await Assertions.Expect(first.GetByText("Step 2 of 3")).ToBeVisibleAsync();
        await second.GetByRole(AriaRole.Button, new() { Name = "Skip this step", Exact = true }).ClickAsync();
        await Assertions.Expect(second.GetByText("Step 2 of 3")).ToBeVisibleAsync();

        await app.WithDatabaseAsync(async database =>
        {
            var user = await database.Users.AsNoTracking().SingleAsync(u => u.Email == WebTestFixture.UserEmail);
            Assert.AreEqual(SetupSteps.Formats, user.SetupCompletedSteps);
            Assert.AreEqual(SetupSteps.None, user.SetupSkippedSteps);
        });
    }

    [TestMethod]
    public async Task ReachingTheCodeLimitIsExplainedOnAPhone()
    {
        await using var app = await RunningApp.StartAsync(Phone);
        var page = app.Page;
        await app.SignInAsync(WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/welcome");
        await page.GotoAsync(app.Url("/settings/matrix"));
        await app.ClickButtonAsync("I've signed in");
        await page.GetByLabel("Element username", new() { Exact = true }).FillAsync("reader");
        // Each click waits for its own answer: a repeated pop-up is not shown twice, so pop-ups cannot be counted.
        Task<IResponse> SendAsync(string button) => page.RunAndWaitForResponseAsync(
            () => app.ClickButtonAsync(button),
            response => response.Request.Method == "POST" &&
                response.Url.Contains("/api/v1/me/communications/matrix", StringComparison.Ordinal));

        Assert.AreEqual(200, (await SendAsync("Send my code")).Status);
        for (var send = 2; send <= 5; send++)
        {
            await Assertions.Expect(app.Button("Send a new code")).ToBeEnabledAsync();
            Assert.AreEqual(200, (await SendAsync("Send a new code")).Status, $"send {send}");
        }

        await Assertions.Expect(app.Button("Send a new code")).ToBeEnabledAsync();
        Assert.AreEqual(429, (await SendAsync("Send a new code")).Status);

        var notice = page.GetByRole(AriaRole.Alert).Filter(new() { HasTextString = "Code limit reached." });
        await Assertions.Expect(notice).ToContainTextAsync(
            "You've sent 5 codes in the last 15 minutes, which is the limit. Nothing is broken: you can send " +
            "another in about 15 minutes. If a code has already reached you, you can still reply with the newest one.");
        await Assertions.Expect(app.Button("Send a new code")).ToBeEnabledAsync();
        Assert.AreEqual(5, app.Messaging.Invitations);
        await app.AssertNoSidewaysScrollAsync("code limit");
    }

    private static async Task<string?> ReadQrCodeAsync(IPage page, byte[] screenshot)
    {
        // The browser turns its own screenshot back into pixels, so the test needs no image library.
        var image = await page.EvaluateAsync<System.Text.Json.JsonElement>(
            """
            async data => {
                const picture = new Image();
                picture.src = 'data:image/png;base64,' + data;
                await picture.decode();
                const canvas = document.createElement('canvas');
                canvas.width = picture.width;
                canvas.height = picture.height;
                const context = canvas.getContext('2d');
                context.drawImage(picture, 0, 0);
                const pixels = context.getImageData(0, 0, canvas.width, canvas.height).data;
                let text = '';
                for (let i = 0; i < pixels.length; i += 0x8000) text += String.fromCharCode.apply(null, pixels.subarray(i, i + 0x8000));
                return { width: canvas.width, height: canvas.height, rgba: btoa(text) };
            }
            """,
            Convert.ToBase64String(screenshot));
        var reader = new ZXing.BarcodeReaderGeneric();
        reader.Options.PossibleFormats = [ZXing.BarcodeFormat.QR_CODE];
        reader.Options.TryHarder = true;
        return reader.Decode(
            Convert.FromBase64String(image.GetProperty("rgba").GetString()!),
            image.GetProperty("width").GetInt32(),
            image.GetProperty("height").GetInt32(),
            ZXing.RGBLuminanceSource.BitmapFormat.RGBA32)?.Text;
    }

    private sealed class RunningApp : IAsyncDisposable
    {
        private readonly List<IAsyncDisposable> disposables = [];
        private HttpClient client = null!;
        private IPlaywright playwright = null!;
        private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<global::Program> factory = null!;

        public IPage Page { get; private set; } = null!;

        public StandInMatrixClient Messaging { get; } = new();

        /// <param name="nothingSetUp">No delivery or messaging, and formats follow the real saved switches.</param>
        public static async Task<RunningApp> StartAsync(ViewportSize viewport, bool nothingSetUp = false)
        {
            if (Environment.GetEnvironmentVariable("FAMILY_LIBRARIAN_SETTINGS_BROWSER_TESTS") != "1")
                Assert.Inconclusive("Set FAMILY_LIBRARIAN_SETTINGS_BROWSER_TESTS=1 for the setup wizard browser regression.");

            var app = new RunningApp();
            var fixture = await WebTestFixture.CreateAsync();
            var available = WebTestFixture.Require(fixture);
            var original = new FamilyLibrarianAppFactory(available.ConnectionString, services =>
            {
                services.RemoveAll<IMatrixClient>();
                services.AddSingleton<IMatrixClient>(app.Messaging);
                if (nothingSetUp)
                {
                    services.RemoveAll<IFormatReadinessService>();
                    services.AddScoped<IFormatReadinessService, FormatReadinessService>();
                }
            });
            app.factory = original.WithWebHostBuilder(builder => builder.UseStaticWebAssets());
            app.factory.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
            app.client = app.factory.CreateClient();
            if (!nothingSetUp)
            {
                await app.SeedAsync();
            }

            app.playwright = await Playwright.CreateAsync();
            var browser = await app.playwright.Chromium.LaunchAsync(new()
            {
                Headless = true,
                ExecutablePath = Environment.GetEnvironmentVariable("FAMILY_LIBRARIAN_E2E_CHROMIUM_EXECUTABLE")
            });
            app.disposables.AddRange([browser, app.factory, original, available]);
            // An explicit context, so that a test can open a second tab with the same sign-in.
            var context = await browser.NewContextAsync(new() { ViewportSize = viewport });
            app.Page = await context.NewPageAsync();
            app.Page.SetDefaultTimeout(20000);
            return app;
        }

        public string Url(string path) => new Uri(client.BaseAddress!, path).ToString();

        public async Task SignInAsync(string email, string password)
        {
            await Page.GotoAsync(Url("/login"));
            await FillSignInAsync(email, password);
        }

        public async Task FillSignInAsync(string email, string password)
        {
            await Page.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
            await Page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
            await Page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        }

        public ILocator Button(string name) => Page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

        public Task ClickButtonAsync(string name) => Button(name).ClickAsync();

        /// <summary>Signs in as the new member and goes to the first step.</summary>
        public async Task OpenWizardAsync(Func<Task>? beforeGetStarted = null)
        {
            await SignInAsync(WebTestFixture.UserEmail, WebTestFixture.UserPassword);
            await Page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/welcome");
            await Assertions.Expect(Button("Get started")).ToBeVisibleAsync();
            if (beforeGetStarted is not null)
            {
                await beforeGetStarted();
            }

            await ClickButtonAsync("Get started");
            await Assertions.Expect(Page.GetByText("Step 1 of 3")).ToBeVisibleAsync();
        }

        /// <summary>Takes over one kind of request to an address; everything else goes through untouched.</summary>
        public Task<IAsyncDisposable> InterceptAsync(string pattern, string method, Func<IRoute, Task> handle) =>
            Page.RouteAsync(pattern, route => route.Request.Method == method ? handle(route) : route.ContinueAsync());

        public async Task WaitUntilWizardSeenAsync(string email)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            var seen = false;
            while (!seen && DateTimeOffset.UtcNow < deadline)
            {
                await WithDatabaseAsync(async database => seen = await database.Users.AsNoTracking()
                    .AnyAsync(user => user.Email == email && user.SetupWizardSeenAtUtc != null));
                if (!seen)
                {
                    await Task.Delay(200);
                }
            }

            Assert.IsTrue(seen, "The home page never recorded that this account had been checked.");
        }

        public async Task AssertNoSidewaysScrollAsync(string where)
        {
            var overflow = await Page.EvaluateAsync<int>(
                "() => document.documentElement.scrollWidth - document.documentElement.clientWidth");
            Assert.IsTrue(overflow <= 0, $"The {where} screen scrolls sideways by {overflow}px.");
        }

        public async Task WithDatabaseAsync(Func<AppDbContext, Task> work)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        /// <summary>The person's reply, as the homeserver's sync would deliver it.</summary>
        public async Task ReplyWithCodeAsync(string email)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userId = (await database.Users.AsNoTracking().SingleAsync(user => user.Email == email)).Id;
            var link = await database.UserMatrixDestinations.AsNoTracking().SingleAsync(d => d.UserId == userId);
            await scope.ServiceProvider.GetRequiredService<MatrixInboundRouter>().HandleMessageAsync(
                new MatrixInboundMessage(link.RoomId!, link.MatrixUserId!, link.VerificationCode!),
                CancellationToken.None);
        }

        private async Task SeedAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<ICredentialProtector>();
            var now = DateTimeOffset.UtcNow;

            var cwa = new CwaSettings(now);
            cwa.SetSettings(CwaTransportMode.Local, "/data/cwa-ingest", null, null, null, null,
                CwaSftpAuthenticationMode.PrivateKey, "http://cwa.example.test", null, null, "delivery-account", null, now);
            cwa.SetEreaderServiceAccountPassword(
                protector.Protect(PublishingSecretPurposes.CwaEreaderServiceAccountPassword, "delivery-password"),
                protector.FormatVersion, null, null, now);

            var matrix = new MatrixSettings(now);
            matrix.SetSettings("http://matrix-internal.example.test", "@librarian:example.test", null, now);
            matrix.SetAccessToken(
                protector.Protect(CommunicationSecretPurposes.MatrixAccessToken, "stand-in-token"),
                protector.FormatVersion, null, now);
            matrix.SetEnabled(true, null, now);

            var guidance = new SetupGuidanceSettings(now);
            Assert.IsNull(guidance.TrySetKindle(Sender, "amazon.co.uk", null, now));

            database.AddRange(cwa, matrix, guidance);
            await database.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var disposable in disposables)
            {
                await disposable.DisposeAsync();
            }

            client?.Dispose();
            playwright?.Dispose();
        }
    }
}
