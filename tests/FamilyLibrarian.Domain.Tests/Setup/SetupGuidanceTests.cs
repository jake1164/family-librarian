using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Domain.Setup;

namespace FamilyLibrarian.Domain.Tests.Setup;

[TestClass]
public sealed class SetupGuidanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow("amazon.com", "amazon.com")]
    [DataRow("Amazon.CO.UK", "amazon.co.uk")]
    [DataRow("www.amazon.de", "amazon.de")]
    [DataRow("amazon.com.au/", "amazon.com.au")]
    [DataRow("https://www.amazon.ca/hz/mycd/myx?ref=abc#/home", "amazon.ca")]
    [DataRow("https://smile.amazon.com./dp/1", "amazon.com")]
    public void AStoreCanBeTypedOrPastedAsAnyLinkFromIt(string input, string expected)
    {
        Assert.IsTrue(AmazonStore.TryNormalize(input, out var domain));
        Assert.AreEqual(expected, domain);
    }

    [TestMethod]
    [DataRow("example.com")]
    [DataRow("notamazon.com")]
    [DataRow("amazon.example.net")]
    [DataRow("amazon.com.example.net")]
    [DataRow("https://amazon.com@example.net/")]
    [DataRow("https://example.net/amazon.com")]
    [DataRow("ftp://amazon.com")]
    [DataRow("javascript://amazon.com/%0Aalert(1)")]
    [DataRow("amazon")]
    public void AnAddressThatIsNotAnAmazonStoreIsRefused(string input) =>
        Assert.IsFalse(AmazonStore.TryNormalize(input, out _));

    [TestMethod]
    public void ABlankStoreUsesTheDefaultForEveryLink()
    {
        Assert.IsTrue(AmazonStore.TryNormalize("  ", out var domain));
        Assert.IsNull(domain);
        Assert.AreEqual("https://www.amazon.com/sendtokindle/email", AmazonStore.FindAddressUrl(domain));
        Assert.AreEqual("https://www.amazon.co.uk/hz/mycd/myx", AmazonStore.ManageContentUrl("amazon.co.uk"));
    }

    [TestMethod]
    [DataRow("alice", "@alice:example.org")]
    [DataRow(" Alice ", "@alice:example.org")]
    [DataRow("@alice", "@alice:example.org")]
    [DataRow("first.last_2", "@first.last_2:example.org")]
    [DataRow("@Bob:other.example", "@Bob:other.example")]
    [DataRow("bob:other.example:8448", "@bob:other.example:8448")]
    public void AUsernameGetsTheServerAddedAndAFullIdIsKept(string input, string expected)
    {
        Assert.IsTrue(MatrixUserIds.TryResolve(input, "example.org", out var id, out var error), error);
        Assert.AreEqual(expected, id);
    }

    [TestMethod]
    [DataRow("", "Enter your username.")]
    [DataRow("alice smith", "cannot contain spaces")]
    [DataRow("alice@example.com", "not your email address")]
    [DataRow("alice!", "can only use")]
    [DataRow("@alice:", "not a complete ID")]
    [DataRow("@:example.org", "not a complete ID")]
    [DataRow("@a@b:example.org", "not a complete ID")]
    public void AnUnusableUsernameSaysWhy(string input, string expectedText)
    {
        Assert.IsFalse(MatrixUserIds.TryResolve(input, "example.org", out _, out var error));
        StringAssert.Contains(error, expectedText);
    }

    [TestMethod]
    [DataRow("example.org")]
    [DataRow("matrix.example.test:8448")]
    [DataRow("localhost")]
    [DataRow("10.0.0.5:1")]
    [DataRow("[::1]")]
    [DataRow("[2001:db8::1]:65535")]
    public void ARealServerNameIsAccepted(string serverName) =>
        Assert.IsTrue(MatrixUserIds.IsValidServerName(serverName), serverName);

    [TestMethod]
    [DataRow("")]
    [DataRow("..")]
    [DataRow("-")]
    [DataRow("a..b")]
    [DataRow(".example.org")]
    [DataRow("example.org.")]
    [DataRow("-a.example.org")]
    [DataRow("a-.example.org")]
    [DataRow("exa_mple.org")]
    [DataRow("example.org:0")]
    [DataRow("example.org:99999")]
    [DataRow("example.org:")]
    [DataRow("example.org:80:80")]
    [DataRow("999.1.1.1")]
    [DataRow("1.2.3")]
    [DataRow("[:::]")]
    [DataRow("[gg::1]")]
    [DataRow("[::1]junk")]
    [DataRow("[10.0.0.1]")]
    [DataRow("exam\u00e9ple.org")]
    public void AnUnusableServerNameIsRefusedEverywhereItIsTyped(string serverName)
    {
        Assert.IsFalse(MatrixUserIds.IsValidServerName(serverName), serverName);
        Assert.IsFalse(MatrixUserIds.TryResolve($"@alice:{serverName}", null, out _, out _), serverName);
        if (serverName.Length > 0)
        {
            Assert.IsNotNull(new SetupGuidanceSettings(Now).TrySetMessaging(serverName, null, null, null, null, Now));
            Assert.IsFalse(MatrixUserIds.TryResolve("alice", serverName, out _, out var error), serverName);
            StringAssert.Contains(error, "server name");
        }
    }

    [TestMethod]
    [DataRow("a!:bad/server")]
    [DataRow("@caf\u00e9:example.org")]
    [DataRow("@a:exa mple.org")]
    public void AMalformedFullIdIsRefused(string input) =>
        Assert.IsFalse(MatrixUserIds.TryResolve(input, "example.org", out _, out _), input);

    [TestMethod]
    public void TheLengthLimitAppliesToTheFinishedId()
    {
        // "@" + name + ":example.org" is 13 characters longer than the name.
        var longest = new string('a', 255 - 13);
        Assert.IsTrue(MatrixUserIds.TryResolve(longest, "example.org", out var id, out _));
        Assert.AreEqual(255, id.Length);

        Assert.IsFalse(MatrixUserIds.TryResolve(longest + "a", "example.org", out var none, out var error));
        Assert.AreEqual(string.Empty, none);
        StringAssert.Contains(error, "too long");
        Assert.IsFalse(MatrixUserIds.TryResolve(new string('a', 250), "example.org", out _, out _));
        Assert.IsFalse(MatrixUserIds.TryResolve("@" + longest + ":example.org" + "a", "example.org", out _, out _));
    }

    [TestMethod]
    public void WithoutAKnownServerOnlyAFullIdWorks()
    {
        Assert.IsFalse(MatrixUserIds.TryResolve("alice", null, out _, out var error));
        StringAssert.Contains(error, "full ID");
        Assert.IsTrue(MatrixUserIds.TryResolve("@alice:example.org", null, out _, out _));
    }

    [TestMethod]
    public void OnlyAnAccountOnTheHomeServerIsShortenedToItsUsername()
    {
        Assert.AreEqual("alice", MatrixUserIds.DisplayName("@alice:Example.org", "example.org"));
        Assert.AreEqual("@alice:other.example", MatrixUserIds.DisplayName("@alice:other.example", "example.org"));
        Assert.AreEqual("@alice:example.org", MatrixUserIds.DisplayName("@alice:example.org", null));
        Assert.AreEqual("example.org", MatrixUserIds.ServerNameOf("@bot:example.org"));
        Assert.IsNull(MatrixUserIds.ServerNameOf("bot"));
    }

    [TestMethod]
    [DataRow("not-an-address", null)]
    [DataRow("Library <library@example.org>", null)]
    [DataRow(null, "example.net")]
    public void InvalidKindleGuidanceChangesNothing(string? sender, string? store)
    {
        var settings = new SetupGuidanceSettings(Now);

        Assert.IsNotNull(settings.TrySetKindle(sender, store, null, Now.AddHours(1)));

        Assert.IsNull(settings.KindleSenderAddress);
        Assert.IsNull(settings.AmazonStoreDomain);
        Assert.AreEqual(Now, settings.UpdatedAtUtc);
    }

    [TestMethod]
    [DataRow("https://example.org", null, null, null)]
    [DataRow("example.org/path", null, null, null)]
    [DataRow(null, null, "javascript:alert(1)", null)]
    [DataRow(null, null, "example.org/help", null)]
    [DataRow(null, null, null, "https://user:secret@example.org/")]
    public void InvalidMessagingGuidanceChangesNothing(string? server, string? instructions, string? help, string? webApp)
    {
        var settings = new SetupGuidanceSettings(Now);

        Assert.IsNotNull(settings.TrySetMessaging(server, instructions, help, webApp, null, Now.AddHours(1)));

        Assert.AreEqual(Now, settings.UpdatedAtUtc);
    }

    [TestMethod]
    public void MessagingGuidanceIsTrimmedAndLengthLimited()
    {
        var settings = new SetupGuidanceSettings(Now);

        Assert.IsNull(settings.TrySetMessaging(
            " example.org:8448 ", " Line one\r\nLine two ", "https://help.example.org/chat", "  ", null, Now));
        Assert.AreEqual("example.org:8448", settings.MessagingServerName);
        Assert.AreEqual("Line one\nLine two", settings.MessagingSignInInstructions);
        Assert.IsNull(settings.MessagingWebAppUrl);
        Assert.IsTrue(settings.ShowElementHelp);

        var tooLong = new string('a', SetupGuidanceSettings.MaxInstructionsLength + 1);
        Assert.IsNotNull(settings.TrySetMessaging(null, tooLong, null, null, null, Now));
        Assert.AreEqual("example.org:8448", settings.MessagingServerName);
    }
}
