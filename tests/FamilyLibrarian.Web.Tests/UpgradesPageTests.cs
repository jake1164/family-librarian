using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Upgrades;
using FamilyLibrarian.Contracts.Upgrades;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Client.Authentication;
using FamilyLibrarian.Web.Client.Pages.Admin;
using FamilyLibrarian.Web.Client.Upgrades;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace FamilyLibrarian.Web.Tests;

/// <summary>A6-UPGRADE-1 U-2 administrator page: the on/off choice is saved and honoured, only administrators reach it, and the page says what it means.</summary>
[TestClass]
public sealed class UpgradesPageTests
{
    private static WebTestFixture? fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (fixture is not null) await fixture.DisposeAsync();
    }

    private static async Task<HttpClient> SignedInAsync(FamilyLibrarianAppFactory factory, string email, string password)
    {
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await http.PostAsJsonAsync("/api/auth/login", new FamilyLibrarian.Contracts.Authentication.LoginRequest { Email = email, Password = password })).EnsureSuccessStatusCode();
        http.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(http));
        return http;
    }

    [TestMethod]
    public async Task TheChoiceDefaultsToOnIsSavedByAdministratorsOnlyAndIsHonouredByTheService()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        using var member = await SignedInAsync(factory, WebTestFixture.UserEmail, WebTestFixture.UserPassword);
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Assert.IsTrue((await admin.GetFromJsonAsync<LibraryUpgradeSettingsResponse>("/api/v1/admin/upgrades/settings"))!.AutoReplace, "On unless an administrator turns it off.");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/admin/upgrades/settings")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.GetAsync("/api/v1/admin/upgrades/settings")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync("/api/v1/admin/upgrades/settings", new SetLibraryUpgradeSettingsRequest(false))).StatusCode, "A member cannot switch it.");

        Assert.AreEqual(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/admin/upgrades/settings", new SetLibraryUpgradeSettingsRequest(false))).StatusCode);
        Assert.IsFalse((await admin.GetFromJsonAsync<LibraryUpgradeSettingsResponse>("/api/v1/admin/upgrades/settings"))!.AutoReplace);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audits = await db.AuditEvents.AsNoTracking().Where(a => a.Action == FamilyLibrarian.Domain.Audit.AuditActions.LibraryUpgradeSettingsChanged).ToListAsync();
            Assert.IsTrue(audits.Any(a => a.Detail!.Contains("false", StringComparison.OrdinalIgnoreCase)), "The change is audited.");
            var store = scope.ServiceProvider.GetRequiredService<ILibraryUpgradeStore>();
            Assert.AreEqual(false, await store.GetAutoReplaceAsync(CancellationToken.None), "The saved choice is what the service reads.");
        }

        Assert.AreEqual(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/admin/upgrades/settings", new SetLibraryUpgradeSettingsRequest(true))).StatusCode);
        Assert.IsTrue((await admin.GetFromJsonAsync<LibraryUpgradeSettingsResponse>("/api/v1/admin/upgrades/settings"))!.AutoReplace);
    }

    [TestMethod]
    public async Task WhenSwitchedOffTheServiceDoesNotStartAProposalOnItsOwnButStillStartsOneAnAdministratorApproves()
    {
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString);
        using var admin = await SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);
        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = (await resolve.Content.ReadFromJsonAsync<FamilyLibrarian.Contracts.Catalog.CatalogWorkResponse>())!;
        var created = await admin.PostAsJsonAsync("/api/v1/requests/", new FamilyLibrarian.Contracts.Requests.CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        var request = (await created.Content.ReadFromJsonAsync<FamilyLibrarian.Contracts.Requests.BookRequestResponse>())!;
        var proposal = new LibraryUpgrade(request.Id, request.Formats.Single().FormatId, Guid.NewGuid(), "source-a", "better-1", null, ["artifact.completeness.toc_targets_missing"], DateTimeOffset.UtcNow);
        await using (var seed = factory.Services.CreateAsyncScope())
        {
            seed.ServiceProvider.GetRequiredService<AppDbContext>().LibraryUpgrades.Add(proposal);
            await seed.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync();
        }

        (await admin.PutAsJsonAsync("/api/v1/admin/upgrades/settings", new SetLibraryUpgradeSettingsRequest(false))).EnsureSuccessStatusCode();
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<LibraryUpgradeService>();

        var automatic = await service.StartAsync(proposal.Id, approvedByAdministrator: false, CancellationToken.None);
        var approved = await service.StartAsync(proposal.Id, approvedByAdministrator: true, CancellationToken.None);

        Assert.IsFalse(automatic.Started);
        StringAssert.Contains(automatic.Reason, "off", "Switched off, nothing starts by itself.");
        Assert.IsFalse(approved.Reason?.Contains("off", StringComparison.Ordinal) ?? false, "An administrator's approval is not blocked by the switch (it goes on to the next check).");
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
    }

    private sealed class Routing(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request.RequestUri!.AbsolutePath));
    }

    private static async Task<string> RenderAsync(Func<string, HttpResponseMessage> respond)
    {
        var http = new HttpClient(new Routing(respond)) { BaseAddress = new Uri("http://localhost/") };
        await using var services = new ServiceCollection().AddLogging().AddMudServices().AddSingleton<IJSRuntime, NoJs>().AddSingleton<NavigationManager, TestNavigation>()
            .AddSingleton(new UpgradesApiClient(http, new AntiforgeryTokenProvider(http))).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Upgrades>(ParameterView.Empty)).ToHtmlString());
    }

    private static LibraryUpgradeResponse Row(string state, string title, string[]? reasons = null, string? failure = null, string media = "Ebook") =>
        new(Guid.NewGuid(), Guid.NewGuid(), title, media, state, reasons ?? ["artifact.completeness.toc_targets_missing"], "source-a", failure,
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow);

    private static HttpResponseMessage Ok(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    [TestMethod]
    public async Task ThePageListsBooksInPlainWordsAndOffersActionsOnlyWhereTheyApply()
    {
        var rows = new[]
        {
            Row("Proposed", "Proposed Book"), Row("Acquiring", "Fetching Book"), Row("Completed", "Done Book"),
            Row("Dismissed", "Set Aside Book"), Row("Failed", "Stopped Book", failure: "Both copies were left as they are.", media: "Audiobook",
                reasons: ["artifact.audio.track_set_gap", "some.future.code"])
        };

        var html = await RenderAsync(path => path.EndsWith("/settings", StringComparison.Ordinal) ? Ok(new LibraryUpgradeSettingsResponse(true)) : Ok(rows));

        foreach (var title in new[] { "Proposed Book", "Fetching Book", "Done Book", "Set Aside Book", "Stopped Book" }) StringAssert.Contains(html, title);
        StringAssert.Contains(html, "Better copy found");
        StringAssert.Contains(html, "Fetching");
        StringAssert.Contains(html, "Upgraded");
        StringAssert.Contains(html, "Set aside");
        StringAssert.Contains(html, "Upgrade stopped", "A failed upgrade is not labelled a failed scan.");
        Assert.IsFalse(html.Contains("Scan failed", StringComparison.Ordinal));
        StringAssert.Contains(html, "Contents entries point at missing text");
        StringAssert.Contains(html, "Tracks missing from the set");
        StringAssert.Contains(html, "some.future.code", "An unknown reason is shown, not hidden.");
        StringAssert.Contains(html, "Both copies were left as they are.");
        Assert.AreEqual(1, CountOf(html, "Replace Proposed Book with the better copy"), "Only a proposal has Replace.");
        Assert.AreEqual(1, CountOf(html, "Set aside the better copy of Proposed Book"));
        Assert.IsFalse(html.Contains("Replace Done Book", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("Replace Stopped Book", StringComparison.Ordinal));
        StringAssert.Contains(html, "Replace incomplete books automatically");
        StringAssert.Contains(html, "On.");
    }

    [TestMethod]
    public async Task ThePageSaysWhenAutomaticReplacementIsOffAndWhenThereIsNothingYet()
    {
        var html = await RenderAsync(path => path.EndsWith("/settings", StringComparison.Ordinal) ? Ok(new LibraryUpgradeSettingsResponse(false)) : Ok(Array.Empty<LibraryUpgradeResponse>()));

        StringAssert.Contains(html, "Off. Better copies are only listed here");
        StringAssert.Contains(html, "No better copies have been found yet.");
    }

    [TestMethod]
    public async Task ThePageSaysSoWhenItCannotLoadInsteadOfShowingAnEmptyList()
    {
        var html = await RenderAsync(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        StringAssert.Contains(html, "could not be loaded");
        Assert.IsFalse(html.Contains("No better copies", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("Proposed", Color.Warning)]
    [DataRow("Acquiring", Color.Info)]
    [DataRow("Publishing", Color.Info)]
    [DataRow("Verified", Color.Info)]
    [DataRow("Completed", Color.Success)]
    [DataRow("Dismissed", Color.Default)]
    [DataRow("Failed", Color.Error)]
    public void UpgradeStatusColorsFollowTheSharedMeaning(string state, Color expected) =>
        Assert.AreEqual(expected, FamilyLibrarian.Web.Client.Theme.MediaTypeVisuals.StatusColor(state));

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var index = haystack.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
