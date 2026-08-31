# Family Librarian — Book Matching Design Findings

## Status

Design guidance derived from the deterministic book-validation work. This document focuses on **matching a requested work to acquisition candidates** and on preserving the evidence needed for later post-download validation and future semantic/LLM matching.

The immediate implementation target is deterministic. AI/LLM matching is a future extension and must not be required by the initial implementation.

---

## 1. Core Finding: Matching and Validation Should Share One Evidence Model

Family Librarian should not treat candidate matching and post-download validation as unrelated systems.

The acquisition matcher should produce a structured record explaining why a candidate appears to represent the requested work. That evidence should be passed unchanged into artifact validation after download.

Recommended flow:

```text
Requested Work
    |
    v
ExpectedBookProfile / Match Intent
    |
    v
Provider Search Results
    |
    v
Deterministic Candidate Matcher
    |
    +--> CandidateMatchResult + MatchEvidence[]
    |
    v
Acquisition
    |
    v
Artifact Inspection
    |
    v
Deterministic Book Validation
        compares observed artifact evidence
        against the same ExpectedBookProfile
        and the acquisition-time MatchEvidence
```

Do not throw away acquisition-time reasoning once a file has been selected.

---

## 2. Model Three Different Identities

Family Librarian should distinguish:

```text
WORK
    The intellectual work the user wants to read.
    Example: Debt of Honor by Tom Clancy

EDITION
    A particular published version of that work.
    Example: specific publisher/date/ISBN/language/revision

ARTIFACT
    The actual file Family Librarian acquired.
    Example: one EPUB with a file hash, embedded metadata,
    TOC, chapter structure, and extracted content measurements
```

This distinction prevents a common matching error: treating an exact retail edition as if it were the only valid representation of a work.

### Work-level fields

Suggested fields:

```text
WorkId
CanonicalTitle
AlternateTitles[]
PrimaryAuthors[]
KnownAuthorVariants[]
Series
SeriesPosition
KnownEditionIds[]
KnownLanguages[]
WorkType
```

### Edition-level fields

Suggested fields:

```text
EditionId
WorkId
ISBN10
ISBN13
ProviderEditionIds[]
Language
Publisher
PublicationDate
EditionTitle
Subtitle
Revision
TranslationInformation
AbridgementStatus
SingleWork / Collection / Omnibus
KnownPageCount
KnownChapterInformation
```

### Artifact-level fields

Suggested fields:

```text
ArtifactId
FileHash
FileFormat
FileSize
EmbeddedTitle
EmbeddedAuthors[]
EmbeddedIdentifiers[]
EmbeddedLanguage
ObservedTOC
ObservedChapterCount
ObservedWordCount
ObservedReadingOrder
ArtifactValidationResult
```

---

## 3. Add an Explicit Match Intent / Acceptance Profile

A search request should contain more than a title and author.

Family Librarian needs to know what variations are acceptable for this particular request.

Suggested `BookMatchIntent` or equivalent:

```text
WorkId
RequestedLanguage
EditionPolicy
    ANY_ACCEPTABLE_EDITION
    SPECIFIC_EDITION
RequireFullWork = true
RequireUnabridged = true/false/unknown
RequireSingleWork = true/false
AllowTranslation = true/false
AllowedFormats[]
PreferredFormats[]
```

For the normal Family Librarian use case, recommended defaults are:

```text
EditionPolicy = ANY_ACCEPTABLE_EDITION
RequireFullWork = true
RequireSingleWork = true
RequestedLanguage = user's requested/preferred language
```

Do not assume that every user requesting a work wants the exact edition returned by the metadata provider.

---

## 4. Preserve Raw and Normalized Metadata

For every candidate, store both:

1. the provider's original values;
2. normalized comparison values.

Example:

```text
Raw title:        "Debt of Honor: A Jack Ryan Novel"
Normalized title: "debt of honor"
```

Normalization should never destroy the original evidence.

Useful normalization includes:

