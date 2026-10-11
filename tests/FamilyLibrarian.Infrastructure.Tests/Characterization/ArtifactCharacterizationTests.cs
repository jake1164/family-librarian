using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Security;
using static FamilyLibrarian.Infrastructure.Tests.Characterization.CharacterizationFixtures;

namespace FamilyLibrarian.Infrastructure.Tests.Characterization;

/// <summary>
/// M1-0: pins what the CURRENT downloaded-artifact checks conclude. The
/// "Observed" column is today's behavior; "Desired" marks a KnownGap that a later
/// M1 slice must close. Identity verification and structural validation are
/// recorded separately on purpose: audiobook technical evidence (duration, tracks,
/// chapters) must stay distinct from verified book identity (alpha.6 owner
/// requirement 2), so no case here mixes the two outcomes.
/// </summary>
[TestClass]
public sealed class ArtifactCharacterizationTests
{
    private static readonly EpubAssetIdentityVerifier EpubIdentity =
        new(new StubWorkLookup(Title, Author), new DeterministicBookMatcher(), new NoRequestStore());
    private static readonly M4bAssetIdentityVerifier M4bIdentity =
        new(new StubWorkLookup(Title, Author), new DeterministicBookMatcher());

    private static string Identity(AssetIdentityVerificationResult r) =>
        r.IsMatch ? "Match" : "Unmatched";

    private static async Task<string> EpubIdentityOf(MemoryStream epub) =>
        Identity(await EpubIdentity.VerifyAsync(Asset(RequestMediaType.Ebook, ".epub"), epub, CancellationToken.None));

    private static async Task<string> EpubStructureOf(MemoryStream epub)
    {
        var outcome = await new EpubValidator().ValidateAsync(Asset(RequestMediaType.Ebook, ".epub"), epub, CancellationToken.None);
        return outcome.IsValid ? "Valid" : "Invalid";
    }

    private static async Task<string> M4bIdentityOf(MemoryStream m4b) =>
        Identity(await M4bIdentity.VerifyAsync(Asset(RequestMediaType.Audiobook, ".m4b"), m4b, CancellationToken.None));

    private static async Task<string> M4bStructureOf(MemoryStream m4b)
    {
        var outcome = await new AudioValidator().ValidateAsync(Asset(RequestMediaType.Audiobook, ".m4b"), m4b, CancellationToken.None);
        return outcome.IsValid ? "Valid" : "Invalid";
    }

    internal static IReadOnlyList<(CharacterizationCase Case, Func<Task<string>> Run)> Cases { get; } =
    [
        (new("E01", "Complete EPUB: 135 chapters, 136 TOC entries (front matter + chapters)", "identity=Match; structure=Valid"),
            async () => { using var e = Epub(Title, Author, "en", 135, 136); return $"identity={await EpubIdentityOf(e)}; structure={await EpubStructureOf(e)}"; }),
        (new("E02", "Three-chapter free sample with the right title/author", "identity=Match; structure=Valid", "identity=Unmatched (held for identity review: sample cue / very few chapters)"),
            async () => { using var e = Epub(Title, Author, "en", 3, 3); return $"identity={await EpubIdentityOf(e)}; structure={await EpubStructureOf(e)}"; }),
        (new("E03", "Right title/author, empty spine (no readable content): M1-3a rejects it structurally", "identity=Match; structure=Invalid"),
            async () => { using var e = Epub(Title, Author, "en", 0, 0); return $"identity={await EpubIdentityOf(e)}; structure={await EpubStructureOf(e)}"; }),
        (new("E04", "Omnibus: collection title that contains the requested title", "identity=Unmatched"),
            async () => { using var e = Epub("The Ember Cycle Omnibus: The Lantern Road and Two Others", Author, "en", 90, 91); return $"identity={await EpubIdentityOf(e)}"; }),
        (new("E05", "Wrong language declared in the package", "identity=Unmatched"),
            async () => { using var e = Epub(Title, Author, "de", 40, 41); return $"identity={await EpubIdentityOf(e)}"; }),
        (new("E06", "Language not declared: unknown is not a contradiction", "identity=Match"),
            async () => { using var e = Epub(Title, Author, null, 40, 41); return $"identity={await EpubIdentityOf(e)}"; }),
        (new("E07", "Author collision in the package", "identity=Unmatched"),
            async () => { using var e = Epub(Title, "Mara Quinn", "en", 40, 41); return $"identity={await EpubIdentityOf(e)}"; }),
        (new("E08", "Series neighbor title by the same author", "identity=Unmatched"),
            async () => { using var e = Epub("The Ash Gate", Author, "en", 40, 41); return $"identity={await EpubIdentityOf(e)}"; }),
        (new("A01", "M4B with matching title/author tags", "identity=Match"),
            async () => { using var m = M4b(("nam", Title), ("ART", Author)); return $"identity={await M4bIdentityOf(m)}"; }),
        (new("A02", "M4B matching tags whose audio track declares a non-requested language (M1-4a holds it)", "identity=Unmatched"),
            async () => { using var m = M4bWithTracks([Track(mdhdLanguage: "deu")], ("nam", Title), ("ART", Author)); return $"identity={await M4bIdentityOf(m)}"; }),
        (new("A02b", "M4B matching tags with only a non-standard language tag: unknown, not a mismatch", "identity=Match"),
            async () => { using var m = M4b(("nam", Title), ("ART", Author), ("lng", "German")); return $"identity={await M4bIdentityOf(m)}"; }),
        (new("A03", "M4B title tag is a series neighbor", "identity=Unmatched"),
            async () => { using var m = M4b(("nam", "The Ash Gate"), ("ART", Author)); return $"identity={await M4bIdentityOf(m)}"; }),
        (new("A04", "M4B with no title/author tags", "identity=Unmatched"),
            async () => { using var m = M4b(); return $"identity={await M4bIdentityOf(m)}"; }),
        (new("A05", "M4B structure for a container with 4 KiB of media and no duration/chapter evidence", "structure=Valid"),
            async () => { using var m = M4b(("nam", Title), ("ART", Author)); return $"structure={await M4bStructureOf(m)}"; }),
        (new("A06", "Format coverage: which formats have an identity verifier at all", ".epub=verifier, .kepub=verifier, .m4b=verifier, .mp3=none, .ogg=none, .mobi=none, .azw3=none, .fb2=none", ".mp3 and .ogg identity-checked, or held unless the match was pre-confirmed (M1-4)"),
            () => Task.FromResult(string.Join(", ", new[] { ".epub", ".kepub", ".m4b", ".mp3", ".ogg", ".mobi", ".azw3", ".fb2" }.Select(format =>
                $"{format}={(EpubIdentity.Supports(Asset(RequestMediaType.Ebook, format)) || M4bIdentity.Supports(Asset(RequestMediaType.Audiobook, format)) ? "verifier" : "none")}")))),
    ];

    [TestMethod]
    public async Task CurrentArtifactVerdictsMatchTheBaseline()
    {
        var failures = new List<string>();
        foreach (var (fixture, run) in Cases)
        {
            var actual = await run();
            if (actual != fixture.Observed) failures.Add($"{fixture.Id} [{fixture.Description}]: observed \"{actual}\"");
        }

        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }
}
