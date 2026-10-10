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

    /// <summary>Why the candidate decision is not a match despite no contradiction.</summary>
    public static class CandidateDecision
    {
        public const string InsufficientEvidence = "candidate.decision.insufficient_evidence";
    }
}
