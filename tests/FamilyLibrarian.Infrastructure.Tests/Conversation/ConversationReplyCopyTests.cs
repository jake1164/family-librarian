using FamilyLibrarian.Application.Catalog.Answers;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Infrastructure.Tests.Conversation;

/// <summary>
/// The wording of two shared replies found in deployed acceptance: a lone series choice must not claim several matched, and a
/// title's date still to come with no source status must not read as already published. Both transports render the same reply.
/// </summary>
[TestClass]
public sealed class ConversationReplyCopyTests
{
    private static readonly DateOnly Today = new(2026, 11, 19);

    private static readonly int[] Transports = [ConversationAnswerText.WebMaxLength, ConversationAnswerText.MatrixMaxLength];

    private static CatalogAnswerChoice SeriesChoice(string label, string id) =>
        new(CatalogSubjectRef.FromSource(CatalogSubjectKind.Series, label, new SourceRef("fiction", id, ExternalReferenceEntityType.Series)),
            null, null, "fiction", null);

    private static CatalogAnswer Clarification(params CatalogAnswerChoice[] choices) => new()
    {
        Operation = CatalogAnswerOperation.Position,
        Outcome = CatalogAnswerOutcome.Clarification,
        Choices = choices,
        Reasons = [CatalogAnswerCodes.SeriesChoices],
        Today = Today
    };

    private static IEnumerable<string> BothTransports(CatalogAnswer answer) =>
        Transports.Select(max => ConversationAnswerText.ForAnswer(answer, max).Text)
            .Select(text => MatrixReplyFormatter.ToText(ConversationReply.Of(ConversationReplyKind.Answer, text)));

    [TestMethod]
    public void OneOfferedSeriesIsConfirmedWithoutClaimingSeveralMatched()
    {
        foreach (var text in BothTransports(Clarification(SeriesChoice("The Lantern Road", "s1"))))
        {
            StringAssert.Contains(text, "1. The Lantern Road");
            StringAssert.Contains(text, "One series matches that name, so I need you to confirm it.");
            Assert.DoesNotContain("More than one series", text);
        }
    }

    [TestMethod]
    public void SeveralOfferedSeriesStillSayMoreThanOneMatched()
    {
        foreach (var text in BothTransports(Clarification(SeriesChoice("The Lantern Road", "s1"), SeriesChoice("The Lantern Road (Omnibus)", "s2"))))
        {
            StringAssert.Contains(text, "1. The Lantern Road");
            StringAssert.Contains(text, "2. The Lantern Road (Omnibus)");
            StringAssert.Contains(text, "More than one series matches that name.");
        }
    }

    private static CatalogAnswerEntry Midnight(DateOnly date, PublicationStatus status) => new()
    {
        Source = new SourceRef("fiction", "w4", ExternalReferenceEntityType.Work),
        ProviderId = "fiction",
        Title = "Lanterns at Midnight",
        Publication = PublicationEvidence.Dated(date, DatePrecision.Year, DateBasis.Original, status)
    };

    private static CatalogAnswer TitleAnswer(CatalogAnswerEntry entry)
    {
        var selection = CatalogAnswerPolicy.PublicationForTitle([entry], "Lanterns at Midnight", Today, new CatalogPolicyScope(true, false, false));
        return new CatalogAnswer
        {
            Operation = CatalogAnswerOperation.PublicationForTitle,
            Outcome = selection.Outcome,
            Subject = CatalogSubjectRef.Title("Lanterns at Midnight"),
            Entries = selection.Entries,
            Qualifiers = selection.Qualifiers,
            Reasons = selection.Reasons,
            Today = Today
        };
    }

