using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.ModelTuning;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Application.DecisionCapture;

/// <summary>
/// Maps what a production service already holds to the tuner's allow-listed input shapes (the one set of shapes shared with
/// the built-in corpus). Anything the shapes do not name is simply not copied: provider ids, links, tokens, revisions, file names
/// and hashes never leave the production objects. Candidates get local keys (c1, c2, ...); the provider ids go to the separate
/// link list so an outcome can later be attached to the right candidate without making them replay input.
/// </summary>
public static class DecisionSnapshotInputs
{
    public const int SchemaVersion = 1;
    private const string Isbn13 = "isbn13";

    public static TuningIdentity Identity(BookIdentity requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        return new TuningIdentity(
            requested.Title, requested.Author, [.. requested.Isbn13Candidates], [.. requested.Authors ?? []], [.. requested.Series ?? []],
            requested.Language, requested.PublicationYear, requested.Publisher, [.. requested.AlternateTitles ?? []]);
    }

    /// <summary>One option as the tuner shape, keyed locally. The provider ids are returned separately and are never part of the shape.</summary>
    public static (TuningOption Option, CapturedCandidateLink Link) Option(FulfillmentOption option, string localKey)
    {
        ArgumentNullException.ThrowIfNull(option);
        var candidate = Candidate(option.IdentityAssessment, localKey, option.Title ?? AllowedReleaseName(option.ReleaseName) ?? string.Empty,
            option.Author);
        var fulfillment = new TuningFulfillment(
            option.MediaType, option.Format, option.Language, option.Title, option.Author, option.PublicationYear, option.Publisher,
            option.SizeBytes, option.MatchBasis, option.HasPlausibleTitle,
            option.AuthorAffinity is { } affinity ? new TuningAffinity(affinity.Kind, affinity.Score) : null,
            option.RequiresLanguageConfirmation, option.RequiresReleaseConfirmation, ConcernKind(option.ReleaseConcern),
            option.RequiresNarrationConfirmation, option.NarrationKind ?? NarrationKind.Unknown,
            string.Equals(option.Quality, "retail", StringComparison.Ordinal), option.AcquisitionAssessment?.Suitability,
            option.AudiobookPart is { } part ? new TuningPart(part.Number, part.Total) : null, option.FragmentOnlyConcern);
        return (new TuningOption(candidate, fulfillment),
            new CapturedCandidateLink(localKey, option.ProviderId, option.ProviderResultId));
    }

    /// <summary>
    /// The candidate's own work, edition and release facts as the resolver read them. Only the isbn13 identifiers are kept. With no
    /// assessment there is only the option's own title and author, so a replay may not reproduce the original assessment (reported stale).
    /// </summary>
    internal static TuningCandidate Candidate(CandidateIdentityAssessment? assessment, string localKey, string fallbackTitle, string? fallbackAuthor)
    {
        var source = assessment?.SourceMetadata;
        var work = source is null
            ? new CandidateWork(fallbackTitle, string.IsNullOrWhiteSpace(fallbackAuthor) ? [] : [new BookAuthor(fallbackAuthor, null)], [])
            : new CandidateWork(source.Work.Title, [.. source.Work.Authors], [.. source.Work.Series]);
        var edition = source?.Edition is { } e
            ? new CandidateEdition(e.Language, e.PublicationYear, e.Publisher, [.. e.Identifiers.Where(identifier => identifier.Scheme == Isbn13)])
            : null;
        var release = source?.Release is { } r
            ? new CandidateRelease(AllowedReleaseName(r.Name), r.Format, r.SizeBytes, assessment!.ReleaseEvidence.AssertedLanguage, r.IsCollection, r.IsSample, r.IsAbridged, r.IsUnabridged)
            : null;
        return new TuningCandidate(localKey, work, edition, release);
    }

    public static ReleaseConcernKind ConcernKind(string? concern) => concern is null ? ReleaseConcernKind.None
        : string.Equals(concern, ExternalReleasePolicy.UnknownDrmConfirmationReason, StringComparison.Ordinal)
            ? ReleaseConcernKind.DrmUnconfirmed : ReleaseConcernKind.Other;

