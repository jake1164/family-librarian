using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.ModelGateway;
using FamilyLibrarian.Web.Client.Authentication;
using FamilyLibrarian.Web.Client.ModelGateway;
using FamilyLibrarian.Web.Client.Pages.Admin;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace FamilyLibrarian.Web.Tests;

/// <summary>The saved-decisions panel and its form: plain wording, counts only, and a form that knows what changed.</summary>
[TestClass]
public sealed class DecisionCapturePanelTests
{
    private static readonly DecisionCaptureRangesDto Ranges = new(50, 1_000, 30, 730, 90, 1_825, 1, 20, 30, 730, 32_768);
    private static readonly DecisionCaptureLimitsDto Defaults = new(200, 180, null, 10, 180);

    private static DecisionCaptureSettingsResponse Settings(bool decisions = true, bool chat = false, DecisionCaptureLimitsDto? limits = null) =>
        new(decisions, chat, limits ?? Defaults, Ranges, Defaults);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static async Task<string> RenderAsync(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var http = new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://localhost/") };
        await using var services = new ServiceCollection().AddLogging().AddMudServices().AddSingleton<IJSRuntime, NoJs>()
            .AddSingleton(new ModelGatewayApiClient(http, new AntiforgeryTokenProvider(http))).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<DecisionCapturePanel>(ParameterView.Empty)).ToHtmlString());
    }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    [TestMethod]
    public async Task ThePanelExplainsWhatIsSavedWhatIsNeverSavedAndThatNothingIsSavedYetAndShowsOnlyCounts()
    {
        var insight = new DecisionCaptureInsightResponse(DateTimeOffset.UtcNow,
            [new("CandidateTriage", 4, 1, 3, DateTimeOffset.UtcNow.AddDays(-9), DateTimeOffset.UtcNow, 2_048),
             new("SearchGuidance", 0, 0, 0, null, null, 0)],
            [new("Decision snapshots", 4, 16_384, DateTimeOffset.UtcNow.AddDays(-9)), new("Usage ledger", 120, 1_572_864, null)],
            1_589_248, true);

        var html = await RenderAsync(request => request.RequestUri!.AbsolutePath.EndsWith("/insight", StringComparison.Ordinal) ? Json(insight) : Json(Settings()));

        StringAssert.Contains(html, "Saved decisions");
        StringAssert.Contains(html, "Book");
        StringAssert.Contains(html, "excerpts, file names, hashes and download links are never saved");
        StringAssert.Contains(html, "reading interests");
        StringAssert.Contains(html, "database backups");
        StringAssert.Contains(html, "There is no export");
        StringAssert.Contains(html, "Nothing is saved yet by this release");
        StringAssert.Contains(html, "on by default, including on installations upgraded");
        StringAssert.Contains(html, "Save the decisions AI features make");
        StringAssert.Contains(html, "Also save chat messages (more personal; off by default)");
        StringAssert.Contains(html, "Counts and approximate sizes only");
        StringAssert.Contains(html, "Sizes include indexes and are approximate.");
        StringAssert.Contains(html, "Review candidates before acquisition");
        StringAssert.Contains(html, "1.5 MB");
        StringAssert.Contains(html, "Delete saved decisions…");
        StringAssert.Contains(html, "cannot be undone");
        StringAssert.Contains(html, "works even while AI is off");
        Assert.IsFalse(html.Contains("Yes, delete", StringComparison.Ordinal), "The destructive confirmation is not shown until asked for.");
    }

    [TestMethod]
    public async Task WhenTheDatabaseReportsNoSizesThePanelSaysSoAndStillShowsCounts()
    {
        var insight = new DecisionCaptureInsightResponse(DateTimeOffset.UtcNow, [], [new("Decision snapshots", 2, 0, null)], 0, SizesAvailable: false);

        var html = await RenderAsync(request => request.RequestUri!.AbsolutePath.EndsWith("/insight", StringComparison.Ordinal) ? Json(insight) : Json(Settings()));

        StringAssert.Contains(html, "The database did not report sizes, so only counts are shown.");
    }

    [TestMethod]
    public async Task APanelThatCannotLoadSaysSoInsteadOfShowingEmptyControls()
    {
        var html = await RenderAsync(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        StringAssert.Contains(html, "could not be loaded");
        Assert.IsFalse(html.Contains("Save the decisions AI features make", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TheFormLoadsRoundTripsAndNoticesChanges()
    {
        var saved = Settings(limits: new DecisionCaptureLimitsDto(120, 100, 400, 7, 45));
        var form = new DecisionCaptureForm();

        form.Load(saved);

        Assert.IsFalse(form.DiffersFrom(saved));
        Assert.IsTrue(form.LimitLabeled);
        Assert.AreEqual(400, form.LabeledDays);
        Assert.AreEqual(saved.Limits, form.ToLimits());
        form.SnapshotsPerRole = 60;
        Assert.IsTrue(form.DiffersFrom(saved));
        form.SnapshotsPerRole = 120;
        form.LimitLabeled = false;
        Assert.IsNull(form.ToLimits().LabeledSnapshotDays, "Turning the labeled expiry off sends none, not the last number typed.");
        Assert.IsTrue(form.DiffersFrom(saved));
        form.DecisionCapture = false;
        Assert.IsFalse(form.ToRequest().DecisionCaptureEnabled);
    }

    [TestMethod]
    public void WhenNoLabeledExpiryIsSetTheFormOffersTheMinimumAsItsStartingValue()
    {
        var form = new DecisionCaptureForm();

        form.Load(Settings());

        Assert.IsFalse(form.LimitLabeled);
        Assert.AreEqual(90, form.LabeledDays);
        Assert.IsFalse(form.DiffersFrom(Settings()));
    }

    [TestMethod]
    [DataRow(0L, "0 B")]
    [DataRow(1_023L, "1023 B")]
    [DataRow(2_048L, "2 KB")]
    [DataRow(1_572_864L, "1.5 MB")]
    public void SizesAreShownInPlainUnits(long bytes, string expected) => Assert.AreEqual(expected, DecisionCaptureText.Size(bytes));

    [TestMethod]
    public void CountsAreWorded()
    {
        Assert.AreEqual("1 call", DecisionCaptureText.Plural(1, "call"));
        Assert.AreEqual("0 calls", DecisionCaptureText.Plural(0, "call"));
        Assert.AreEqual("Deleted 1 saved decision, 2 calls and 0 results.", DecisionCaptureText.Purged(new DecisionCapturePurgeResponse(1, 2, 0, 5)));
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