    [TestMethod]
    public void AFutureYearWithNoSourceStatusIsAnAttributedDateNotAPublication()
    {
        var answer = TitleAnswer(Midnight(new DateOnly(2027, 1, 1), PublicationStatus.Unknown));

        Assert.AreEqual(CatalogAnswerOutcome.Qualified, answer.Outcome);
        Assert.IsTrue(answer.HasQualifier(CatalogAnswerCodes.ReleaseDateAttributed));
        Assert.IsTrue(answer.HasQualifier(CatalogAnswerCodes.StatusUnverified));
        foreach (var text in BothTransports(answer))
        {
            StringAssert.StartsWith(text, "What the sources say about its publication date:");
            Assert.DoesNotContain("was published", text);
            StringAssert.Contains(text, "2027 (year only), original publication");
            StringAssert.Contains(text, "That date is what a source attributes to it, not a confirmed release.");
            StringAssert.Contains(text, "The source did not state whether it is published.");
        }
    }

    [TestMethod]
    public void APastYearOrASourceStatedFutureDateIsNotMarkedAsAnAttributedRelease()
    {
        var past = TitleAnswer(Midnight(new DateOnly(2012, 1, 1), PublicationStatus.Unknown));
        var forthcoming = TitleAnswer(Midnight(new DateOnly(2027, 1, 1), PublicationStatus.Forthcoming));

        Assert.IsFalse(past.HasQualifier(CatalogAnswerCodes.ReleaseDateAttributed));
        Assert.IsFalse(forthcoming.HasQualifier(CatalogAnswerCodes.ReleaseDateAttributed));
        Assert.IsFalse(forthcoming.HasQualifier(CatalogAnswerCodes.StatusUnverified));
        StringAssert.StartsWith(ConversationAnswerText.ForAnswer(past, ConversationAnswerText.WebMaxLength).Text, "What the sources say about its publication date:");
    }

    // ---- A7-C1: "announced" only with evidence of an announcement; a future date is not a confirmed release ----------------

    private static CatalogAnswerEntry LatestRecord(string id, string title, PublicationEvidence publication) => new()
    {
        Source = new SourceRef("fiction", id, ExternalReferenceEntityType.Work),
        ProviderId = "fiction",
        Title = title,
        Publication = publication
    };

    private static readonly string[] LatestGroupOrder = ["future-dated", "undated", "announced-undated"];

    private static readonly SourceRef LanternSeries = new("fiction", "s-lantern", ExternalReferenceEntityType.Series);

    /// <summary>The production latest policy over one record of each kind, so the groups are the ones the policy really makes.</summary>
    private static CatalogAnswer LatestOfFourKinds()
    {
        var selection = CatalogAnswerPolicy.Latest(
            [
                LatestRecord("w1", "Published Work",
                    PublicationEvidence.Dated(new DateOnly(2020, 3, 5), DatePrecision.Day, DateBasis.Original, PublicationStatus.Published)),
                LatestRecord("w2", "Unknown Date Work", PublicationEvidence.NotStated),
                LatestRecord("w3", "Announced Work", PublicationEvidence.KnownUndated()),
                LatestRecord("w4", "Future Dated Work",
                    PublicationEvidence.Dated(new DateOnly(2028, 1, 1), DatePrecision.Year, DateBasis.Original, PublicationStatus.Unknown))
            ],
            Today,
            new CatalogPolicyScope(true, false, false));
        return new CatalogAnswer
        {
            Operation = CatalogAnswerOperation.LatestPublished,
            Outcome = selection.Outcome,
            Subject = CatalogSubjectRef.FromSource(CatalogSubjectKind.Series, "The Lantern Road", LanternSeries),
            Entries = selection.Entries,
            Groups = selection.Groups,
            Qualifiers = selection.Qualifiers,
            Reasons = selection.Reasons,
            Today = Today
        };
    }

    private static IReadOnlyList<string> Facts(ConversationAnswerCardGroup group) => group.Lines.Single().Facts;

