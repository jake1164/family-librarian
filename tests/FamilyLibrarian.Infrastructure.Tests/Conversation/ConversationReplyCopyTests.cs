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
