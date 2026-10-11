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
        public const string ContentContradicted = "identity.hold.content_contradicted";
        public const string ContentInconclusive = "identity.hold.content_inconclusive";
        public const string LanguageConflict = "identity.hold.language_conflict";
        public const string NotAttested = "identity.hold.not_attested";
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
    }

    /// <summary>The language the file itself declares, compared with the language accepted for the request.</summary>
    public static class ArtifactLanguage
    {
        public const string DeclaredMatchesRequest = "artifact.language.declared_matches_request";
        public const string DeclaredConflicts = "artifact.language.declared_conflicts";
        public const string NotDeclared = "artifact.language.not_declared";
        public const string NotAssessed = "artifact.language.not_assessed";
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

    /// <summary>Malware and safety scanning results.</summary>
    public static class ArtifactSecurity
    {
        public const string ScanClean = "artifact.security.scan_clean";
        public const string ThreatDetected = "artifact.security.threat_detected";
        public const string ScanError = "artifact.security.scan_error";
        public const string ScannerUnavailable = "artifact.security.scanner_unavailable";
    }
}
