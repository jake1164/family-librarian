using FamilyLibrarian.Web.Client.Gutenberg;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace FamilyLibrarian.Web.Client.Tests;

[TestClass]
public sealed class GutenbergCatalogProgressTests
{
    [TestMethod]
    public async Task DownloadShowsBytesSourceAndDeterminateProgressInsteadOfZeroImportedBooks()
    {
        var html = await RenderAsync(Status("Downloading") with
        { DownloadedBytes = 52_428_800, DownloadSizeBytes = 104_857_600, SourceHost = "mirror.example" });
        StringAssert.Contains(html, "50.0 MiB of 100.0 MiB");
        StringAssert.Contains(html, "mirror.example");
        StringAssert.Contains(html, "aria-valuenow=\"50\"");
        StringAssert.Contains(html, "server will continue");
        Assert.IsFalse(html.Contains("Imported so far", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UnknownDownloadSizeAndSeparateDecompressionPhaseAreTruthful()
    {
        var downloading = await RenderAsync(Status("Downloading"));
        StringAssert.Contains(downloading, "total size not supplied");
        var decompressing = await RenderAsync(Status("Decompressing"));
        StringAssert.Contains(decompressing, "Download complete. Decompressing");
        Assert.IsFalse(decompressing.Contains("Imported so far", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ImportAndFeedChecksShowCountsAndEncodeSourceMessages()
    {
        var html = await RenderAsync(Status("Importing") with
        { InProgressBookCount = 250, InProgressFormatCount = 1000, ProgressMessage = "<script>bad</script>" });
        StringAssert.Contains(html, "250 books");
        StringAssert.Contains(html, "1,000 formats");
        Assert.IsFalse(html.Contains("<script>", StringComparison.Ordinal));
        StringAssert.Contains(html, "&lt;script&gt;");
    }

    [TestMethod]
    public async Task QueuedFirstImportDoesNotClaimAnExistingSearchableCatalogue()
    {
        var html = await RenderAsync(Status("Queued") with { IsReady = false });
        StringAssert.Contains(html, "Refresh queued");
        Assert.IsFalse(html.Contains("remains searchable", StringComparison.Ordinal));
    }

    private static GutenbergCatalogStatusResponse Status(string phase) => new(
        true, null, DateTimeOffset.UtcNow, null, 0, 0, 0, 0, DateTimeOffset.UtcNow, phase, null);

    private static async Task<string> RenderAsync(GutenbergCatalogStatusResponse status)
    {
        await using var services = new ServiceCollection().AddLogging().AddMudServices().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<GutenbergCatalogProgress>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(GutenbergCatalogProgress.Status)] = status }))).ToHtmlString());
    }
}
