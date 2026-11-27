using Bunit;
using FamilyLibrarian.Contracts.Setup;
using FamilyLibrarian.Web.Client.Setup;
using MudBlazor.Services;

namespace FamilyLibrarian.Web.Client.Tests;

/// <summary>What the setup instructions say, depending on what the administrator has filled in.</summary>
[TestClass]
public sealed class SetupGuideTests
{
    private static BunitContext NewContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.AddAuthorization().SetAuthorized("reader");
        return context;
    }

    private static MessagingGuidanceResponse Messaging(
        string? serverName = "example.org", string? instructions = null, string? helpUrl = null,
        string? webAppUrl = null, bool showElementHelp = true) =>
        new(serverName, "@librarian:example.org", instructions, helpUrl, webAppUrl, showElementHelp, ChatEnabled: false);

    [TestMethod]
    [DataRow("reader@kindle.com", false)]
    [DataRow("reader@free.kindle.com", false)]
    [DataRow("reader@gmail.com", true)]
    [DataRow("reader", false)]
    [DataRow("reader@kin", false)]
    [DataRow("", false)]
    public void OnlyACompleteNonKindleAddressIsQuestioned(string address, bool warns) =>
        Assert.AreEqual(warns, KindleAddress.Warning(address) is not null);

    [TestMethod]
    public async Task TheKindleStepsShowTheRealSenderToCopyAndTheChosenStoreLinks()
    {
        await using var context = NewContext();

        var cut = context.Render<KindleSetupGuide>(parameters => parameters.Add(
            component => component.Guidance,
            new KindleGuidanceResponse(
                "library@example.org",
                "https://www.amazon.co.uk/sendtokindle/email",
                "https://www.amazon.co.uk/hz/mycd/myx")));

        Assert.AreEqual("library@example.org", cut.Find(".setup-copy-value").TextContent);
        Assert.IsNotNull(cut.Find("[aria-label='Copy sender address']"));
        var links = cut.FindAll("a").Select(link => link.GetAttribute("href")).ToArray();
        CollectionAssert.Contains(links, "https://www.amazon.co.uk/hz/mycd/myx");
        CollectionAssert.Contains(links, "https://www.amazon.co.uk/sendtokindle/email");
        Assert.IsTrue(cut.FindAll("a").All(link => link.GetAttribute("rel") == "noopener noreferrer"));
    }

    [TestMethod]
    public async Task WithoutASenderPeopleAreToldToAskInsteadOfBeingGivenAGuess()
    {
        await using var context = NewContext();

        var cut = context.Render<KindleSetupGuide>(parameters => parameters.Add(
            component => component.Guidance,
            new KindleGuidanceResponse(null, "https://www.amazon.com/a", "https://www.amazon.com/b")));

        StringAssert.Contains(cut.Markup, "Ask your library administrator which address to approve.");
        Assert.IsEmpty(cut.FindAll(".setup-copy-value"));
        Assert.IsEmpty(cut.FindAll("a[href='settings/publishing']"), "Only an administrator is sent to settings.");
    }

    [TestMethod]
    public async Task ElementHelpOffersTheStoresAndACodeForTheChosenServer()
    {
        await using var context = NewContext();

        var cut = context.Render<MessagingSetupGuide>(parameters => parameters.Add(
            component => component.Guidance, Messaging()));

        var code = cut.FindComponent<QrCodeImage>();
        Assert.AreEqual("https://mobile.element.io/element/?account_provider=example.org", code.Instance.Text);
        StringAssert.Contains(cut.Markup, "https://apps.apple.com/app/id1631335820");
        StringAssert.Contains(cut.Markup, "https://play.google.com/store/apps/details?id=io.element.android.x");
        StringAssert.Contains(cut.Markup, MessagingSetupGuide.DefaultInstructions);
        Assert.AreEqual("example.org", cut.Find(".setup-copy-value").TextContent);
    }

    [TestMethod]
    public async Task WithElementHelpOffOnlyTheAdministratorsOwnHelpIsShown()
    {
        await using var context = NewContext();

        var cut = context.Render<MessagingSetupGuide>(parameters => parameters.Add(
            component => component.Guidance,
            Messaging(instructions: "Use your library login. <script>alert(1)</script>",
                helpUrl: "https://help.example.org/", webAppUrl: "https://web.example.org/", showElementHelp: false)));

        Assert.IsEmpty(cut.FindComponents<QrCodeImage>());
        Assert.IsFalse(cut.Markup.Contains("Element", StringComparison.Ordinal));
        Assert.IsFalse(cut.Markup.Contains(MessagingSetupGuide.DefaultInstructions, StringComparison.Ordinal));
        Assert.IsEmpty(cut.FindAll("script"), "Instructions are plain text.");
        StringAssert.Contains(cut.Find(".setup-sign-in-instructions").TextContent, "<script>alert(1)</script>");
        var links = cut.FindAll("a").Select(link => link.GetAttribute("href")).ToArray();
        CollectionAssert.Contains(links, "https://help.example.org/");
        CollectionAssert.Contains(links, "https://web.example.org/");
    }

    [TestMethod]
    public async Task WithoutAServerNameThereIsNoCodeThatWouldPickTheWrongServer()
    {
        await using var context = NewContext();

        var cut = context.Render<MessagingSetupGuide>(parameters => parameters.Add(
            component => component.Guidance, Messaging(serverName: null)));

        Assert.IsEmpty(cut.FindComponents<QrCodeImage>());
        Assert.IsFalse(cut.Markup.Contains("mobile.element.io", StringComparison.Ordinal));
        Assert.IsEmpty(cut.FindAll(".setup-copy-value"));
    }

    [TestMethod]
    public async Task TheQrCodeIsASelfContainedLabelledDrawing()
    {
        await using var context = NewContext();

        var cut = context.Render<QrCodeImage>(parameters => parameters
            .Add(component => component.Text, "https://mobile.element.io/element/?account_provider=example.org")
            .Add(component => component.Description, "Code for a phone"));

        var svg = cut.Find("svg");
        Assert.AreEqual("img", svg.GetAttribute("role"));
        Assert.AreEqual("Code for a phone", svg.GetAttribute("aria-label"));
        Assert.IsTrue(cut.Find("path").GetAttribute("d")!.Length > 100);
        Assert.AreEqual("#ffffff", cut.Find("rect").GetAttribute("fill"));
    }
}
