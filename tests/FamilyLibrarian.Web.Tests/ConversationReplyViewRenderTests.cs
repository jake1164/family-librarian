using FamilyLibrarian.Contracts.Conversation;
using FamilyLibrarian.Web.Client.Conversation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace FamilyLibrarian.Web.Tests;

/// <summary>M3-3: what the chat shows for an answer: plain text only, numbered results with named controls, links to requests.</summary>
[TestClass]
public sealed class ConversationReplyViewRenderTests
{
    private static async Task<string> RenderAsync(ConversationReplyResponse reply)
    {
        var services = new ServiceCollection().AddLogging().AddMudServices().AddSingleton<Microsoft.JSInterop.IJSRuntime, NoJs>().BuildServiceProvider();
        await using var _ = services;
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<ConversationReplyView>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Reply"] = reply }));
            return output.ToHtmlString();
        });
    }

    private static ConversationReplyResponse Reply(string text, IReadOnlyList<ConversationChoiceResponse>? choices = null,
        IReadOnlyList<ConversationRequestSummaryResponse>? requests = null, IReadOnlyList<string>? suggested = null, Guid? requestId = null) =>
        new("Text", text, choices ?? [], requests ?? [], suggested ?? [], requestId, false);

    [TestMethod]
    public async Task TextFromACatalogSourceIsEncodedNeverRenderedAsMarkup()
    {
        var html = await RenderAsync(Reply("<script>alert(1)</script> <b>bold</b>",
            [new ConversationChoiceResponse(1, "<img src=x onerror=alert(1)>", "Mara <i>Quill</i>", "2021")]));

        Assert.IsFalse(html.Contains("<script>", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("<img src=x", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("<b>bold</b>", StringComparison.Ordinal));
        StringAssert.Contains(html, "&lt;script&gt;");
        StringAssert.Contains(html, "&lt;img src=x onerror=alert(1)&gt;");
    }

    [TestMethod]
    public async Task ResultsAreAnOrderedListWithNamedPickAndDetailsButtons()
    {
        var html = await RenderAsync(Reply("I found these:",
            [new ConversationChoiceResponse(1, "The Lantern Road", "Mara Quill", "2021"), new ConversationChoiceResponse(2, "Lanterns at Dusk", "Mara Quill", null)]));

        StringAssert.Contains(html, "<ol");
        StringAssert.Contains(html, "aria-label=\"Pick The Lantern Road\"");
        StringAssert.Contains(html, "aria-label=\"Details for Lanterns at Dusk\"");
        StringAssert.Contains(html, "(2021)");
    }

    [TestMethod]
    public async Task QuickRepliesSkipNumbersWhichAlreadyHaveTheirOwnButtons()
    {
        var html = await RenderAsync(Reply("Which format?", suggested: ["ebook", "audiobook", "1", "cancel"]));

        StringAssert.Contains(html, ">ebook<");
        StringAssert.Contains(html, ">cancel<");
        Assert.IsFalse(html.Contains(">1<", StringComparison.Ordinal));
        StringAssert.Contains(html, "aria-label=\"Quick replies\"");
    }

    [TestMethod]
    public async Task StatusAndCreatedRequestsLinkToTheRequestsPage()
    {
        var status = await RenderAsync(Reply("Your requests:",
            requests: [new ConversationRequestSummaryResponse(Guid.NewGuid(), "The Lantern Road", "Mara Quill", "Pending acquisition", ["ebook: Requested"])]));
        var created = await RenderAsync(Reply("Requested “The Lantern Road”.", requestId: Guid.NewGuid()));

        StringAssert.Contains(status, "Pending acquisition");
        StringAssert.Contains(status, "href=\"requests\"");
        StringAssert.Contains(created, "See it in My requests");
    }

    [TestMethod]
    public async Task AStatusReplyShowsEachRequestOnceNotInTheTextAndAgainInTheList()
    {
        var html = await RenderAsync(Reply("Your requests:\n\u2022 The Lantern Road \u2014 Pending acquisition (ebook: Requested)",
            requests: [new ConversationRequestSummaryResponse(Guid.NewGuid(), "The Lantern Road", "Mara Quill", "Pending acquisition", ["ebook: Requested"])]));

        StringAssert.Contains(html, "Your requests:");
        Assert.AreEqual(1, CountOf(html, "The Lantern Road"), "The request appears only in the structured list.");
    }

    [TestMethod]
    public async Task SearchResultsAreNotRepeatedInTheTextButTheLeadAndInstructionsStay()
    {
        var html = await RenderAsync(Reply(
            "I found these for \u201cdune\u201d:\n1. Dune \u2014 Frank Herbert (en)\n2. Dune Messiah \u2014 Frank Herbert (en)\nReply with a number to pick one, \u201cdetails 2\u201d for more.",
            [new ConversationChoiceResponse(1, "Dune", "Frank Herbert", "en"), new ConversationChoiceResponse(2, "Dune Messiah", "Frank Herbert", "en")]));

        StringAssert.Contains(html, "I found these for");
        StringAssert.Contains(html, "Reply with a number to pick one");
        Assert.IsFalse(html.Contains("2. Dune Messiah", StringComparison.Ordinal), "The numbered text line is dropped; the list item remains.");
        StringAssert.Contains(html, "<strong>Dune Messiah</strong>");
    }

    [TestMethod]
    public void TextIsLeftAloneWhenThereAreNoStructuredItemsOrOnlyAnotherKindOfLine()
    {
        var plain = new ConversationReplyResponse("Text", "1. not a result\n• not a request", [], [], [], null, false);
        var onlyRequests = new ConversationReplyResponse(
            "Status", "Your requests:\n• A\n1. kept because there are no structured choices", [],
            [new ConversationRequestSummaryResponse(Guid.NewGuid(), "A", "", "Pending", [])], [], null, false);

        Assert.AreEqual("1. not a result\n• not a request", ConversationReplyText.ForStructuredDisplay(plain));
        Assert.AreEqual("Your requests:\n1. kept because there are no structured choices", ConversationReplyText.ForStructuredDisplay(onlyRequests));
    }

    private static int CountOf(string haystack, string needle) =>
        (haystack.Length - haystack.Replace(needle, string.Empty, StringComparison.Ordinal).Length) / needle.Length;

    [TestMethod]
    public async Task AnAnswerIsAnnouncedAsAnArticleForAssistiveTechnology()
    {
        var html = await RenderAsync(Reply("Hello"));

        StringAssert.Contains(html, "role=\"article\"");
        StringAssert.Contains(html, "aria-label=\"Chat answer\"");
    }

    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }
}
