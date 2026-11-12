namespace FamilyLibrarian.Domain.Evidence;

/// <summary>
/// The catalog of stable, machine-readable evidence reason codes (alpha.6 D-11).
/// </summary>
/// <remarks>
/// Codes are persisted and exchanged as plain strings, never as enum ordinals, so
/// they stay readable in storage and survive reordering. They are additive: a code
/// is never renamed or reused for a different meaning. Shape is
/// <c>&lt;subject&gt;.&lt;facet&gt;.&lt;value&gt;</c> in lower snake case. A code names
/// <em>what was observed</em>; it never carries an accept or reject decision, which
/// stays with the policy that consumes the evidence.
/// </remarks>
public static class EvidenceReasonCodes
{
    /// <summary>Independent catalog/source agreement available to grounded candidate triage.</summary>
    public static class CandidateCorroboration
    {
        public const string Identifier = "candidate.identifier.exact";
        public const string Year = "candidate.year.exact";
        public const string Publisher = "candidate.imprint.exact";
    }

    /// <summary>Requested-title evidence. Mirrors the candidate evidence states.</summary>
    public static class CandidateTitle
    {
        public const string Unknown = "candidate.title.unknown";
        public const string Exact = "candidate.title.exact";
        public const string Compatible = "candidate.title.compatible";
        public const string Partial = "candidate.title.partial";
        public const string Fuzzy = "candidate.title.fuzzy";
        public const string Conflicting = "candidate.title.conflicting";
    }

    /// <summary>Author evidence. Mirrors the author-affinity kinds.</summary>
    public static class CandidateAuthor
    {
        public const string Conflict = "candidate.author.conflict";
        public const string Unknown = "candidate.author.unknown";
        public const string Fuzzy = "candidate.author.fuzzy";
        public const string FirstOnly = "candidate.author.first_only";
        public const string FirstExactLastFuzzy = "candidate.author.first_exact_last_fuzzy";
        public const string LastOnly = "candidate.author.last_only";
        public const string LastExactFirstFuzzy = "candidate.author.last_exact_first_fuzzy";
        public const string Compatible = "candidate.author.compatible";
        public const string Exact = "candidate.author.exact";
        public const string ContextCorroborated = "candidate.author.context_corroborated";
    }

    /// <summary>Language evidence. Unknown is not a contradiction.</summary>
    public static class CandidateLanguage
    {
        public const string Unknown = "candidate.language.unknown";
        public const string Exact = "candidate.language.exact";
        public const string Compatible = "candidate.language.compatible";
        public const string Partial = "candidate.language.partial";
        public const string Fuzzy = "candidate.language.fuzzy";
        public const string Conflicting = "candidate.language.conflicting";
    }

    /// <summary>Series-position evidence. Unknown is not a contradiction.</summary>
    public static class CandidateSeries
    {
        public const string Unknown = "candidate.series.unknown";
        public const string Exact = "candidate.series.exact";
        public const string Compatible = "candidate.series.compatible";
        public const string Partial = "candidate.series.partial";
        public const string Fuzzy = "candidate.series.fuzzy";
        public const string Conflicting = "candidate.series.conflicting";
    }

    /// <summary>An explicit contradiction between the request and the candidate.</summary>
    public static class CandidateContradiction
    {
        public const string Title = "candidate.contradiction.title";
        public const string Author = "candidate.contradiction.author";
        public const string SeriesPosition = "candidate.contradiction.series_position";
        public const string Language = "candidate.contradiction.language";
    }

    /// <summary>A structural property that needs a person's judgment rather than contradicting the request.</summary>
    public static class CandidateCondition
    {
        public const string DrmUnconfirmed = "candidate.condition.drm_unconfirmed";
        public const string Collection = "candidate.condition.collection";
        public const string Sample = "candidate.condition.sample";
        public const string Abridged = "candidate.condition.abridged";
        public const string QualifierReview = "candidate.condition.qualifier_review";
        public const string CombinedWork = "candidate.condition.combined_work";
        public const string CompanionParts = "candidate.condition.companion_parts";
        public const string MalformedPartNumbering = "candidate.condition.malformed_part_numbering";
        public const string MalformedBookNumber = "candidate.condition.malformed_book_number";