- Unicode normalization;
- case folding;
- whitespace normalization;
- punctuation normalization;
- author-name reordering (`Clancy, Tom` vs `Tom Clancy`);
- ISBN normalization and check-digit validation;
- language-tag normalization;
- known title/subtitle splitting.

---

## 5. Do Not Blindly Strip Qualifiers From Titles

Some title suffixes are harmless edition noise:

```text
A Novel
25th Anniversary Edition
Movie Tie-In Edition
Jack Ryan #7
```

Other qualifiers fundamentally change what the item is:

```text
Preview
Sample
Excerpt
Summary
Study Guide
Workbook
Analysis
Abridged
Collection
Omnibus
Box Set
Books 1-3
```

The matcher should classify qualifiers rather than simply remove everything after punctuation or parentheses.

Example:

```text
Expected: Debt of Honor
Candidate: Debt of Honor / Executive Orders
```

This must not become an automatic match merely because the expected title is a substring of the candidate title.

---

## 6. Deterministic Evidence Hierarchy

The matcher should collect independent pieces of evidence rather than produce one opaque fuzzy score.

Recommended evidence strength classes:

```text
CONCLUSIVE
STRONG
MODERATE
WEAK
INFORMATIONAL
```

### Strong identity evidence

- known ISBN maps to an edition of the requested work;
- provider edition identifier is already associated with the requested work;
- normalized title + primary author match;
- known alternate title + primary author match.

### Supporting evidence

- series name matches;
- series position matches;
- language matches;
- publisher/date are compatible with a known edition;
- page/length metadata is plausible.

### Strong negative evidence

- valid ISBN is known to represent another work;
- primary author conflicts;
- explicit wrong language;
- explicit preview/sample/summary/study-guide classification;
- explicit omnibus/collection where a single work was requested;
- title clearly contains another complete work in addition to the requested one.

Missing information is `UNKNOWN`, not automatically negative evidence.

---

## 7. Suggested Deterministic Matching Order

Evaluate candidates approximately in this order.

### 7.1 Known identifiers

If a provider/ISBN identifier is already mapped to the requested work, treat this as very strong evidence.

An exact ISBN is edition identity, not necessarily work identity. Other ISBNs can still be valid editions of the same work.

### 7.2 Title + primary author

Use normalized title and author comparison.

Recommended match states:

```text
EXACT
NORMALIZED_EXACT
KNOWN_ALIAS
COMPATIBLE_VARIANT
AMBIGUOUS
CONFLICT
MISSING
```

A title match without author support is not enough for common or reused titles.

### 7.3 Language

If the provider explicitly identifies a language that conflicts with the request, reject the candidate before acquisition where policy permits.

Missing provider language should not reject the candidate; actual artifact language can be checked after download.

### 7.4 Unwanted variant markers

Check title, subtitle, description, format/type fields, and provider tags for preview/summary/collection/etc. indicators.

### 7.5 Series

Series and position are useful corroborating evidence, not primary identity proof.

### 7.6 Edition metadata

Publisher, publication date, page count, and format can help resolve ambiguity but should rarely override stronger work-identity evidence.

---

## 8. Fuzzy Matching Should Rank Candidates, Not Prove Identity

Deterministic string-similarity algorithms can be useful for candidate discovery and ordering.

Examples include:

- token similarity;
- edit distance;
- Jaro/Jaro-Winkler-style comparison;
- word-set overlap;
- normalized subtitle comparison.

However, a high fuzzy-title score alone should not authorize unattended acquisition/import.

Example risk:

```text
Debt of Honor
Summary of Debt of Honor by Tom Clancy
```

The lexical similarity is extremely high even though these are different products.

Use fuzzy matching to say:

> "This candidate deserves further comparison."

not:

> "This is definitely the requested book."

---

## 9. Primary Author and Contributor Roles Matter

Do not compare against an undifferentiated list of people.

Where provider metadata permits, distinguish:

