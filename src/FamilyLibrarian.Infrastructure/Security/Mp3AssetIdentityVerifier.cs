using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>
/// Reads an MP3's ID3 tags and compares the book's album, author and language with the Work being fulfilled, so an external
/// MP3 can clear on its content instead of always being held as attested-only.
/// </summary>
/// <remarks>
/// <para>Conservative in the same way as <see cref="M4bAssetIdentityVerifier"/>, with one deliberate difference: a file that
/// cannot be judged (no usable tags, no author to compare, or tags written in a language other than the one accepted but
/// not declared) returns exactly what an unverifiable format returns (<see cref="NoVerifierOutcome"/>), so a built-in or
/// manual source keeps its attested outcome and an external source stays held. Only a positive title and author match
/// clears; only an explicit contradiction (a different album, a different author, a declared or cue-word language that
/// conflicts) is an unmatched hold.</para>
/// <para>A track-level title (<c>TIT2</c>) is never used against the book: "Chapter 01" is normal. Descriptive text
/// (comment, genre, album) written in another language is evidence that prevents clearing but creates no new hold.</para>
/// </remarks>
public sealed class Mp3AssetIdentityVerifier(
    IWorkLookup works, IBookMatcher bookMatcher, IBookRequestFulfillmentStore? requestFulfillment = null) : IAssetIdentityVerifier
{
    // ISO 639-2 values that explicitly say "no usable language": unknown, never a mismatch.
    private static readonly string[] UnspecifiedLanguages = ["und", "zxx", "mis", "mul"];

    public string Id => "mp3-id3-tags";

    public bool Supports(MediaAsset asset) =>
        string.Equals(asset.Format, ".mp3", StringComparison.OrdinalIgnoreCase);

    public async Task<AssetIdentityVerificationResult> VerifyAsync(
        MediaAsset asset, Stream content, CancellationToken cancellationToken)
    {
        var expected = await works.FindAsync(asset.WorkId, cancellationToken);
        if (expected is null || string.IsNullOrWhiteSpace(expected.Title))
        {
            // Nothing to compare against: behave exactly as before this verifier existed.
            return NoVerifierOutcome.For(asset);
        }

        var tags = await Id3TagReader.ReadAsync(content, cancellationToken);
        var authors = tags?.AuthorCandidates.ToArray() ?? [];
        var albumTitle = tags?.Album;
        var trackTitle = albumTitle is null ? tags?.Title : null;
        if (tags is null || (albumTitle is null && trackTitle is null && authors.Length == 0))
        {
            return WithFacts(NoVerifierOutcome.For(asset),
                [new IdentityFact("ev-tags", EvidenceDimension.IdentityCorroboration,
                    EvidenceReasonCodes.ArtifactIdentityCorroboration.TagsAbsent, EvidencePolarity.Unknown)]);
        }

        var facts = new List<IdentityFact>
        {
            new("ev-tag-source", EvidenceDimension.IdentityCorroboration,
                EvidenceReasonCodes.ArtifactIdentityCorroboration.TagSource, EvidencePolarity.Unknown, tags.Source)
        };

        var expectedTitles = new[] { expected.Title }
            .Concat(expected.AlternateTitles ?? [])
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Title: the album names the book; a track title alone cannot contradict or confirm it.
        var titleMatches = albumTitle is not null &&
            expectedTitles.Any(expectedTitle => bookMatcher.TitleMatches(expectedTitle, albumTitle));
        if (albumTitle is not null)
        {
            facts.Add(IdentityFacts.Title(declared: true, agrees: titleMatches));
        }
        else
        {
            facts.Add(new IdentityFact("ev-title", EvidenceDimension.IdentityCorroboration,
                trackTitle is null
                    ? EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleAbsent
                    : EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleTrackOnly,
                EvidencePolarity.Unknown));
        }

        var authorChecked = authors.Length > 0 && !string.IsNullOrWhiteSpace(expected.PrimaryAuthor);
        var authorMatches = authorChecked && authors.Any(author => bookMatcher.AuthorMatches(expected.PrimaryAuthor, author));
        facts.Add(IdentityFacts.Author(authorChecked, authorMatches));

        // Language. A declared tag always wins; otherwise a cue word; otherwise the language of the descriptive text.
        var acceptedLanguage = await FindAcceptedLanguageAsync(asset.AssociatedRequestFormatId, cancellationToken);
        var declared = tags.Languages
            .Select(language => language.Trim())
            .Where(language => language.Length > 0 && !UnspecifiedLanguages.Contains(language, StringComparer.OrdinalIgnoreCase) &&
                LanguageAcceptance.IsRecognized(language))
            .ToArray();
        var languageConflict = false;
        var inferredConflict = false;
        if (declared.Length > 0)
        {
            var accepted = declared.Any(language => LanguageAcceptance.IsAcceptedOrUnspecified(language, acceptedLanguage));
            facts.Add(IdentityFacts.Language(declared, accepted));
            languageConflict = !accepted;
        }
        else if (FindCue(tags, albumTitle, trackTitle, asset.OriginalFilename) is { } cue)
        {
            var accepted = LanguageAcceptance.IsAcceptedOrUnspecified(cue.Language, acceptedLanguage);
            if (!accepted)
            {
                facts.Add(new IdentityFact("ev-language", EvidenceDimension.Language,
                    EvidenceReasonCodes.ArtifactLanguage.CueConflicts, EvidencePolarity.Negative, $"cue:{cue.Phrase}"));
                languageConflict = true;
            }
        }
        else
        {
            var descriptive = string.Join(' ', new[] { tags.Comment, tags.Genre, albumTitle, trackTitle }.Where(text => text is not null));
            if (LanguageFunctionWords.TryInfer(descriptive, out var inferred, out _))
            {
                var accepted = LanguageAcceptance.IsAcceptedOrUnspecified(inferred, acceptedLanguage);
                facts.Add(new IdentityFact("ev-language-inferred", EvidenceDimension.Language,
                    accepted
                        ? EvidenceReasonCodes.ArtifactLanguage.InferredMatchesRequest
                        : EvidenceReasonCodes.ArtifactLanguage.InferredConflicts,
                    accepted ? EvidencePolarity.Positive : EvidencePolarity.Negative,
                    $"inferred:{inferred}"));
                inferredConflict = !accepted;
            }
        }

        // An explicit contradiction is an unmatched hold with its own cause.
        var titleContradicted = albumTitle is not null && !titleMatches;
        var authorContradicted = authorChecked && !authorMatches;
        if (titleContradicted || authorContradicted || languageConflict)
        {
            return AssetIdentityVerificationResult.Unmatched(
                Id,
                Describe(expectedTitles, expected.PrimaryAuthor, albumTitle, authors, declared, acceptedLanguage,
                    titleContradicted, authorContradicted, languageConflict),
                facts);
        }

        // Positive title and author evidence is the only thing that clears; text written in another language prevents it.
        if (titleMatches && authorMatches && !inferredConflict)
        {
            return AssetIdentityVerificationResult.Match(Id, facts);
        }

        return WithFacts(NoVerifierOutcome.For(asset), facts);
    }

    private static AssetIdentityVerificationResult WithFacts(
        AssetIdentityVerificationResult result, IReadOnlyList<IdentityFact> facts) =>
        result with { Facts = facts };

    private static (string Language, string Phrase)? FindCue(
        Id3Tags tags, string? album, string? trackTitle, string fileName)
    {
        foreach (var text in new[] { album, trackTitle, tags.Title, fileName })
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var tokens = new string(text.ToUpperInvariant().Select(static character => char.IsLetter(character) ? character : ' ').ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (LanguageCueWords.TryFind(tokens, new bool[tokens.Length], out var language, out var start, out var length))
            {
                return (language, string.Join(' ', tokens.Skip(start).Take(length)));
            }
        }

        return null;
    }

    private async Task<string?> FindAcceptedLanguageAsync(Guid requestFormatId, CancellationToken cancellationToken)
    {
        if (requestFulfillment is null) return null;
        var request = await requestFulfillment.FindByFormatIdAsync(requestFormatId, cancellationToken);
        return request?.Formats.SingleOrDefault(format => format.Id == requestFormatId)?.AcceptedLanguage;
    }

    private static string Describe(
        IReadOnlyList<string> expectedTitles, string? expectedAuthor, string? albumTitle, IReadOnlyList<string> authors,
        string[] declaredLanguages, string? acceptedLanguage,
        bool titleContradicted, bool authorContradicted, bool languageConflict)
    {
        var parts = new List<string>();
        if (titleContradicted)
        {
            parts.Add($"the file's album is \"{Bounded(albumTitle)}\" but the catalog expects one of \"{string.Join("\" / \"", expectedTitles)}\"");
        }

        if (authorContradicted)
        {
            parts.Add($"the file's artist tags name \"{Bounded(string.Join("\" / \"", authors))}\" but the catalog expects \"{expectedAuthor}\"");
        }

        var reason = parts.Count == 0 ? string.Empty : $"Embedded audio tags didn't match the catalog entry: {string.Join("; ", parts)}.";
        if (languageConflict)
        {
            var language = declaredLanguages.Length > 0
                ? $"The audiobook's declared language ({Bounded(string.Join(", ", declaredLanguages))}) doesn't match"
                : "The audiobook's album, title or file name is worded in another language, which doesn't match";
            var languageReason = $"{language} the language accepted for this request" +
                $"{(string.IsNullOrWhiteSpace(acceptedLanguage) ? string.Empty : $" ({acceptedLanguage})")}.";
            reason = reason.Length == 0 ? languageReason : $"{reason} {languageReason}";
        }

        return reason;
    }

    private static string Bounded(string? value) =>
        ArtifactEvidence.Bound(value, 200) ?? string.Empty;
}