    private static readonly string[] FileExtensions = [".epub", ".mp3", ".m4b", ".pdf", ".ogg", ".kepub"];

    /// <summary>
    /// A release name is a bibliographic label, never a file name. Many real release names end in a file extension, which the
    /// tuner's allow-list refuses, so such a name is withheld (null) rather than stored or trimmed. The snapshot then cannot
    /// reproduce a prompt that used the name and is reported stale instead of claiming an exact reconstruction.
    /// </summary>
    internal static string? AllowedReleaseName(string? name) =>
        name is null || name.Length > 256 || name.Any(char.IsControl) || name.Contains("://", StringComparison.Ordinal) ||
        name.StartsWith('/') || name.Contains('\\', StringComparison.Ordinal) || name.Contains("../", StringComparison.Ordinal) ||
        name.Length == 64 && name.All(char.IsAsciiHexDigit) ||
        FileExtensions.Any(extension => name.Length > extension.Length && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            ? null : name;

    public static string LocalKey(int index) => "c" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Search guidance shows at most 15 results, so at most 15 are kept.</summary>
    public static (SearchCaseInput Input, IReadOnlyList<CapturedCandidateLink> Links) Search(
        BookIdentity requested, IReadOnlyList<FulfillmentOption> results, IReadOnlyCollection<string> alreadyTried)
    {
        var built = results.Take(15).Select((option, index) => Option(option, LocalKey(index))).ToArray();
        return (new SearchCaseInput(SchemaVersion, Identity(requested), [.. built.Select(item => item.Option)], [.. alreadyTried]),
            [.. built.Select(item => item.Link)]);
    }

    /// <summary>
    /// Triage shows the current pick first, then the rest, at most eight (see <c>CandidateTriageEvidence.Render</c>); the snapshot keeps
    /// exactly those, in that order, so the local keys are the very ids the model saw.
    /// </summary>
    public static (CandidateTriageCaseInput Input, IReadOnlyList<CapturedCandidateLink> Links) Triage(SemanticAssessment.CandidateTriageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var shown = (context.Pick is null ? context.Candidates : new[] { context.Pick }.Concat(context.Candidates))
            .DistinctBy(candidate => candidate.ProviderResultId, StringComparer.Ordinal).Take(8).ToArray();
        var built = shown.Select((option, index) => Option(option, LocalKey(index))).ToArray();
        string? KeyOf(string providerResultId) =>
            built.FirstOrDefault(item => item.Link.ProviderResultId == providerResultId).Link?.CandidateKey;
        var pickKey = context.Pick is null ? null : KeyOf(context.Pick.ProviderResultId);
        var tied = context.TiedResultIds.Select(KeyOf).OfType<string>().ToArray();
        return (new CandidateTriageCaseInput(SchemaVersion, Identity(context.Requested), [.. built.Select(item => item.Option)], pickKey, tied, context.Level),
            [.. built.Select(item => item.Link)]);
    }

    /// <summary>One unsettled candidate beside the request. The provider reference becomes the link, never part of the shape.</summary>
    public static (CandidateAssessmentInput Input, IReadOnlyList<CapturedCandidateLink> Links) CandidateAssessment(CandidateIdentityAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        var key = LocalKey(0);
        return (new CandidateAssessmentInput(SchemaVersion, Identity(assessment.RequestedMetadata), Candidate(assessment, key, assessment.SourceMetadata.Title, null)),
            [new CapturedCandidateLink(key, "unknown", assessment.SourceMetadata.ProviderReference)]);
    }

    /// <summary>
    /// A value read out of a file (an embedded title, a language tag) may be stored only as a short plain observation: 24 words or
    /// fewer and none of what the allow-list refuses. Anything else is withheld (null) and the snapshot is reported non-exact if the
    /// prompt used it, never trimmed to fit.
    /// </summary>
    internal static string? AllowedEvidenceValue(string? value) =>
        value is null || value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 24 ? null : AllowedReleaseName(value);

    /// <summary>
    /// The recorded evidence a file-assessment prompt can show, in the order it shows it (profile findings are never shown; the first
    /// 40 by record kind and finding id are). Values are kept only when the model was allowed to see them: a withheld value is not stored.
    /// </summary>
    public static ArtifactAssessmentInput ArtifactAssessment(
        MediaAsset asset, Publishing.WorkSummary? work, IReadOnlyList<ArtifactEvidence> evidence, bool includeDetails)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(evidence);
        var shown = evidence.Where(item => item.Dimension != EvidenceDimension.Profile)
            .OrderBy(item => item.RecordKind, StringComparer.Ordinal).ThenBy(item => item.FindingId, StringComparer.Ordinal)
            .Take(SemanticAssessment.ArtifactSemanticEvidence.MaxFindingsShown)
            .Select(item => new TuningEvidence(item.RecordKind, item.FindingId, item.Dimension, item.Code, item.Polarity,
                includeDetails ? AllowedEvidenceValue(item.Value) : null))
            .ToArray();
        return new ArtifactAssessmentInput(
            SchemaVersion, new TuningAsset(asset.MediaType, asset.Format, asset.Origin, asset.StorageState),
            new DeclaredBook(work?.Title ?? string.Empty, work?.PrimaryAuthor, work?.Language), shown, includeDetails);
    }

