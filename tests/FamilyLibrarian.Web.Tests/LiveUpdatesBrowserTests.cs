using System.Collections.Concurrent;
using FamilyLibrarian.Domain.Notifications;
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
        await Assertions.Expect(page.GetByText("Live updates connected", new() { Exact = true })).ToBeVisibleAsync();

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
            await Assertions.Expect(page.GetByText("Live updates connected", new() { Exact = true })).ToBeVisibleAsync();
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
        await Assertions.Expect(page.GetByText("Live updates connected", new() { Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = 45_000 });
        await page.GetByLabel("Notifications", new() { Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText(title, new() { Exact = true })).ToBeVisibleAsync();
        Assert.HasCount(1, connections.Snapshot());
        Assert.IsTrue(pageErrors.IsEmpty, string.Join(Environment.NewLine, pageErrors));
    }
}