    [TestMethod]
    public void LatestKeepsThePublishedWorkRankedAndSplitsTheOtherThreeKindsApart()
    {
        var answer = LatestOfFourKinds();

        Assert.AreEqual("Published Work", answer.Entries.Single().Title, "Ranking is unchanged: only the dated, past Work is the latest.");
        CollectionAssert.AreEqual(LatestGroupOrder, answer.Groups.Select(group => group.Code).ToArray());
        Assert.AreEqual("Unknown Date Work", answer.Groups.Single(group => group.Code == "undated").Entries.Single().Title);
        Assert.AreEqual("Announced Work", answer.Groups.Single(group => group.Code == "announced-undated").Entries.Single().Title);
        Assert.IsTrue(answer.HasQualifier(CatalogAnswerCodes.UndatedEntriesPresent), "The undated qualification still applies.");
    }

    [TestMethod]
    public void TheWebCardHeadsOnlyAnAnnouncedRecordAsAnnouncedAndNeverImpliesAConfirmedRelease()
    {
        foreach (var max in Transports)
        {
            var card = ConversationAnswerText.ForAnswer(LatestOfFourKinds(), max).Cards.Single();

            Assert.AreEqual("1. Published Work", card.Lines.Single().Title);
            CollectionAssert.Contains(card.Lines.Single().Facts.ToArray(), "2020-03-05, original publication");

            Assert.AreEqual(3, card.Groups.Count);
            var future = card.Groups[0];
            var unknown = card.Groups[1];
            var announced = card.Groups[2];

            Assert.AreEqual("Listed with a date still to come (not counted as published):", future.Heading);
            Assert.AreEqual("No publication date stated:", unknown.Heading);
            Assert.AreEqual("Announced by a source, with no date stated:", announced.Heading);

            StringAssert.Contains(future.Lines.Single().Title, "Future Dated Work");
            CollectionAssert.Contains(Facts(future).ToArray(), "2028 (year only), original publication", "The date precision is kept.");
            StringAssert.Contains(unknown.Lines.Single().Title, "Unknown Date Work");
            CollectionAssert.Contains(Facts(unknown).ToArray(), "no date stated");
            StringAssert.Contains(announced.Lines.Single().Title, "Announced Work");
            CollectionAssert.Contains(Facts(announced).ToArray(), "announced, no date stated");

            Assert.DoesNotContain("Announced", unknown.Heading);
            Assert.DoesNotContain("Announced", future.Heading);
            Assert.DoesNotContain("confirmed", future.Heading);
            Assert.DoesNotContain("scheduled", future.Heading);
            CollectionAssert.Contains(card.Notes.ToArray(), "Entries with no stated date are listed apart.", "The qualification is unchanged.");
        }
    }

    [TestMethod]
    public void MatrixTextPutsEachRecordUnderItsOwnHeadingInOrder()
    {
        foreach (var max in Transports)
        {
            var rendered = ConversationAnswerText.ForAnswer(LatestOfFourKinds(), max).Text;
            var text = MatrixReplyFormatter.ToText(ConversationReply.Of(ConversationReplyKind.Answer, rendered));

            var future = text.IndexOf("Listed with a date still to come (not counted as published):", StringComparison.Ordinal);
            var futureBook = text.IndexOf("Future Dated Work", StringComparison.Ordinal);
            var unknown = text.IndexOf("No publication date stated:", StringComparison.Ordinal);
            var unknownBook = text.IndexOf("Unknown Date Work", StringComparison.Ordinal);
            var announced = text.IndexOf("Announced by a source, with no date stated:", StringComparison.Ordinal);
            var announcedBook = text.IndexOf("Announced Work", StringComparison.Ordinal);

            Assert.IsTrue(future >= 0 && future < futureBook && futureBook < unknown, text);
            Assert.IsTrue(unknown < unknownBook && unknownBook < announced, text);
            Assert.IsTrue(announced < announcedBook, text);
            StringAssert.Contains(text, "2020-03-05, original publication");
            StringAssert.Contains(text, "2028 (year only), original publication");
            StringAssert.Contains(text, "Entries with no stated date are listed apart.");
            Assert.AreEqual(1, CountOf(text, "Announced by a source"), "Only the evidenced record is called announced under a heading.");
            Assert.AreEqual(1, CountOf(text, "announced, no date stated"), "Only the evidenced record carries the announced fact.");
        }
    }

