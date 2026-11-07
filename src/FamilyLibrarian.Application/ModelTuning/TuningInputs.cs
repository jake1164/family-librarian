using System.Text.Json.Serialization;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.ModelTuning;

public interface ITuningInput { int SchemaVersion { get; } }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TuningAsset(RequestMediaType MediaType, string Format, AssetOrigin Origin, MediaAssetStorageState StorageState);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeclaredBook(string Title, string? PrimaryAuthor, string? Language);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TuningIdentity(string Title, string? Author, IReadOnlyList<string> Isbn13Candidates,
    IReadOnlyList<BookAuthor> Authors, IReadOnlyList<BookSeries> Series, string? Language, int? PublicationYear,
    string? Publisher, IReadOnlyList<string> AlternateTitles)
{
    internal BookIdentity Build() => new(Title, Author, Isbn13Candidates, Authors, Series, Language, PublicationYear, Publisher, AlternateTitles);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CandidateWork(string Title, IReadOnlyList<BookAuthor> Authors, IReadOnlyList<BookSeries> Series);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CandidateEdition(string? Language, int? PublicationYear, string? Publisher, IReadOnlyList<BookIdentifier> Identifiers);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CandidateRelease(string? Name, string? Format, long? SizeBytes, string? AssertedLanguage,
    bool? IsCollection, bool? IsSample, bool? IsAbridged, bool? IsUnabridged);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TuningCandidate(string Key, CandidateWork Work, CandidateEdition? Edition, CandidateRelease? ReleaseFacts);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TuningAffinity(AuthorAffinityKind Kind, int Score);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TuningPart(int Number, int? Total);
public enum ReleaseConcernKind { None, DrmUnconfirmed, Other }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TuningFulfillment(RequestMediaType MediaType, string? Format, string? Language, string? Title,
    string? Author, int? PublicationYear, string? Publisher, long? SizeBytes, BookMatchBasis? MatchBasis,
    bool HasPlausibleTitle, TuningAffinity? AuthorAffinity, bool RequiresLanguageConfirmation,
    bool RequiresReleaseConfirmation, ReleaseConcernKind ReleaseConcernKind, bool RequiresNarrationConfirmation,
    NarrationKind NarrationKind, bool IsRetail, AcquisitionSuitability? AcquisitionSuitability,
    TuningPart? AudiobookPart, bool FragmentOnlyConcern);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TuningOption(TuningCandidate Candidate, TuningFulfillment Fulfillment);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TuningFact(string FindingId, EvidenceDimension Dimension, string Code, EvidencePolarity Polarity, string? Value);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TuningEvidence(string RecordKind, string FindingId, EvidenceDimension Dimension, string Code, EvidencePolarity Polarity, string? Value);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TuningVerification(bool IsMatch, IReadOnlyList<TuningFact> Facts);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DerivedExcerptFact(string FindingId, string Code, EvidencePolarity Polarity);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TuningProfile(string Title, IReadOnlyList<string> AlternateTitles, string? PrimaryAuthor,
    IReadOnlyList<BookSeries> Series, IReadOnlyList<string> Isbn13s, string? AcceptedLanguage,
    int? CatalogPageCount, int? CatalogAudioSeconds, bool AcceptedAsCollectionContaining,
    AcquisitionQuestionKind? AcceptedVariantKind, long? PeerMedianSizeBytes, int? PeerCount, long? AssetSizeBytes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArtifactAssessmentInput(int SchemaVersion, TuningAsset Asset, DeclaredBook Declared,
    IReadOnlyList<TuningEvidence> Recorded, bool IncludeDetails) : ITuningInput;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArtifactHoldInput(int SchemaVersion, TuningAsset Asset, TuningVerification Verification,
    TuningProfile ExpectedProfile, IReadOnlyList<TuningEvidence> Recorded, bool IncludeDetails,
    IReadOnlyList<DerivedExcerptFact> DerivedExcerptFacts) : ITuningInput;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CandidateAssessmentInput(int SchemaVersion, TuningIdentity Requested, TuningCandidate Candidate) : ITuningInput;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CandidateTriageCaseInput(int SchemaVersion, TuningIdentity Requested, IReadOnlyList<TuningOption> Candidates,
    string? PickKey, IReadOnlyList<string> TiedKeys, ProviderConfidenceLevel Confidence) : ITuningInput;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SearchCaseInput(int SchemaVersion, TuningIdentity Requested, IReadOnlyList<TuningOption> Results,
    IReadOnlyList<string> AlreadyTried) : ITuningInput;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IntentCaseInput(int SchemaVersion, string Message, string? PreviousSearch) : ITuningInput;