        /// <summary>Fallbacks for a condition raised without a specific code.</summary>
        public const string EditionReview = "candidate.condition.edition_review";
        public const string MalformedStructure = "candidate.condition.malformed_structure";
    }

    /// <summary>
    /// Why an asset is held as unmatched (<c>MediaAsset.IdentityHoldCode</c>). Observations of the existing checks'
    /// outcomes; M1-5a labels the holds that already exist and adds no new hold.
    /// </summary>
    public static class IdentityHold
    {
        public const string ModelConcern = "identity.hold.model_concern";
        public const string ContentContradicted = "identity.hold.content_contradicted";
        public const string ContentInconclusive = "identity.hold.content_inconclusive";
        public const string LanguageConflict = "identity.hold.language_conflict";
        public const string NotAttested = "identity.hold.not_attested";

        /// <summary>No content verifier exists for the format and the only vouching came from an external provider (M1-5b, P1).</summary>
        public const string AttestedOnlyExternal = "identity.hold.attested_only_external";

        /// <summary>No content verifier exists and the asset has no trustworthy origin to rest an attestation on (M1-5b).</summary>
        public const string AttestedOnlyUnknownOrigin = "identity.hold.attested_only_unknown_origin";

        /// <summary>The package metadata labels the file a sample, excerpt, preview or teaser (M1-5b).</summary>
        public const string CompletenessSampleMarker = "identity.hold.completeness_sample_marker";

        /// <summary>Reading-order items point at files that are not in the archive (M1-5b).</summary>
        public const string CompletenessSpineContentMissing = "identity.hold.completeness_spine_content_missing";
        public const string SeriesPositionDiffers = "identity.hold.series_position_differs";
        public const string LengthFarBelowExpected = "identity.hold.length_far_below_expected";
        public const string DurationFarBelowExpected = "identity.hold.duration_far_below_expected";
        public const string AbridgedMarker = "identity.hold.abridged_marker";

        /// <summary>The file is a dramatized (full-cast) adaptation, not the narrated audiobook: kept in reserve.</summary>
        public const string DramatizedAdaptation = "identity.hold.dramatized_adaptation";
        public const string CollectionMissingRequestedWork = "identity.hold.collection_missing_requested_work";
        public const string TocEntriesMissing = "identity.hold.toc_entries_missing";
    }

    /// <summary>Why the candidate decision is not a match despite no contradiction.</summary>
    public static class CandidateDecision
    {
        public const string InsufficientEvidence = "candidate.decision.insufficient_evidence";
    }

    /// <summary>
    /// Who vouched for an artifact's identity before its content was read. This is NOT
    /// content verification and must never be read as such (alpha.6 D-14).
    /// </summary>
    public static class ArtifactIdentityAttestation
    {
        /// <summary>Supplied through the manual-upload route, which is administrator-only.</summary>
        public const string ManualUpload = "artifact.identity_attestation.manual_upload";
        public const string BuiltInSource = "artifact.identity_attestation.built_in_source";
        public const string ExternalProviderConfirmed = "artifact.identity_attestation.external_provider_confirmed";
        public const string UnknownOrigin = "artifact.identity_attestation.unknown_origin";

        /// <summary>A row that predates origin tracking; nothing is inferred about its source.</summary>
        public const string LegacyUnclassified = "artifact.identity_attestation.legacy_unclassified";
        public const string None = "artifact.identity_attestation.none";

        /// <summary>An administrator reviewed a held asset and approved it on their own judgment (M1-5c). A human attestation, never content verification.</summary>
        public const string AdminOverride = "artifact.identity_attestation.admin_override";

        /// <summary>The (bounded) reason the administrator gave for an override.</summary>
        public const string AdminOverrideReason = "artifact.identity_attestation.admin_override_reason";
        public const string CollectionConsentRecorded = "artifact.identity.collection_consent_recorded";
        public const string ModelRelease = "artifact.identity_attestation.model_release";
    }

    /// <summary>What the file's own content says about its identity, independent of any attestation.</summary>
    public static class ArtifactIdentityCorroboration
    {
        public const string Corroborated = "artifact.identity_corroboration.corroborated";

        /// <summary>A verifier read the file and found an explicit difference.</summary>
        public const string Contradicted = "artifact.identity_corroboration.contradicted";