```text
Author
Coauthor
Editor
Translator
Introduction/Foreword contributor
Illustrator
Narrator
```

A candidate should not match Tom Clancy merely because `Tom Clancy` appears somewhere in contributor metadata.

Unexpected coauthors should normally create an ambiguity/warning rather than an automatic rejection because legitimate collaborations and revised editions exist.

---

## 10. Same-Title Collision Rule

If title identity is strong but author identity is absent or conflicting, do not auto-match.

Examples:

```text
same title + expected author       -> potentially strong match
same title + different author      -> conflict
same title + missing author        -> ambiguous
```

Identifiers or other independent evidence can resolve the ambiguity.

---

## 11. Candidate Match Result Should Be Explainable

Suggested result:

```text
CandidateMatchResult
    CandidateId
    ExpectedWorkId
    Decision
        MATCH
        POSSIBLE_MATCH
        NO_MATCH
    Evidence[]
    Warnings[]
    NegativeEvidence[]
    RawProviderMetadata
    NormalizedProviderMetadata
```

Suggested evidence item:

```text
MatchEvidence
    RuleId
    Category
    Strength
    ExpectedValue
    ObservedValue
    Outcome
    Source
    Message
```

Example:

```text
RuleId: identity.title
Strength: STRONG
Expected: Debt of Honor
Observed: Debt of Honor: A Jack Ryan Novel
Outcome: NORMALIZED_EXACT
```

This same evidence should appear in administrator review screens and logs.

---

## 12. Track Provenance of Metadata Claims

When Family Librarian knows something about a work or edition, retain where that knowledge came from.

Example:

```text
Value: 9780399142185
Field: ISBN13
Source: MetadataProviderX
ProviderRecordId: ...
ObservedAt: ...
```

Do not silently merge conflicting metadata into one apparently authoritative value.

This becomes especially important as multiple metadata providers are supported.

Provider-specific IDs should remain provider-specific rather than being treated as universal identifiers.

---

## 13. Acquisition Matching and Artifact Validation Must Be Allowed to Disagree

A provider candidate can be a good match based on provider metadata and still download the wrong artifact.

Therefore:

```text
Candidate MATCH
```

must never mean:

```text
Artifact automatically trusted
```

After acquisition, compare embedded/observed artifact evidence with both:

- `ExpectedBookProfile`;
- the provider candidate metadata that led to the match.

Useful contradiction checks include:

```text
Provider title matches; embedded title conflicts
Provider author matches; visible title-page author conflicts
Provider says English; actual content is another language
Provider says full ebook; artifact contains explicit preview marker
```

Contradictions should be retained as first-class evidence.

---

## 14. Rejected Candidate Memory

Do not repeatedly reacquire a candidate known to be wrong.

Persist sufficient identity to recognize it again:

```text
Provider
ProviderCandidateId
Source identifier/URL where appropriate
File hash after acquisition
Observed ISBN
Observed title
Observed author
RejectionReason[]
```

A provider result known to produce a preview, wrong language, corrupt file, or wrong work should be skipped on future attempts unless an administrator clears the rejection record or the provider record materially changes.

---

# Future LLM / Semantic Matching Findings

The following should **not** be part of the deterministic MVP, but the current design should preserve enough evidence to add them without restructuring the matching pipeline.

## 15. Good Future Uses of an LLM

A semantic matcher could help with cases where deterministic metadata is insufficient, including:

1. **Work identity from content samples**  
   Determine whether sampled text actually appears consistent with the requested work when identifiers or metadata are poor.

2. **Unlabeled omnibus/collection detection**  
   Recognize that one artifact contains multiple works even when metadata does not explicitly say `omnibus` or `collection`.

3. **Preview/truncation detection**  
   Interpret end matter, narrative discontinuity, purchase prompts, or other contextual evidence that is difficult to encode as fixed rules.

4. **Summary/study-guide/derivative detection**  
   Identify a derivative work that intentionally uses the original book's title and author names but is not the original text.

