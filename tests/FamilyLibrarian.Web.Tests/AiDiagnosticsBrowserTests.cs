using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
[TestCategory("Release")]
public sealed class AiDiagnosticsBrowserTests
{
    [TestMethod]
    public async Task TasksCountsOpenFilteredDiagnosticsWithPagingHistoryAndEmptyStates()
    {
        if (Environment.GetEnvironmentVariable("FAMILY_LIBRARIAN_AI_DIAGNOSTICS_BROWSER_TESTS") != "1")
            Assert.Inconclusive("Set FAMILY_LIBRARIAN_AI_DIAGNOSTICS_BROWSER_TESTS=1 for the isolated AI diagnostic browser regression.");
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var original = new FamilyLibrarianAppFactory(fixture.ConnectionString);
        await using var factory = original.WithWebHostBuilder(builder => builder.UseStaticWebAssets());
        factory.UseKestrel(0);
        using var client = factory.CreateClient();
        var artifact = await AiFailureEndpointTests.SeedTrackAsync(factory.Services);
        using (var scope = factory.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IModelUsageLedger>();
            var fileCall = new ModelUsageRecord(ModelRole.ArtifactValidationDecision, ModelBudgetClass.BackgroundValidation, null,
                "example-model", false, 100, DateTimeOffset.UtcNow.AddMinutes(-1), artifact.Id);
            await ledger.AddAsync(fileCall, CancellationToken.None);
            await ledger.CompleteAsync(fileCall.Id, ModelCallOutcome.SchemaRejected, 123, null, CancellationToken.None, ModelFailureReason.UngroundedConcern);
            var providerError = new ModelUsageRecord(ModelRole.CandidateTriage, ModelBudgetClass.BackgroundValidation, null,
                "example-model", false, 100, DateTimeOffset.UtcNow.AddMinutes(-1));
            await ledger.AddAsync(providerError, CancellationToken.None);
            await ledger.CompleteAsync(providerError.Id, ModelCallOutcome.Timeout, 30000, null, CancellationToken.None);
            for (var i = 0; i < 2; i++)
            {
                var used = new ModelUsageRecord(ModelRole.ArtifactValidationDecision, ModelBudgetClass.BackgroundValidation, null,
                    "example-better", false, 100, DateTimeOffset.UtcNow.AddMinutes(-1), artifact.Id);
                await ledger.AddAsync(used, CancellationToken.None);
                await ledger.CompleteAsync(used.Id, ModelCallOutcome.Succeeded, 123, null, CancellationToken.None);
                await ledger.ObserveWorkflowAsync(used.Id, i == 0 ? ModelWorkflowEffect.NoChange : ModelWorkflowEffect.FileHoldApplied, CancellationToken.None);
            }
            for (var i = 0; i < 53; i++)
            {
                var record = new ModelUsageRecord(ModelRole.SearchGuidance, ModelBudgetClass.BackgroundValidation, null,
                    "example-model", false, 100, DateTimeOffset.UtcNow.AddMinutes(-1).AddDays(i == 52 ? -2 : 0));
                await ledger.AddAsync(record, CancellationToken.None);
                await ledger.CompleteAsync(record.Id, ModelCallOutcome.SchemaRejected, 123, null, CancellationToken.None,
                    ModelFailureReason.RepeatedSearch);
            }
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
        await page.GotoAsync(new Uri(client.BaseAddress!, "/admin/tasks").ToString());
        var activity = page.GetByRole(AriaRole.Table, new() { Name = "AI calls by feature", Exact = true });
        await activity.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        var searchRow = activity.GetByRole(AriaRole.Row).Filter(new() { HasTextString = "Suggest additional searches" });
        await Assertions.Expect(searchRow.GetByText("Provider replies: 100% (52/52)", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(searchRow.GetByText("Accepted replies: 0% (0/52)", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(searchRow.GetByRole(AriaRole.Link, new() { Name = "52 rejected replies for Suggest additional searches", Exact = true })).ToBeVisibleAsync();
        var providerRow = activity.GetByRole(AriaRole.Row).Filter(new() { HasTextString = "Review candidates before acquisition" });
        await Assertions.Expect(providerRow.GetByRole(AriaRole.Link, new() { Name = "1 provider errors for Review candidates before acquisition", Exact = true })).ToBeVisibleAsync();
        await page.GetByText("Quality by model and recorded recommendations", new() { Exact = true }).ClickAsync();
        var byModel = page.GetByRole(AriaRole.Table, new() { Name = "AI operational quality by model", Exact = true });
        var better = byModel.GetByRole(AriaRole.Row).Filter(new() { HasTextString = "example-better" });
        await Assertions.Expect(better.GetByText("Accepted replies: 100% (2/2)", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(better.GetByText("Calls used for workflow actions: 1", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(better.GetByText("No change or abstained: 1", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(better.GetByText("File hold applied: 1", new() { Exact = true })).ToBeVisibleAsync();
        var screenshotDirectory = Environment.GetEnvironmentVariable("FAMILY_LIBRARIAN_AI_QUALITY_SCREENSHOT_DIR");
        if (screenshotDirectory is not null)
        {
            Directory.CreateDirectory(screenshotDirectory);
            await page.SetViewportSizeAsync(1920, 1080);
            await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotDirectory, "quality-desktop.png"), FullPage = true });
            await page.SetViewportSizeAsync(390, 844);
            await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotDirectory, "quality-mobile.png"), FullPage = true });
            await page.SetViewportSizeAsync(1280, 720);
        }
        await page.GetByRole(AriaRole.Link, new() { Name = "52 rejected replies for Suggest additional searches", Exact = true }).ClickAsync();
        await page.WaitForURLAsync("**/admin/ai-failures?role=SearchGuidance&category=RejectedReply");
        var table = page.GetByRole(AriaRole.Table, new() { Name = "AI call diagnostics", Exact = true });
        var rows = table.Locator("tbody tr");
        await Assertions.Expect(rows).ToHaveCountAsync(50);
        await Assertions.Expect(table.GetByText("The response suggested a search already tried, the original request, or a duplicate suggestion.").First).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Diagnostic category", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Provider errors", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("No diagnostic calls match the selected filters.", new() { Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Combobox, new() { Name = "AI feature", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Review candidates before acquisition", Exact = true }).ClickAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(1);
        await Assertions.Expect(table.GetByRole(AriaRole.Cell, new() { Name = "Timeout", Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Diagnostic category", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Rejected replies", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("No diagnostic calls match the selected filters.", new() { Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Combobox, new() { Name = "AI feature", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Suggest additional searches", Exact = true }).ClickAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(50);
        var next = page.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true });
        await next.ClickAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(2);
        await Assertions.Expect(next).ToBeDisabledAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Previous", Exact = true }).ClickAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(50);
        await page.GetByRole(AriaRole.Combobox, new() { Name = "History", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Last 7 days", Exact = true }).ClickAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(50);
        await next.ClickAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(3);
        await page.GetByRole(AriaRole.Combobox, new() { Name = "AI feature", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Review files before approval", Exact = true }).ClickAsync();
        await Assertions.Expect(table.GetByText("Track 12 of 80", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(table.GetByRole(AriaRole.Link, new() { Name = "Book · MP3", Exact = true })).ToHaveAttributeAsync("href", $"works/{artifact.WorkId}");
        await table.GetByText("File references", new() { Exact = true }).ClickAsync();
        await Assertions.Expect(table.GetByText($"Bundle: {artifact.BundleId}", new() { Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Combobox, new() { Name = "AI feature", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Identify books from cover images", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("No diagnostic calls match the selected filters.", new() { Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh diagnostics", Exact = true }).ClickAsync();
        await Assertions.Expect(next).ToBeDisabledAsync();
        await page.SetViewportSizeAsync(390, 844);
        await Assertions.Expect(page.GetByText("Each row is one call, not one book.", new() { Exact = false })).ToBeVisibleAsync();
    }
}