    private static int CountOf(string haystack, string needle) =>
        (haystack.Length - haystack.Replace(needle, string.Empty, StringComparison.Ordinal).Length) / needle.Length;

    private const string Origin = "https://fl.example.test";
    private static readonly Guid AuthorId = Guid.Parse("a1111111-1111-4111-8111-111111111111");
    private static readonly Guid WorkId = Guid.Parse("b2222222-2222-4222-8222-222222222222");
    private static readonly Guid SeriesId = Guid.Parse("c3333333-3333-4333-8333-333333333333");

    private static CatalogAnswerEntry SavedWork() => new()
    {
        SavedWorkId = WorkId,
        Title = "Salt and Reed",
        Publication = PublicationEvidence.Dated(new DateOnly(2004, 1, 1), DatePrecision.Year, DateBasis.Original, PublicationStatus.Published)
    };

    private static CatalogAnswer SavedAnswer(CatalogAnswerOperation operation, CatalogSubjectRef subject) => new()
    {
        Operation = operation,
        Outcome = CatalogAnswerOutcome.Answered,
        Subject = subject,
        Entries = [SavedWork()],
        Today = Today
    };

    private static IEnumerable<ConversationAnswerCard> Cards(CatalogAnswer answer) =>
        [.. ConversationAnswerText.ForAnswer(answer, ConversationAnswerText.WebMaxLength).Cards,
         .. ConversationAnswerText.ForAnswer(answer, ConversationAnswerText.MatrixMaxLength).Cards];

    [TestMethod]
    public void ASavedAuthorsHeadingIsNeverALinkToASeriesOrWorkWithTheAuthorsId()
    {
        var answer = SavedAnswer(CatalogAnswerOperation.AuthorWorks, CatalogSubjectRef.Saved(CatalogSubjectKind.Author, "Nell Ashby", AuthorId));

        foreach (var card in Cards(answer))
        {
            Assert.AreEqual($"/authors/{AuthorId:D}", card.Href, "An author's id only ever becomes the author's own page.");
            Assert.AreEqual($"/works/{WorkId:D}", card.Lines.Single().Href);
        }
    }

    [TestMethod]
    public void ASavedAuthorAnswerInMatrixLinksToTheListedSavedWorkNotTheAuthor()
    {
        var answer = SavedAnswer(CatalogAnswerOperation.AuthorWorks, CatalogSubjectRef.Saved(CatalogSubjectKind.Author, "Nell Ashby", AuthorId));
        var rendered = ConversationAnswerText.ForAnswer(answer, ConversationAnswerText.MatrixMaxLength);
        var reply = new ConversationReply(ConversationReplyKind.Answer, rendered.Text, [], [], [], Answers: rendered.Cards);

        var text = MatrixReplyFormatter.ToText(reply, Origin);

        Assert.AreEqual($"Open in Family Librarian: {Origin}/works/{WorkId:D}", text.Split('\n')[^1]);
        Assert.DoesNotContain(AuthorId.ToString("D"), text);
        Assert.DoesNotContain("/series/", text);
    }

    [TestMethod]
    public void ASavedSeriesAndASavedBookKeepTheirOwnRoutes()
    {
        var series = SavedAnswer(CatalogAnswerOperation.SeriesMembers, CatalogSubjectRef.Saved(CatalogSubjectKind.Series, "Saltmarsh Chronicles", SeriesId));
        var book = SavedAnswer(CatalogAnswerOperation.PublicationForTitle, CatalogSubjectRef.Title("Salt and Reed"));

        foreach (var card in Cards(series)) Assert.AreEqual($"/series/{SeriesId:D}", card.Href);
        foreach (var card in Cards(book))
        {
            Assert.IsNull(card.Href, "A title has no page of its own.");
            Assert.AreEqual($"/works/{WorkId:D}", card.Lines.Single().Href);
        }
    }
}