5. **Edition relationship reasoning**  
   Assess whether apparently conflicting metadata still represents an acceptable edition of the requested work.

6. **Alternate-title resolution**  
   Help recognize regional titles, translated titles, historical title changes, and unusual subtitle relationships.

7. **Contributor-role ambiguity**  
   Help distinguish original author, editor, translator, collaborator, and other roles when provider metadata is incomplete.

8. **Chapter-structure comparison across editions**  
   Compare differently labeled or nested TOCs and decide whether they plausibly represent the same complete work.

9. **Conflicting evidence explanation**  
   Analyze why metadata, title-page text, TOC, and content samples disagree and produce an advisory finding.

10. **Completeness reasoning**  
    Determine whether beginning/middle/end samples appear to form a complete work when no authoritative chapter or word-count reference is available.

---

## 16. Future LLM Should Consume Existing Evidence, Not Replace Extraction

Recommended future input:

```text
ExpectedBookProfile
BookMatchIntent
CandidateMatchResult
InspectedBookArtifact
DeterministicValidationResult
Selected sanitized content samples
```

The LLM should not be responsible for ZIP parsing, ISBN validation, language-tag parsing, TOC extraction, file hashing, or other tasks that deterministic code can perform reliably.

---

## 17. Preserve Future Semantic Findings Separately

When LLM validation is added, do not let it silently overwrite canonical metadata or deterministic findings.

Suggested record:

```text
SemanticFinding
    FindingId
    WorkId
    CandidateId / ArtifactId
    FindingType
    Outcome
    EvidenceReferences[]
    Explanation
    ModelProvider
    ModelName
    ModelVersion
    PromptSchemaVersion
    ArtifactHash
    CreatedAt
```

This permits Family Librarian to:

- explain why AI influenced a decision;
- rerun ambiguous artifacts with a newer model;
- compare model behavior over time;
- invalidate findings if the artifact changes;
- retain deterministic truth separately from semantic interpretation.

---

## 18. Do Not Treat LLM Self-Reported Confidence as Probability

A future model may return categorical findings such as:

```text
MATCH
LIKELY_MATCH
AMBIGUOUS
LIKELY_WRONG
WRONG
```

along with evidence and explanation.

Family Librarian policy should combine that finding with deterministic evidence.

Do not use a model-generated `97% confidence` as the sole basis for unattended acceptance.

---

## 19. Recommended Future AI Invocation Policy

Use semantic validation primarily for deterministic ambiguity:

```text
Deterministic MATCH / PASS with strong evidence
    -> no LLM required

Deterministic POSSIBLE_MATCH / REVIEW_REQUIRED
    -> optional local semantic validator

Deterministic NO_MATCH / REJECT with conclusive evidence
    -> do not spend LLM resources
```

This makes local AI a quality-enhancement layer rather than a dependency.

---

# Recommended Family Librarian Changes Now

The deterministic implementation should introduce or confirm the following concepts now:

1. `Work` vs `Edition` vs `Artifact` identity.
2. `ExpectedBookProfile` shared by acquisition matching and validation.
3. `BookMatchIntent` describing acceptable language/edition/full-work/single-work constraints.
4. Raw + normalized provider metadata storage.
5. Provider-specific identifier provenance.
6. Structured `CandidateMatchResult` and `MatchEvidence`.
7. Positive, negative, contradictory, and unknown evidence as distinct states.
8. Explicit unwanted-variant classification rather than naive title stripping.
9. Deterministic fuzzy similarity used only as supporting/ranking evidence.
10. Rejected-candidate memory to prevent repeated bad downloads.
11. Artifact validation that can contradict the provider-time match.
12. A future `SemanticFinding` extension point that remains advisory and auditable.

The key design principle is:

> **Family Librarian should not ask only whether two strings look similar. It should accumulate objective evidence that a provider candidate and, later, an acquired artifact represent an acceptable edition of the requested work.**