        /// <summary>A verifier ran but could not read enough to compare. Not a contradiction.</summary>
        public const string Inconclusive = "artifact.identity_corroboration.inconclusive";

        /// <summary>No content verifier exists for this format, so the content was never compared.</summary>
        public const string Unavailable = "artifact.identity_corroboration.unavailable";

        public const string TitleAgrees = "artifact.identity_title.agrees";
        public const string TitleDiffers = "artifact.identity_title.differs";
        public const string TitleAbsent = "artifact.identity_title.absent";
        public const string AuthorAgrees = "artifact.identity_author.agrees";
        public const string AuthorDiffers = "artifact.identity_author.differs";
        public const string AuthorAbsent = "artifact.identity_author.absent";
        public const string AuthorSpellingVariant = "artifact.identity.author_spelling_variant";
        public const string ContainsRequestedWork = "artifact.identity.contains_requested_work";

        /// <summary>The file carries no readable title or author tags at all (3a). Unknown, never a contradiction.</summary>
        public const string TagsAbsent = "artifact.identity.tags_absent";

        /// <summary>Which tag format the identity values were read from (for example id3v2.3). A description, never a verdict.</summary>
        public const string TagSource = "artifact.identity.tag_source";

        /// <summary>Only a track-level title tag exists; track titles such as "Chapter 01" say nothing about the book. Unknown.</summary>
        public const string TitleTrackOnly = "artifact.identity_title.track_title_only";
    }

    /// <summary>The language the file itself declares, compared with the language accepted for the request.</summary>
    public static class ArtifactLanguage
    {
        public const string DeclaredMatchesRequest = "artifact.language.declared_matches_request";
        public const string DeclaredConflicts = "artifact.language.declared_conflicts";
        public const string NotDeclared = "artifact.language.not_declared";
        public const string NotAssessed = "artifact.language.not_assessed";

        /// <summary>A language cue word in the album, title or file name (for example "ungekürzt") conflicts with the request.</summary>
        public const string CueConflicts = "artifact.language.cue_conflicts";
        public const string CueMatchesRequest = "artifact.language.cue_matches_request";

        /// <summary>The descriptive tags are written in a language other than the accepted one. Inferred, so evidence only.</summary>
        public const string InferredConflicts = "artifact.language.inferred_conflicts";

        /// <summary>The descriptive tags are written in the accepted language.</summary>
        public const string InferredMatchesRequest = "artifact.language.inferred_matches_request";
    }

    /// <summary>Container and format validator results.</summary>
    public static class ArtifactStructure
    {
        /// <summary>The validators that ran reported no defect. Not a claim that the file is complete.</summary>
        public const string NoDefectReported = "artifact.structure.no_defect_reported";
        public const string DefectReported = "artifact.structure.defect_reported";
    }

    /// <summary>Whether the file appears to be the complete work.</summary>
    public static class ArtifactCompleteness
    {
        public const string NotAssessed = "artifact.completeness.not_assessed";

        /// <summary>Measured reading-order and navigation counts. Informational; no judgment.</summary>
        public const string ReadingOrderCounted = "artifact.completeness.reading_order_counted";

        /// <summary>Cues were checked and none found. NOT proof of completeness.</summary>
        public const string NoCueDetected = "artifact.completeness.no_cue_detected";

        /// <summary>The package metadata itself labels the file a sample, excerpt, preview or teaser.</summary>
        public const string SampleMarkerInMetadata = "artifact.completeness.sample_marker_in_metadata";

        /// <summary>Table-of-contents links point at files that are not in the archive.</summary>
        public const string TocTargetsMissing = "artifact.completeness.toc_targets_missing";

        /// <summary>Reading-order items point at files that are not in the archive.</summary>
        public const string SpineContentMissing = "artifact.completeness.spine_content_missing";

        /// <summary>The same file is listed more than once in the reading order.</summary>
        public const string DuplicateReadingOrderItems = "artifact.completeness.duplicate_reading_order_items";
    }