    /// <summary>
    /// The hold review's input: what the model was shown about a provider file, minus any book text. Only the fact codes derived from
    /// excerpts are kept (never their text) and the caller marks the decision as having involved excerpts, so a replay can say it was
    /// rebuilt without them. Values are kept only when the model was allowed to see them.
    /// </summary>
    public static ArtifactHoldInput ArtifactHold(
        MediaAsset asset, Security.AssetIdentityVerificationResult verification, Security.ExpectedArtifactProfile? expected,
        IReadOnlyList<ArtifactEvidence> recorded, bool includeDetails, IReadOnlyList<Security.ArtifactExcerpt> bookText)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(recorded);
        string? Value(string? value) => includeDetails ? AllowedEvidenceValue(value) : null;
        var facts = (verification.Facts ?? []).OrderBy(fact => fact.FindingId, StringComparer.Ordinal)
            .Select(fact => new TuningFact(fact.FindingId, fact.Dimension, fact.Code, fact.Polarity, Value(fact.Value))).ToArray();
        var rows = recorded.OrderBy(row => row.RecordKind, StringComparer.Ordinal).ThenBy(row => row.FindingId, StringComparer.Ordinal)
            .Select(row => new TuningEvidence(row.RecordKind, row.FindingId, row.Dimension, row.Code, row.Polarity, Value(row.Value))).ToArray();
        // Derive from the excerpts and keep only code and polarity: the derived text could echo the book.
        var derived = includeDetails
            ? SemanticAssessment.ArtifactExcerptEvidence.Derive(bookText, expected).Select(fact => new DerivedExcerptFact(fact.Id, fact.Code, fact.Polarity)).ToArray()
            : [];
        var profile = expected is null
            ? new TuningProfile(string.Empty, [], null, [], [], null, null, null, false, null, null, null, null)
            : new TuningProfile(expected.Title, [.. expected.AlternateTitles], expected.PrimaryAuthor, [.. expected.Series], [.. expected.Isbn13s],
                expected.AcceptedLanguage, expected.CatalogPageCount, expected.CatalogAudioSeconds, expected.AcceptedAsCollectionContaining,
                expected.AcceptedVariantKind, expected.PeerMedianSizeBytes, expected.PeerCount, expected.AssetSizeBytes);
        return new ArtifactHoldInput(
            SchemaVersion, new TuningAsset(asset.MediaType, asset.Format, asset.Origin, asset.StorageState),
            new TuningVerification(verification.IsMatch, facts), profile, rows, includeDetails, derived);
    }

    /// <summary>
    /// What the model was shown for a chat message: the normalized message and the previous search, exactly as rendered. The person's
    /// name and every other profile fact are never part of it. The recorder saves this only while the separate chat switch is on.
    /// </summary>
    public static IntentCaseInput Intent(string normalizedMessage, string? previousSearch) =>
        new(SchemaVersion, normalizedMessage, previousSearch);
}
