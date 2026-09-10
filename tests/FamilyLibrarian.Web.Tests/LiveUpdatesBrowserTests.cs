using System.Collections.Concurrent;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Domain.Delivery;
using FamilyLibrarian.Contracts.Realtime;
using Microsoft.EntityFrameworkCore;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Realtime;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class LiveUpdatesBrowserTests
{
    [TestMethod]
    public async Task KindleHistoryAndAdminReceiptUpdateAcrossTabsAndPublishingDraftSurvivesRefresh()
    {
        if (Environment.GetEnvironmentVariable("FAMILY_LIBRARIAN_LIVE_BROWSER_TESTS") != "1")
            Assert.Inconclusive("Set FAMILY_LIBRARIAN_LIVE_BROWSER_TESTS=1 to run the isolated Chromium live-update regression.");
        await using var fixture = await WebTestFixture.CreateAsync();
        var available = WebTestFixture.Require(fixture);
        await using var original = new FamilyLibrarianAppFactory(available.ConnectionString);
        await using var factory = original.WithWebHostBuilder(builder => builder.UseStaticWebAssets());
        factory.UseKestrel(0);
        using var client = factory.CreateClient();
        Guid firstId;
        Guid ownerId;
        Guid targetId;
        var now = DateTimeOffset.UtcNow;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            ownerId = (await db.Users.SingleAsync(user => user.Email == WebTestFixture.UserEmail)).Id;
            var target = new DeliveryTarget(ownerId, DeliveryTargetProvider.CwaKindleEmail, "Kindle", "owner@kindle.com", now);
            var attempt = new DeliveryAttempt(null, ownerId, target.Id, "cwa", "42", "epub", false, 1, now, "Browser Kindle book");
            db.DeliveryTargets.Add(target);
            db.DeliveryAttempts.Add(attempt);
            await db.SaveChangesAsync();
            firstId = attempt.Id;
            targetId = target.Id;
        }
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            ExecutablePath = Environment.GetEnvironmentVariable("FAMILY_LIBRARIAN_E2E_CHROMIUM_EXECUTABLE")
        });
        await using var ownerContext = await browser.NewContextAsync();
        await using var adminContext = await browser.NewContextAsync();
        var owner = await ownerContext.NewPageAsync();
        var admin = await adminContext.NewPageAsync();
        var errors = new ConcurrentQueue<string>();
        owner.PageError += (_, error) => errors.Enqueue(error);
        admin.PageError += (_, error) => errors.Enqueue(error);
        await LoginAsync(owner, client.BaseAddress!, WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        await LoginAsync(admin, client.BaseAddress!, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        await owner.GotoAsync(new Uri(client.BaseAddress!, "/deliveries").ToString());
        await admin.GotoAsync(new Uri(client.BaseAddress!, "/admin/publishing").ToString());
        await WaitForConnectionsAsync(factory.Services.GetRequiredService<LiveConnections>(), 2);
        await Assertions.Expect(owner.GetByText("Waiting to send", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(admin.GetByText("Waiting to send", new() { Exact = true })).ToBeVisibleAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var attempt = await db.DeliveryAttempts.SingleAsync(row => row.Id == firstId);
            attempt.TransitionTo(DeliveryAttemptStatus.Submitting, now);
            attempt.TransitionTo(DeliveryAttemptStatus.Failed, now, "offline", retryable: true);
            await db.SaveChangesAsync();
        }
        await Assertions.Expect(owner.GetByText("Delivery failed", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(admin.GetByText("Delivery failed", new() { Exact = true })).ToBeVisibleAsync();
        Guid sentId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var sent = new DeliveryAttempt(null, ownerId, targetId, "cwa", "42", "epub", false, 2, now,
                "Browser Kindle book", firstId);
            sent.TransitionTo(DeliveryAttemptStatus.Submitting, now);
            sent.TransitionTo(DeliveryAttemptStatus.Submitted, now);
            db.DeliveryAttempts.Add(sent);
            db.NotificationEvents.Add(new NotificationEvent(NotificationAudience.SingleUser, ownerId,
                "delivery.kindle_confirmation_requested", NotificationSeverity.Info, "Did Browser Kindle book arrive?",
                null, "delivery_attempt", sent.Id.ToString(), now));
            await db.SaveChangesAsync();
            sentId = sent.Id;
        }
        var ownerSent = owner.Locator($"[data-attempt-id='{sentId}']");
        var adminSent = admin.Locator($"[data-attempt-id='{sentId}']");
        await Assertions.Expect(ownerSent.GetByText("Sent — receipt unconfirmed", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(adminSent.GetByText("Sent — receipt unconfirmed", new() { Exact = true })).ToBeVisibleAsync();
        await ownerSent.GetByRole(AriaRole.Button, new() { Name = "Not received", Exact = true }).ClickAsync();
        await Assertions.Expect(adminSent.GetByText("Sent, but not received", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(adminSent.GetByRole(AriaRole.Button, new() { Name = "Retry", Exact = true })).ToBeVisibleAsync();
        await ownerSent.GetByRole(AriaRole.Button, new() { Name = "Received", Exact = true }).ClickAsync();
        await Assertions.Expect(adminSent.GetByText("Received", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(adminSent.GetByRole(AriaRole.Button, new() { Name = "Retry", Exact = true })).ToHaveCountAsync(0);
        await owner.GetByLabel("Notifications", new() { Exact = true }).ClickAsync();
        await owner.Locator($"a[href='deliveries/{sentId}']").Last.ClickAsync();
        await owner.WaitForURLAsync(url => new Uri(url).AbsolutePath == $"/deliveries/{sentId}");
        await Assertions.Expect(owner.Locator("[data-attempt-id]")).ToHaveCountAsync(1);

        await admin.GotoAsync(new Uri(client.BaseAddress!, "/settings/publishing").ToString());
        await admin.GetByRole(AriaRole.Switch).First.ClickAsync();
        var username = admin.GetByLabel("Service account username", new() { Exact = true });
        var password = admin.GetByLabel("Service account password", new() { Exact = true });
        await username.FillAsync("unsaved-kindle-service");
        await password.FillAsync("unsaved-test-password");
        await admin.RunAndWaitForResponseAsync(async () =>
            await factory.Services.GetRequiredService<LiveUpdatesPublisher>().PublishAsync(
                new LiveChanges { AdminTopics = LiveUpdateTopics.Publishing }),
            response => response.Url.Contains("/api/v1/admin/publishing/cwa", StringComparison.Ordinal) && response.Request.Method == "GET");
        await Assertions.Expect(username).ToHaveValueAsync("unsaved-kindle-service");
        await Assertions.Expect(password).ToHaveValueAsync("unsaved-test-password");
        Assert.IsTrue(errors.IsEmpty, string.Join(Environment.NewLine, errors));
    }

    private static async Task WaitForConnectionsAsync(LiveConnections connections, int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (connections.Snapshot().Length != count && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50);
        Assert.HasCount(count, connections.Snapshot());
    }

    private static async Task LoginAsync(IPage page, Uri baseAddress, string email, string password)
    {
        await page.GotoAsync(new Uri(baseAddress, "/login").ToString());
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");

    }

    [TestMethod]
    public async Task NavigationSharesOneSocketAndReconnectRestoresMissedNotifications()
    {
        if (Environment.GetEnvironmentVariable("FAMILY_LIBRARIAN_LIVE_BROWSER_TESTS") != "1")
            Assert.Inconclusive("Set FAMILY_LIBRARIAN_LIVE_BROWSER_TESTS=1 to run the isolated Chromium live-update regression.");

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
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var pageErrors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => pageErrors.Enqueue(error);
        await page.GotoAsync(new Uri(client.BaseAddress!, "/login").ToString());
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(FamilyLibrarianAppFactory.AdminEmail);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(FamilyLibrarianAppFactory.AdminPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");


        await WaitForConnectionsAsync(factory.Services.GetRequiredService<LiveConnections>(), 1);
        var extraSockets = 0;
        page.WebSocket += (_, socket) =>
        {
            if (socket.Url.Contains("/api/v1/live", StringComparison.Ordinal)) Interlocked.Increment(ref extraSockets);
        };
        await page.GetByLabel("Open navigation", new() { Exact = true }).ClickAsync();
        foreach (var path in new[] { "admin/security", "admin/tasks", "requests", "admin/publishing" })
        {
            await page.Locator($"a[href='{path}']").First.ClickAsync();
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/" + path);

        }
        Assert.AreEqual(0, extraSockets, "SPA navigation must reuse the tab's existing socket.");
        var connections = factory.Services.GetRequiredService<LiveConnections>();
        Assert.HasCount(1, connections.Snapshot());

        await context.SetOfflineAsync(true);
        connections.Snapshot().Single().Context.Abort();
        await Assertions.Expect(page.GetByText("Reconnecting — displayed data may be out of date", new() { Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = 45_000 });
        var title = "Missed live notification " + Guid.NewGuid().ToString("N");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            database.NotificationEvents.Add(new NotificationEvent(NotificationAudience.AdminBroadcast, null,
                "test", NotificationSeverity.Info, title, null, null, null, DateTimeOffset.UtcNow));
            await database.SaveChangesAsync();
        }
        await context.SetOfflineAsync(false);
        await WaitForConnectionsAsync(connections, 1);
        await page.GetByLabel("Notifications", new() { Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText(title, new() { Exact = true })).ToBeVisibleAsync();
        Assert.HasCount(1, connections.Snapshot());
        Assert.IsTrue(pageErrors.IsEmpty, string.Join(Environment.NewLine, pageErrors));
    }
}