    /// <summary>
    /// Measurements of an audiobook file taken from its headers. Informational: none of these codes is, or feeds, an identity
    /// or approval decision. A measurement that could not be taken is Unknown, never a guess.
    /// </summary>
    public static class ArtifactAudio
    {
        public const string DurationMeasured = "artifact.audio.duration_measured";
        public const string DurationUnavailable = "artifact.audio.duration_unavailable";
        public const string Codec = "artifact.audio.codec";
        public const string Bitrate = "artifact.audio.bitrate";
        public const string ChaptersCounted = "artifact.audio.chapters_counted";
        public const string ChaptersNotFound = "artifact.audio.chapters_not_found";
        public const string TrackPosition = "artifact.audio.track_position";

        /// <summary>Tracks of a numbered set are absent. Evidence only; the set is not judged here.</summary>
        public const string TrackSetGap = "artifact.audio.track_set_gap";
        public const string TrackSetDuplicate = "artifact.audio.track_set_duplicate";
        public const string NotAssessed = "artifact.audio.not_assessed";
    }

    /// <summary>Malware and safety scanning results.</summary>
    public static class ArtifactSecurity
    {
        public const string ScanClean = "artifact.security.scan_clean";
        public const string ThreatDetected = "artifact.security.threat_detected";
        public const string ScanError = "artifact.security.scan_error";
        public const string ScannerUnavailable = "artifact.security.scanner_unavailable";
    }

    /// <summary>
    /// Comparisons of bounded artifact facts with the catalog expectation. Only the explicit findings named by policy hold.
    /// </summary>
    public static class ProfileComparison
    {
        public const string SizePeerOutlier = "profile.compare.size_peer_outlier";
        public const string SeriesPositionDiffers = "profile.compare.series_position_differs";
        public const string SeriesPositionAgrees = "profile.compare.series_position_agrees";
        public const string LengthFarBelowExpected = "profile.compare.length_far_below_expected";
        public const string LengthBelowExpected = "profile.compare.length_below_expected";
        public const string DurationFarBelowExpected = "profile.compare.duration_far_below_expected";
        public const string DurationBelowExpected = "profile.compare.duration_below_expected";
        public const string AbridgedMarker = "profile.compare.abridged_marker";
        public const string HeadingsOtherLanguage = "profile.compare.headings_other_language";
        public const string TocEntriesMissing = "profile.compare.toc_entries_missing";
        public const string IdentifierAgrees = "profile.compare.identifier_agrees";
    }

    /// <summary>Fixed observations derived from transient EPUB passages; values never contain book text.</summary>
    public static class ArtifactExcerpt
    {
        public const string Text = "artifact.excerpt.text";
        public const string SampleMarker = "artifact.excerpt.sample_marker";
        public const string AbridgedMarker = "artifact.excerpt.abridged_marker";
        public const string ProseLanguageDiffers = "artifact.excerpt.prose_language_differs";
        public const string PrintedIsbnAgrees = "artifact.excerpt.printed_isbn_agrees";
    }

    public static class ArtifactProfile
    {
        public const string PackagePublisher = "profile.package.imprint";
        public const string PackageDate = "profile.package.date";
        public const string PackageIdentifier = "profile.package.identifier";
        public const string PackageLanguage = "profile.package.language";

        /// <summary>Value <c>{name}#{index}</c>.</summary>
        public const string PackageSeries = "profile.package.series";

        /// <summary>Value <c>words={n};items={m};basis=xhtml_text</c>.</summary>
        public const string TextWordCount = "profile.text.word_count";

        /// <summary>Why no word count was taken: a CJK or Thai package language, a bound, or unreadable content.</summary>
        public const string TextWordCountUnavailable = "profile.text.word_count_unavailable";

        /// <summary>Value <c>seconds={n};tracks={k}</c>.</summary>
        public const string AudioTotalDuration = "profile.audio.total_duration";

        /// <summary>The first ten chapter names, joined by " / ".</summary>
        public const string AudioSeriesPosition = "profile.audio.series_position";
        public const string AudioAsin = "profile.audio.asin";
        public const string AudioTagTruncated = "profile.audio.tag_truncated";
        public const string AudioChapterTitles = "profile.audio.chapter_titles";

        public const string TocEntry = "profile.toc.entry";
        public const string Heading = "profile.heading.title";
        public const string FrontMatter = "profile.section.front_matter";
        public const string BackMatter = "profile.section.back_matter";
    }

}
