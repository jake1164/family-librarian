# Family Librarian — Domain Model & Workflow Specification

**Status:** Draft v0.1  
**Date:** 2026-08-08

---

## 1. Purpose

Define the core entities and workflow states that allow Family Librarian to remain independent of metadata vendors, acquisition sources, malware scanners, audiobook servers, and e-reader vendors.

---

## 2. Domain Model Principles

1. A **Work** is not the same thing as an **Edition**.
2. A **Request** is not the same thing as a **Book**.
3. A downloaded file is an **Asset**, not the bibliographic Work.
4. Library destinations and user delivery targets must be replaceable, and they
   are separate facts. A destination is the permanent media store; Family
   Librarian storage is transient staging only.
5. Completed requests remain historical records.
6. Series and authors are first-class entities.
7. External provider identifiers are references, not primary domain identity.
8. Family ownership, a user's request, acquisition work, and delivery to a
   target are separate facts. They must not be inferred from one another.

---

## 3. Primary Entities

### User

Represents an application user.

Suggested fields:

```text
UserId
DisplayName
Email
Status
CreatedAt
LastLoginAt
```

Related data:

```text
Roles
NotificationPreferences
DeliveryTargets
FollowedAuthors
FollowedSeries
Requests
History
```

---

### Author

```text
AuthorId
CanonicalName
SortName
Biography
ImageAssetId?
ExternalIds[]
```

Relationships:

```text
Author -> Works
User -> FollowedAuthors
```

---

### Series

```text
SeriesId
Name
Description
Status
ExternalIds[]
```

Relationships:

```text
Series -> SeriesEntries
User -> FollowedSeries
```

Possible status:

```text
Active
Completed
Unknown
```

---

### Work

Represents the conceptual book.

Example:

```text
Project Hail Mary
```

Suggested fields:

```text
WorkId
CanonicalTitle
Description
PrimaryAuthorId
PublicationStatus
FirstPublicationDate?
ExpectedPublicationDate?
CoverAssetId?
ExternalIds[]
```

Relationships:

```text
Work -> Authors
Work -> Editions
Work -> SeriesEntries
```

---

### SeriesEntry

Links a Work to a Series.

```text
SeriesEntryId
SeriesId
WorkId
Position
PositionLabel
IsPrimary
```

Position may need to support:

```text
1
2
2.5
Novella
Prequel
Unknown
```

Avoid assuming all series positions are integers.

---

### Edition

Represents a particular publication/format/version.

```text
EditionId
WorkId
Title
Publisher
PublicationDate
Language
ISBN10?
ISBN13?
EditionType
ExternalIds[]
```

Potential edition types:

```text
Hardcover
Paperback
Ebook
Audiobook
Other
```

---

### BookRequest

Represents shared household intent to obtain media for a Work, with individual requester participation.

```text
RequestId
UserId
WorkId
RequestedAt
Status
Priority
Notes
```

Ordinary active requests are unique per canonical Work. Each requested media
type has one shared format row, so two people asking for the same format do not
create separate acquisition work. A Work-scoped transaction serializes creation,
joining, and withdrawal across users, backed by a unique active-request index.
`UserId` retains the original creator for historical attribution; membership
controls requester access.

`RequestParticipant` records the user, requested formats, private note, join time,
and withdrawal time. A requester can withdraw only their own interest. The whole
request closes when everyone withdraws. Asking again rejoins current shared work
or reopens the old ordinary request when no other active request exists. Members
see their own notes and formats; the admin queue exposes all participants.
Completion notices go to each active participant once their requested formats
are available, and live invalidations reach all participants.

An explicit version exception requires `VersionKind` (Language, Edition,
Narration, Accessibility, or Replacement) and `VersionDetails`. It starts in
`NeedsReview` with a persistent `RequiresManualFulfillment` restriction. Both
individual status changes and bulk rechecks must preserve the restriction;
automatic workers exclude it. Identical explicit descriptions join the same
active review request; the application does not infer equivalent versions from
free text. A librarian must assess the difference before selecting a copy.
These fields do not establish automatic variant-aware library matching.

During upgrade, existing creator membership and private notes are backfilled.
Historical overlaps are held for review with their original request, format,
and file references intact. The oldest is the ordinary shared entry; additional
historical entries carry `LegacyOverlap` and cannot reenter automation. This
preserves evidence where the original requests did not record version intent.

Requested formats should not be a single enum if multiple formats can be requested.

Use child records:

```text
RequestFormat
  RequestId
  MediaType
  Status
```

Media types:

```text
Ebook
Audiobook
```

`BookRequest` is the user-intent aggregate. Its summary status is derived from
its child `RequestFormat` records and is not an ownership record. A request can
be satisfied from an existing family-owned asset without an acquisition job.

Each `RequestFormat` has an independent lifecycle. Conceptually:

```text
Requested
  -> CheckingLibrary
      -> Available                  (an owned, already deliverable format)
      -> Delivering -> Available    (an owned format needs delivery)
      -> LinkedLibraryAvailable -> [WaitingForSecurityScanner] -> Staging -> Processing -> Importing -> Available
      -> Searching -> Found -> [PendingApproval] -> [WaitingForSecurityScanner] -> Acquiring
         -> Processing -> Importing -> Available
      -> WaitingForAvailability
```

Names can vary in implementation, but the model must preserve the distinction
between intent, ownership, acquisition, processing/import, delivery, and final
availability. `WaitingForSecurityScanner` is used only when the requested path
would accept, download, or stage file bytes; catalog search and request creation
remain available while the scanner is unhealthy. A combined Ebook + Audiobook
request therefore has two independent format states rather than one opaque
request state.

---

### WorkFormatAvailability

Represents the family's current local availability for a Work and one media
type. It is a catalogue read model, not a user request and not a delivery log.

```text
WorkId
MediaType
OwnershipState
PreferredAssetId?
PreferredEditionId?
AcquisitionState?
```

`OwnershipState` distinguishes at least `NotOwned` and `Owned`. The model may
also expose an in-progress state derived from active request formats. It must
remain possible for an owned asset to have no delivery record, or to have been
delivered to one target but not another.

Search-result enrichment reads this model with the current user's requests and
delivery targets to produce format-level indicators and actions. It must resolve
by canonical Work/Edition and retained identifiers first (internal IDs, provider
IDs, ISBNs, and relevant audiobook identifiers). Title/author fuzzy matching is
only a fallback because titles, translations, boxed sets, punctuation, editions,
and abridged audio can be ambiguous.

`WorkFormatAvailability` represents local family ownership only. It must not be
overloaded with a public-library hold, a commercial price, or an external action.
Those are provider results and may coexist with `NotOwned` or `Owned`.

An item found in a linked Calibre-Web library is similarly separate from local
ownership: it is a provider-backed `LinkedLibraryAvailable` outcome, not a
Family Librarian media copy. The application may stage its requested file into
quarantine, then create a provenance record after the normal safety pipeline
passes and the selected destination verifies import.

**Implementation note (current state, 2026-08-29):** the placeholder
`WorkFormatAvailability` implementation was retired. The working ownership
read model is `FulfillmentOption`/`IOwnedLibraryProvider`
(`docs/03-provider-api-contracts.md` §3), specifically
`CwaOwnedLibraryProvider` and `AudiobookshelfOwnedLibraryProvider`, surfaced
through `GET /works/{workId}/fulfillment-options` and rendered on the work
detail page. `BookRequestService.CreateAsync` queries the same model before it
creates a request. An owned match produces a deliberate, confirmable warning;
it does not silently create needless acquisition work. Identifier-first
matching, suitability checks, and a deliberate replacement reason remain
required before an existing match can automatically satisfy an irreversible
fulfillment decision.

---

### LibraryItemReference

Represents a verified reference to an item in an external library catalog. It
allows Family Librarian to locate an existing Calibre-Web/CWA book without
importing the whole library or treating the library's database entities as its
own domain records.

```text
LibraryItemReferenceId
ProviderId
ExternalBookId
WorkId?
EditionId?
MediaType
Format
MatchConfidence
CatalogUrl?
LastVerifiedAt
```

Ordinary ownership lookup accepts only a unique English-or-unspecified match.
Ambiguous and language-excluded library candidates are not `Owned` fulfillment
options: they must neither block an ordinary request nor enable Send Existing.
A low-confidence title/author confirmation does not resolve either condition.
Owned-library candidate selection remains a separate future workflow.

The reference is retained only after an identifier-first or explicitly reviewed
match. A title/author-only match remains ambiguous until confirmed. A catalog
URL is a user-facing deep-link hint, never a credential-bearing download URL.

---

### Language consent

Automatic matching defaults to English; missing language metadata stays eligible.
Accepting a preference-review candidate stores that candidate's language on the
request format. EPUB identity verification and CWA/Audiobookshelf verification
then require that language, including when the selected edition is English.
Known two/three-letter ISO codes, regional tags, and English language names are
normalized (for example `es`, `spa`, `Spanish`, and `es-MX`). Unknown values
require exact case-insensitive equality. Accepting Spanish never authorizes
French or another declared language. With multiple EPUB language declarations,
at least one must match. Missing declarations remain eligible.

### FulfillmentOption

Represents a provider-supplied way to obtain, access, or act on one Work/format.
It is an enriched read model and an audit/provenance record when a user or admin
chooses it; it is not a request, ownership record, or artifact.

```text
ProviderId
ProviderResultId
WorkId / EditionId?
MediaType
OptionKind              Owned | Availability | StoreOffer | DirectAcquisition | ExternalAction
AcquisitionMethod       Borrow | Purchase | DirectDownload | ManualImport | OwnedImport | ProviderManaged
Format / Language / Quality?
Availability / Cost / Currency?
LicenseOrUsageStatus / DrmStatus?
ExternalActionUri?
ProviderData
```

Opaque provider data remains outside the domain's decision rules. The core uses
only standardized facts needed for authorization, display, and policy. A store
offer does not create an `AcquisitionJob`; a borrow/hold or external action may
record user intent without claiming Family Librarian acquired a file.

---

### ProviderPolicyProfile

Represents an explainable selection policy, not provider configuration. Provider
configuration controls whether a provider can be used; a policy ranks options
that are already permitted.

The first policy model should be intentionally small:

```text
PolicyProfileId
Scope                    SystemDefault | User
MediaType?
Ordered rules            Prefer | Deprioritize | Skip
ProviderId or capability
Manual/automatic permission
```

Initial profiles may be `Library First`, `Free First`, `Lowest Cost`, and
`Manual Choice`. Each recommendation retains the matched rule/profile so an
administrator or user can understand why it was selected. Role, author, series,
title, price/wait, and time-based fallback rules remain later extensions rather
than a generic rules language introduced before real options are available.

---

### AcquisitionJob

Represents an attempt to obtain media.

```text
AcquisitionJobId
RequestId
MediaType
ProviderId
ScannerHealthAtStart?
Status
CreatedAt
StartedAt
CompletedAt
FailureReason?
```

A request may have multiple acquisition jobs.

**Provider-attempt ledger:** `AcquisitionJob` is created only after an artifact
is successfully staged, so it cannot explain providers that found no result.
The append-only `ProviderAttempt` ledger records every automatic lookup with the
request format, provider ID, outcome, safe summary, attempted time, and next
eligible check time. The administrator request-detail page displays the ledger.
It never contains download URLs, provider credentials, or untrusted response
bodies.

The provider-attempt record is distinct from a successful `AcquisitionJob`:
one request format can have several unsuccessful provider checks and no job at
all, then later have one job that stages an artifact. The administrator timeline
must present both in chronological order.

Required malware-scanner health is a separate, mandatory acquisition gate. The
application checks it before creating a manual-upload, linked-library staging,
or provider-download attempt. If it is unavailable, the request format remains
`WaitingForSecurityScanner`; no file bytes are accepted or acquired. A health
loss after acquisition has begun leaves the file in quarantine and creates a
retryable held job. Recovery resumes queued work without requiring the user to
submit another request.

---

### AcquisitionCandidate

A provider result that may or may not become an accepted asset.

```text
CandidateId
AcquisitionJobId
ProviderId
ProviderReference
Title
Author
Format
Size
Duration?
Bitrate?
MetadataJson
ConfidenceScore?
Status
```

Candidate statuses:

```text
Discovered
Selected
Acquired
Rejected
Failed
```

---

### MediaAsset

Represents a concrete file or file set.

```text
AssetId
WorkId
EditionId?
MediaType
Format
OriginalFilename
StoredFilename
SizeBytes
Sha256
DetectedMimeType
StorageState
CreatedAt
```

Storage states:

```text
Quarantine
Processing
Rejected
Trusted
Archived
```

---

### SecurityEvaluation

Represents the overall security decision for an Asset.

```text
SecurityEvaluationId
AssetId
Status
CreatedAt
CompletedAt
PolicyVersion
```

Statuses:

```text
Pending
Passed
Failed
ReviewRequired
```

---

### SecurityScanResult

One Asset may have many scan results.

```text
ScanResultId
SecurityEvaluationId
ProviderId
ScannerType
EngineVersion
SignatureVersion?
StartedAt
CompletedAt
Status
ThreatName?
DetailsJson
```

Status:

```text
Clean
Detected
Error
Unavailable
```

---

### FormatValidationResult

```text
ValidationResultId
AssetId
ValidatorId
Status
Details
```

Examples:

```text
EPUB structure valid
M4B parsable
MIME matches extension
No forbidden embedded executable
```

---

### Approval

Represents a human or policy decision.

```text
ApprovalId
AssetId
Decision
ActorType
ActorUserId?
PolicyName?
CreatedAt
Reason?
```

Decision:

```text
Approved
Rejected
NeedsReview
```

Actor types:

```text
Admin
Policy
System
```

---

### LibraryImport

Represents publication of an approved staged Asset to an external library. The
external library becomes the permanent media store. It is not necessarily a
user-specific delivery attempt: one library import can make a book available to
many users or later delivery targets.

```text
LibraryImportId
AssetId
ProviderId
DestinationReference
ExternalBookId?
Status
CreatedAt
CompletedAt?
FailureReason?
```

Statuses:

```text
Pending
Staging
Importing
Verifying
Available
Failed
Cancelled
```

For every destination, `Available` requires that the expected work/format be
verified through that destination's supported catalog/API surface. Once that
succeeds, Family Librarian keeps the artifact's checksum, validation/approval
evidence, and opaque destination reference, then removes its local media copy
under the staging-retention policy. For CWA, the managed Calibre library—not its
processed ingest copy—is the permanent ebook archive.

**Local vs. remote CWA does not change this model.** Whether the ingest
transport is a shared filesystem or SFTP, `LibraryImport` verification always
resolves the resulting book through the CWA catalog/query connection (OPDS),
never through ingest-directory contents. A file disappearing from the ingest
folder, or an SFTP upload completing, is evidence the transfer happened — it
is not evidence CWA imported the expected book. `CwaPublishingService`
already implements this correctly: it hands the file to whichever
`ICwaIngestTransport` is configured, then separately calls `ICwaCatalogClient`
to resolve the resulting book ID before marking the import `Available`. See
`docs/01-product-architecture-spec.md` §12.1.1 for the full topology model.

**Correlation is now identifier-first, 2026-08-31.** The OPDS lookup
(`CwaCatalogClient`) tries each ISBN-13 known for the Work as a search query
before falling back to title/author matching — see
`docs/03-provider-api-contracts.md` "OPDS integration stability" for the
full algorithm, including the ambiguity-safe fallback (more than one distinct
title/author match now returns "not found" rather than guessing) and the
derivative/combined-title guard (rejects "Summary of X", "X / Y", etc.).
Whether CWA's search indexes ISBN is unconfirmed, but the fallback degrades
safely either way. Remaining gaps: no per-edition ISBN selection (any ISBN
known for the Work is tried), and a CWA entry missing an `<author>` element
is not treated as a mismatch.

After a handoff, a canonical lookup miss can also be resolved by the exact approved
file: title searches and the recent-books feed discover candidates, then the offered
format must have the same byte count and SHA-256 as the Trusted asset. This permits
an approved author variant or missing byline to reach Available without changing
ownership matching or repeating identity approval. Language eligibility still applies;
multiple byte-identical book IDs remain ambiguous. Unreadable or changed files stay
AwaitingVerification. CWA must preserve the approved bytes for this fallback.

Automatic fulfillment and an administrator's explicit external acquisition
share active provider work for a requested format. After fresh source checks,
submitting the same unchanged candidate returns the existing job; a competing
copy must wait for completion or cancellation. Distinct numbered parts of the
same validated audiobook set may proceed together; a job waiting on human
interaction still permits a different copy to progress. Duplicate submissions must
not be resolved by weakening the destination's catalog ambiguity checks.

**Existing-artifact suitability is not yet modeled.** A `LibraryImport`
reaching `Available`, or a `FulfillmentOption` with `OptionKind.Owned`, is
currently treated as unconditionally usable. Nothing checks that the resolved
artifact still exists, has a sensible size, is in a format the requested
delivery method can use, or passes a basic integrity check before it is reused
for delivery or shown as "owned." The architecture needs to support at least
three outcomes for an existing match — usable as-is, usable after
conversion/repair, or unusable (missing/corrupt) — with the unusable case
routing to a distinct `ReplaceDefectiveCopy`/`UpgradeOrReplaceExistingCopy`
acquisition reason rather than silently behaving as if the title were never
owned. `AcquisitionJob` does not currently carry a reason/cause field at all,
so this will need one.

---

### DeliveryTarget

Represents where content can be sent.

```text
DeliveryTargetId
UserId
ProviderId
ProviderType
DisplayName
ConfigurationReference
Enabled
```

Provider types:

```text
Device
Cloud
MediaLibrary
```

Examples:

```text
Kindle via browser filesystem
Send to Kindle
Audiobookshelf
Kobo
Generic folder
```

A Calibre-Web or CWA library is not a Family Librarian-managed `DeliveryTarget`.
It may be a linked catalog/source and CWA may be a `LibraryImport` destination;
it can also provide its own reading, download, or send features. Kindle,
browser, or email delivery remain optional, separate user-specific operations.

**Implementation status (2026-09-09):** `DeliveryTarget` is implemented
(`src/FamilyLibrarian.Domain/Delivery/DeliveryTarget.cs`) for the CWA-mediated
Send-to-Kindle path — see `docs/01-product-architecture-spec.md` §15.1 for how
its real fields differ from the sketch below. Delivery-attempt history,
retry, and receipt confirmation are implemented. A `DeliveryConfirmationStatus`
(Unconfirmed/Confirmed/ReportedMissing) is tracked on a `Submitted` attempt.
Per-user delivery intent lives on `RequestParticipant.DeliveryTargetId`,
alongside `WantsEbook`/`WantsAudiobook`. An audiobook-only join preserves any
existing ebook delivery choice; an explicit ebook choice can change it.

---

### DeliveryAttempt (implemented, 2026-09-09 — CWA Send-to-Kindle only)

Represents one attempt to deliver an Asset to a user's `DeliveryTarget`. This
is the record referred to as `Delivery` in earlier drafts of this document.
The naming collision this section originally flagged is resolved (2026-09-07):
`FamilyLibrarian.Domain.Publishing.Delivery` (one attempt to publish an
approved audiobook `MediaAsset` into Audiobookshelf — a `MediaLibraryImport`
concept, not a user-specific delivery) has been renamed to
`AudiobookshelfDelivery`, so `Delivery`/`DeliveryAttempt` were free for this
concept. It is modeled as one row per attempt (mirroring `AcquisitionJob`'s
private-setter/`TransitionTo` shape), not a single mutable row — see
`src/FamilyLibrarian.Domain/Delivery/DeliveryAttempt.cs`.

**The field list and status set below are the original forward design.** The
actual implementation is narrower — no `Method` field (only CWA-mediated
`SendToKindle` exists; `DirectDevice`/`BrowserDownload` remain unbuilt, P1-3),
`RequestId` is nullable rather than required (the existing-book fast path
creates an attempt with no request at all), and `AssetId` does not appear —
delivery targets a resolved `ExternalBookId`/`BookFormat` instead. The real
status set is `Pending -> Submitting -> Submitted/Failed/SubmissionUnknown`,
with `Pending -> Cancelled` when recipient eligibility no longer holds. None
of `Preparing`, `Ready`, `AwaitingDevice`, `Delivering`, or `SubmittedToAmazon`
below exist today.

Every logical delivery has a stable `DeliveryId`; retries keep it and create a
new row with an incremented `AttemptNumber`. Initial request releases are
unique per request/user, and attempt numbers are unique within a delivery.
Standalone sends have independent identities, so sending another book cannot
suppress an earlier book's retry. Concurrent calls reuse the winning successor;
only the worker that durably claims a Pending row may dispatch it.

Automatic retries select the latest row **before** checking Failed/retryable,
the three-attempt cap, and 2/10-minute cooldowns. A newer success, permanent
failure, unknown submission, cancellation or pending attempt suppresses all
older failures. Explicit retries can bypass the cap and cooldown, but cannot
create another successor for an already superseded row.

Before dispatch, the server freshly checks that the target is enabled and
belongs to an active account; request deliveries also require an active ebook
participant with the same target and a request that is not cancelled. Failing
this check cancels the attempt. An audiobook-only join leaves Kindle intent
unchanged.

All recipients' initial attempts are saved before the first external send.
On startup and every five minutes, the worker reconciles verified Available
library imports with unreleased eligible request intent (including late joins),
then resumes Pending attempts. No additional external readiness poll is needed.
A Submitting row older than five minutes becomes `SubmissionUnknown`. Provider
calls have a two-minute bound; uncertain outcomes after dispatch also become
`SubmissionUnknown`, and completion persistence survives browser cancellation.
Only failures known to precede dispatch are automatically retryable. Unknown
submissions require the requester or administrator to choose **Resend (may
create a duplicate)** after checking receipt; the retry API requires
`confirmPossibleDuplicate=true`. A failed completion write leaves durable work
for recovery instead of silently repeating a possibly accepted send.

Owners can list all their attempts or open one by ID through the authorized
personal delivery API, irrespective of whether `RequestId` exists. History
exposes the latest attempt ID, retry eligibility, earliest automatic retry time
and exhaustion. Receipt notifications link directly to the attempt. The admin
support projection adds recipient identity, stored title, receipt state/time and
supersession; admins may explicitly retry Submitted + ReportedMissing, while
Submitted + Confirmed is not retryable. Request withdrawal or disabled targets
still prevent dispatch through the shared eligibility check.

Delivery writes emit recipient-only Deliveries/Requests invalidation and admin
Publishing/Requests invalidation after commit. They do not use shared-request
participant fan-out. Live views reload on reconnect using their authorized APIs.

**KINDLE-7 (2026-09-09):** `Delivered`/`UserReportedMissing` above were never
built as further `DeliveryAttemptStatus` values. Instead, a `Submitted` row
carries a separate `DeliveryConfirmationStatus`
(`Unconfirmed`/`Confirmed`/`ReportedMissing`) and `ConfirmedAtUtc`, set by
`DeliveryAttempt.ConfirmReceived`/`ReportMissing` — CWA genuinely accepted the
send, so `Submitted` stays the accurate status; the user's on-device answer
is a fact layered on top of it, not a status transition. Reporting an attempt
missing does not itself retry it: an explicit retry creates a new row, and
`RetryAsync`'s eligibility was widened to accept
a `Submitted`+`ReportedMissing` row alongside an ordinary `Failed` one.
Reaching `Submitted` also raises a
`NotificationCategories.KindleDeliveryConfirmationRequested` notification
asking the user to confirm receipt. Confirming receipt or reporting it missing
dismisses that attempt's in-app prompt; reporting it missing still raises the
admin-facing delivery-needs-attention notification. Automatic retries never
resend a Submitted attempt solely because the user reported it missing.

```text
DeliveryAttemptId
RequestId
AssetId
DeliveryTargetId
Method                SendToKindle | DirectDevice | BrowserDownload | ...
Status
CreatedAt
StartedAt
CompletedAt
ProviderReference?
FailureReason?
```

Status:

```text
Pending
Preparing
Ready
AwaitingDevice
Delivering
SubmittedToAmazon
Delivered
UserReportedMissing
Failed
Cancelled
```

A request's asset may accumulate more than one `DeliveryAttempt` (for example,
a `SendToKindle` attempt the user reports as `UserReportedMissing`, followed by
a successful `DirectDevice` attempt). Retaining every attempt, rather than
overwriting one mutable status, is required to support retry/fallback and the
"didn't receive it" flow without losing history. `SubmittedToAmazon` is
deliberately distinct from `Delivered`: Family Librarian never has positive
confirmation that a Send-to-Kindle submission actually reached the device, so
it must not be represented the same way as a verified `DirectDevice` transfer.

Library artifact state and delivery state must be able to vary independently:
a `LibraryImport`/artifact can be `Available` while its most recent
`DeliveryAttempt` is `Failed` or `AwaitingDevice`, without that failure ever
moving the artifact itself back into an acquisition state.

---

### UserWorkState

Tracks user relationship to a Work independent of requests.

```text
UserId
WorkId
State
UpdatedAt
```

Possible states:

```text
Interested
Requested
Owned
Delivered
Reading
Completed
Abandoned
NotInterested
```

This record enables future recommendations and prevents repeated suggestions.

---

### UserSeriesState

```text
UserId
SeriesId
Followed
CurrentPosition?
LastCompletedWorkId?
AutoSuggestNext
NotifyNewRelease
```

---

### UserAuthorState

```text
UserId
AuthorId
Followed
NotifyNewRelease
```

---

### Recommendation

```text
RecommendationId
UserId
WorkId
ReasonType
ReasonText
Score?
CreatedAt
ExpiresAt?
Status
```

Reason types:

```text
NextInSeries
MissingEarlierSeriesBook
SameAuthor
NewAuthorRelease
NewSeriesByFollowedAuthor
AIRecommendation
Manual
```

Status:

```text
New
Viewed
Accepted
Dismissed
Expired
```

---

### NotificationBatch

```text
NotificationBatchId
UserId
ProviderId
Status
CreatedAt
SentAt?
```

Items may include:

```text
ReadyRequest
Recommendation
ApprovalRequired
NewRelease
Failure
```

---

## 4. Request Workflow

`BookRequest` captures intent. The executable workflow runs for each
`RequestFormat`, starting with a library check so an already owned format never
unnecessarily enters acquisition.

```text
Requested
  ->
CheckingLibrary
  ->
Owned? -- yes --> Available (already deliverable)
  |              or Delivering (when a target needs import/copy) --> Available
  |
  no
  v
Searching
  ->
Found
  ->
[PendingApproval when policy requires it]
  ->
Acquiring
  ->
Processing
  ->
Importing
  ->
Available
```

Exception/alternate states:

```text
NeedsIdentification
NeedsReview
AwaitingPublication
WaitingForAvailability
WaitingForSecurityScanner
AcquisitionFailed
SecurityFailed
Rejected
DeliveryFailed
Cancelled
```

An administrator approval may gate acquisition or a risky asset, but it is not
an inherent requirement of creating a request. A request should not be
represented by one uncontrolled free-text status field.

The aggregate request moves to completed history when all of its requested
formats are available, delivered where requested, or otherwise terminal.

Transitions should be explicit application commands.

The scanner gate is evaluated before any transition that accepts, downloads, or
stages file bytes. It does not block metadata/catalog checks or user request
creation, so queued requests can be backfilled after scanner recovery.

**Implementation status:** the request lifecycle actually shipped today is
still deliberately smaller than the workflow above. `RequestStatus` has five
values (`PendingAcquisition`, `NeedsReview`, `NotAvailable`, `Cancelled`, and
`Available`) and `RequestFormatStatus` has four (`Requested`, `NotAvailable`,
`Cancelled`, and `Available`). A verified CWA ebook import automatically marks
its matching requested format available; when every requested format is
available, the request moves to completed history with an explicit status
event. A background verifier performs the OPDS rechecks, so administrators do
not have to drive that normal asynchronous CWA step by hand.

**Automatic public-domain format path:** a second background worker processes
pending formats. Project Gutenberg and LibriVox are compiled-in direct-acquisition
providers, independently enabled by default, and can each be disabled in
Settings → Sources. Gutenberg searches its locally imported RDF catalogue;
LibriVox searches its live audiobook API by title. LibriVox uses one project ID
per recording and acquires the selected recording's whole-book ZIP, safely
extracts its MP3 tracks into the existing quarantine bundle pipeline, and lets
the existing audio, security, and identity validation run before approval.
Both public-domain providers use the existing acquisition and review flow; no
provider writes directly to a library destination. The Project Gutenberg source
is only eligible for unattended acquisition when it returns exactly
one result whose normalized title starts with the canonical title and whose
creator-name tokens exactly match the canonical primary author. For an ebook,
the worker then re-derives the candidate on the server, downloads it into
quarantine, runs malware and EPUB structure checks, verifies package
title/creator identity, and lets the existing approval and CWA publishing path
continue. A result that is missing, ambiguous, unavailable, or cannot be
acquired is not guessed or retried continuously.

An ambiguous or failed format can place the aggregate request in
`NeedsReview`, but it does not stop the same automatic pass from acquiring a
different requested format that has one safe candidate. For example, an
ambiguous audiobook must not prevent a clearly selected EPUB from entering the
security and publishing pipeline. The review remains scoped to its
`RequestFormat`; the request becomes fully available only when all requested
formats complete.
This also applies to an already-stored preference review: the worker skips the
format under review and continues unattended processing of another requested
format, so a historical audiobook review does not require someone to requeue
the request before its ebook can proceed.

When several records require review, Family Librarian retains every source
record for the librarian rather than collapsing them into a single blind
approval. The requester is not asked to guess among several copies. The
administrator sees the source name and record number, neutral media facts such
as format, size, and track count, and—where the provider supplies one or a
built-in catalogue has a stable public record—a link to the corresponding
browser page before choosing an acquisition.

For the built-in Gutenberg source's **ebook** path, the administrator also sees
the actual automatic-selection evidence. Download counts are a narrow,
source-owned tie breaker for choosing among several plausible text editions of
the same title — not a general quality score. An edition is selected only when
it has at least 1,000 downloads and a 3× lead over the next edition. Older
collapsed Gutenberg ebook reviews are refreshed once by the background worker:
the worker replaces the stale one-record evidence with the current complete
comparison while still leaving the request in review. **This download-count
dominance rule applies only to ebooks.** It has no role in choosing among
audiobook recordings (below) — a lightly-downloaded audiobook is not treated
as less acceptable than a popular one.

**Automatic audiobook selection:** choosing among multiple acceptable
audiobook recordings of the same requested work is a deterministic
suitability/quality decision, not a popularity contest, and multiple
same-provider candidates never force a review merely because more than one
exists. `AudiobookCandidateSelector` ranks acceptable candidates by an ordered
comparator chain — each dimension dominates every later one, never combined
into one additive score — so an earlier requirement can never be outvoted by
several weaker signals in a later one:

1. **Completeness.** A recording known to be abridged ranks behind one that
   is not known to be.
2. **Narration**, governed by the requester's own narration preference
   (`/settings/audiobook-narration`, defaulting to *Prefer human narration*
   for every account): under *Prefer human narration*, a confirmed human
   recording outranks every other candidate, but
   a synthetic (computer-generated) one remains fully eligible when no human
   recording is acceptable; under *Human narration only*, a confirmed
   synthetic recording is excluded outright, and one whose narration cannot be
   confirmed is neither accepted nor silently discarded — it is routed for
   review as genuine unresolved uncertainty; under *No narration preference*,
   narration has no effect on the outcome. For Project Gutenberg, narration is
   classified from that record's own `*readme.txt` (structured catalogue
   metadata does not distinguish a human reading from a computer-generated
   one), never guessed — an unrecognized statement is `Unknown`, a valid,
   expected result.
3. **Packaging/format preference** — the existing container/codec ranking
   below (M4B, MP3, M4A/AAC, OPUS, OGG/OGA, then FLAC), applied only as a late
   tiebreaker among otherwise-equivalent recordings, never as a stand-in for
   audio quality (a smaller M4B file is not "better" than a larger MP3 one on
   that basis alone).
4. **Popularity**, then **a stable record ID**, as the last, weak
   tiebreakers — never an acceptance requirement, and never enough by
   themselves to prefer one otherwise-equivalent recording over another for a
   requester-visible reason.

A record's own bundled codecs are resolved separately, before this
comparison: Project Gutenberg audio records commonly publish several codecs
side by side (e.g. MP3, M4B, and Ogg Vorbis for the same reading), so the
Gutenberg provider first picks that one record's own best usable format —
M4B, MP3, M4A/AAC, OPUS, OGG/OGA, then FLAC, in that order — before it is
ever compared against a *different* record. Legacy Speex (`.spx`), WAV,
AIFF/AIF, WMA, APE, and any other unrecognized audio format are ignored:
they neither auto-acquire nor create an automatic-review choice. A
cross-provider disagreement, and identity/language/release/DRM checks, are
unchanged.

This is intentionally limited to the bundled provider that explicitly opts in
to automatic acquisition. Project Gutenberg has effective `Once` behavior: each outcome
is recorded and it is not repeatedly queried. Admin-registered external
providers default to `Daily`, but an administrator may select `Manual` or
`Weekly` per enabled provider. Existing providers retain their saved schedule.
A scheduled external lookup calls the provider over its registered API URL
and records the outcome. Candidates permitted by the source's download confidence
proceed through the normal security and identity checks within the provider's
attempt limit; other reviewable candidates go to `NeedsReview`.

External sources have a download confidence level with optional ebook and audiobook
overrides. Suggest only permits discovery and review, but no unattended downloads,
including multipart audiobook sets. A librarian may explicitly choose a reviewed copy.
Strict allows strong identifier, title/author or context-corroborated
author evidence. Standard also permits close calls: exact titles with both author
components within one edit, or missing/partial authors with independent agreement.
Liberal additionally permits a plausible title with incomplete author metadata.
Fuzzy titles, contradictions and unresolved language or release concerns remain
ineligible at every level. Candidate pools are ordered by confidence tier, then
the shared external-candidate ranker; the existing retry exclusions still apply.
Complete multipart audiobooks also require every part to meet the format's
effective source confidence, both at selection and before submission. Only the
fragment concern resolved by that complete, compatible set is removed for this
comparison. Within a set, a misspelled author without independent corroboration
is a close call: Strict leaves it in ordinary review, while Standard may fetch
it. Other concerns and whole-set security and identity checks still apply.
When no individual candidate is allowed, a pool of at least two copies can form
a CloseCall if every reviewable copy has the same normalized work-title core,
no conditions or release/language concerns beyond unknown DRM, no author conflict,
and at least one has strong or fuzzy author support. Standard and Liberal may
fetch its highest-ranked copy; Strict still reviews it. A different work or a
derivative condition anywhere in that pool prevents collapse. A pool with an
already allowed strong candidate keeps its ordinary selection. Acquisition
re-proves confidence from fresh source results before submitting a job; pool
confidence does not set the file's pre-confirmed-identity flag.
New sources start at Strict. Existing sources migrate to Standard and show a
Sources-page review notice until every enabled source's confidence has been saved.

A structured author spelling difference of up to two edits per name component,
with the same first letter and component count, can be context-corroborated only
when the title is exact and series name/position, an edition ISBN-13, or publication
year and publisher independently agree with the catalog. Explicit conflicting
release-name credits remain contradictions. Content verification still compares
the downloaded copy with the catalog identity.

Preference reviews and reviews opened after that limit keep being searched.
Their wait slows from daily to monthly with age, anchored on the publication
date when it is later than the request date, otherwise on the request date;
the provider's own interval remains the minimum wait. A new eligible copy
withdraws a pending preference question with a requester notice. After a spent
budget, only a new strong candidate may get an extra attempt: at most one per
recheck and at most the provider's attempt limit in total. Failed copies and
reposts remain excluded. Requests requiring manual fulfillment are excluded,
and batches consider the least recently searched requests first.

Search proposals do not resolve a source's identity review. Ordinary saved
release-only candidates whose labels fall back to catalog context require
librarian verification; they are omitted from requester choices and the requester
acceptance endpoint refuses those selections. Explicit product questions
retain their separately verified identity and existing consent flow.

**Optional AI search guidance:** the `SearchGuidance` role is off by default.
It is considered only after deterministic variants finish for every scheduled
external source for the requested format, with no candidate eligible for
download and no product-choice question available. At most one guidance call
is made per requested format and recheck cycle. With the role disabled, no
guidance model-policy call is made.

Guidance receives sanitized requested-work metadata, at most 15 pooled result
summaries with fixed discard-reason codes, and previously tried variant keys.
It may suggest an original-language title, alternate published title, series
name, collection title or author form. At most five suggestions are accepted;
values must be new and pass the bibliographic character, length and no-link
rules. A new name is a search proposal only. Evidence IDs must have been
supplied, and a collection suggestion must cite a result. Series searches use
an existing, unambiguous catalog position. Suggestions run across scheduled
sources, and results are pooled and verified against the requested work.
Collection results still require deterministic containment proof before the
existing requester question can be offered. Source confidence, attempt limits,
consent, security and content checks continue to govern every download.

Guidance uses the background model budget, a 400-token output cap and a linked
60-second timeout, subject to installation limits. Refused, timed-out, failed,
empty, invalid or inapplicable replies leave the deterministic result intact;
caller cancellation still propagates. Call outcomes and validated proposals
are persisted as semantic findings, and identical reusable findings are reused
for seven days. A `give_up` reply ends extra searches for that cycle across
sources; the base search remains eligible next cycle. Existing candidate and
artifact semantic assessments remain advisory shadows.

**Optional AI candidate triage:** the separately enabled `CandidateTriage` role
compares up to eight candidates before an external source's automatic
submission, including complete audiobook sets. At most one model call occurs
per requested format and source pass. The server supplies bounded, sanitized
bibliographic fields and candidate-specific evidence IDs. Opaque source
references, URLs and credentials are excluded. Provider text is untrusted data.

The closed `candidate-triage/1` response allows `keep`, `uncertain`, `veto`,
`prefer` and `author_variant`. Keep and uncertain preserve the original choice.
A veto must name the current choice and cite its negative or conditional
evidence; it excludes that copy for this pass at every confidence level. Vetoing
a companion part defers that set. Preference must cite positive evidence for a
supplied member of the genuinely tied best candidates. An author variant must
have strong title support, only an author conflict, and independent matching
series and position, ISBN, or both year and publisher. It cannot override
language, title, series, edition, narration or completeness conflicts.
Preference and author variants require Standard or Liberal confidence; Strict
and SuggestOnly gain no upgrade. An author variant ranks as a close call and
cannot displace a strong copy or a better deterministic close call. This ordering
is checked again against fresh source results before submission.

Triage uses the background budget, a 300-token output cap and linked 20-second
deadline, subject to shorter installation limits. Refusal, invalid schema or
IDs, timeout, transport or persistence failure preserve deterministic behavior;
caller cancellation propagates. Validated decisions and outcomes are stored
beside the original assessment and reusable for seven days only with identical
evidence, with all move rules rechecked. Acquisition retrieves the candidate
again and refuses a changed or stale triage decision. An AI author variant
never preconfirms the downloaded file's identity. Attempt limits, product
consent, DRM, scanning, artifact identity and destination checks remain in force.

Project Gutenberg discovery reads the locally imported daily RDF catalogue, so it
continues to work when external catalogue APIs are blocked. LibriVox discovery
uses the upstream title-only search (not combined title-and-author filtering),
spaces catalog requests, and performs one bounded retry for rate limits or
transient server failures. An upstream search failure is recorded separately
from an archive download or post-download validation failure. Neither source's
optional fulfillment-options lookup may fail or delay core Work and request
detail views.

**Cancel and ask again:** reopening a cancelled request begins a fresh
acquisition cycle. Previous provider attempts remain visible in the audit
timeline, but they cannot suppress new automatic or scheduled checks for the
reopened request.

**Product-choice questions:** after the search finds no copy allowed by the
source's confidence level, a known collection containing the requested work,
an abridged version, an edition available only in another language, or an
incomplete part set can become a structured preference question. A dramatized
audiobook already held in reserve is offered only after looking for a narrated
copy. Active requesters see **Take it** and **Keep looking**; the first answer
applies to the shared request, and administrators may answer too. Searching
continues while the question is open, with one reminder after seven days. A
new eligible standalone copy withdraws the question with a notice.

Consent is recorded on the requested format and applies to the offered variant.
An accepted collection must identify the requested work in its EPUB contents or
headings when its package title differs. Abridgement and dramatization consent
suppresses only the corresponding identity concern. All other identity checks,
malware scanning, structural validation and approval rules still apply. Accepting
a held dramatization rechecks the existing bytes through the security pipeline.
An open question has amber progress and a card that says an answer is waiting.
Linked Matrix users may answer with the whole-message keywords **take**, **take
it**, **keep**, or **keep looking**. Numeric answers **1** and **2** require a
reply relation to the event recorded for that recipient's question; missing or
different event IDs never count as numeric consent. Kindle yes/no confirmations
retain priority, and unbound numbers retain their conversation meaning.

Requesters may save **Always accept collections containing my book** or an
offered language when taking that choice. Saved choices are personal and can be
removed under **Settings → Saved acquisition choices**. A shared request skips
the question only when every active requester has the matching preference; the
history says “Accepted by your saved preferences.” Otherwise only requesters
without it receive the question and may answer. Holders see a read-only card
waiting for the other requester(s); their answer attempts receive `NotAsked`
(HTTP 409). Removing a preference makes its holder eligible to answer an open
question again. Administrators may answer through the administrator request
actions. Abridged, incomplete-part and dramatized choices always require an
answer and have no “always” option. Saved consent uses the same acquisition and
validation path as **Take it**.

**Requester progress:** My Requests and the selected Work page supplement the
stable request status with one safe, per-format workflow message whenever a file has entered the pipeline:
awaiting scan, scanning, awaiting approval, security review required, needs
librarian identification, approved and publishing, awaiting destination
verification, or publishing needs attention. It intentionally excludes uploaded filenames, scan evidence,
destination failure details, and librarian-only notes.

**Administrator attention:** the application chrome exposes a persistent,
admin-only attention summary whenever one or more requests are in
`NeedsReview` or a source's latest automatic lookup failed or was blocked. The
summary links directly to Queue and Sources; Queue keeps the review count visible
instead of leaving it behind a filter, and Sources shows the latest bounded,
secret-free provider-attempt summary. The detailed request activity ledger
remains the provenance view. Requesters never receive provider IDs, transport
failures, URLs, credentials, or diagnostic details.

**Preference-review presentation:** when a provider's existing search result
includes edition or release evidence, a requester sees only neutral facts that
can make a choice meaningful: language, format, publication year, publisher,
size, part count, and abridged/unabridged status when applicable. Candidate
records that are identical on every requester-visible fact collapse to one
choice while retaining one opaque server-side handle for a later acquisition.
This presentation never triggers a per-candidate provider request, range
probe, download, or file inspection; source identity, URLs, opaque handles,
raw release names, provider-supplied title/author labels, and extension data
remain administrator/server-only. The requester-facing title and author always
come from Family Librarian's canonical catalog Work.

### External ebook source formats and DRM

External ebook candidates are classified before any source acquisition: Safe
sources are EPUB, AZW3, MOBI, and AZW; Possible sources are FB2, FBZ, KEPUB,
PRC, and DOCX; every other source format, including PDF and plain text, is
rejected. Possible sources are offered only when the provider returned no Safe
source and always require explicit review. A provider reports `none`,
`encrypted`, or `unknown` DRM evidence: encrypted files are rejected; unknown
DRM can never be auto-acquired. FL also inspects acquired MOBI-family files for
their encryption flag and never decrypts or circumvents DRM. Provider format
constraints are a quota-saving hint, not enforcement; FL independently applies
this policy before it can submit `/acquire`.

For deterministic title identity, FL compares a candidate against the Work's
canonical title and any distinct titles recorded on that same Work's catalog
editions. These edition titles are catalog-owned evidence (for example, an
original-language title), not provider labels or guessed translations. A
candidate must still pass decisive deterministic work identity, language, release,
DRM and Safe-format checks before unattended acquisition. Exact release title
phrases tolerate neutral extra descriptors; unknown author/series metadata is
not a contradiction. Work identity and typed edition/completeness conditions
are assessed separately. Author-only or unresolved title evidence remains
reviewable. See docs/03-provider-api-contracts.md for the evidence model and
complete numbered-audiobook grouping policy. EPUB package identity verification uses the
same title set after download. M4B audiobooks are verified the same way from
their embedded iTunes-style tags: the title (`nam`/`alb`) must match, and the
author/artist tags (`ART`/`aART`/`wrt`), when present, must include a match;
a file with no author tag is judged on title alone. MP3 and OGG/Opus identity
checks use embedded album and author tags; a section title alone does not
identify the book. Missing tags preserve the source's attested outcome rather
than being recorded as content verification. Formats with no verifier, such as
MOBI, remain subject to source-attestation review.

An EPUB or tagged audiobook author spelling difference may pass only with an exact title
and an independent embedded ISBN-13 or matching series name and position. Component
counts and first letters must agree, with at most two edits per component.
Only confirmed, bounded audio metadata supplies corroboration; ASIN remains
evidence only, and truncated metadata cannot fabricate agreement.
Audio strips one terminal abridged/unabridged edition marker in the supported
English or German forms before requiring an exact core title. Explicit
abridgement holds the file; German wording remains a language cue.

Catalog page counts are filled from metadata on creation or ISBN reuse only
while unknown; later values do not overwrite them. A text below 25% of the
catalog estimate (250 words per page) holds only for works of at least 80 pages.
An audio duration below 25% of a known catalog duration also holds; absent
catalog lengths remain unknown. Missing TOC targets hold only when at least two
entries and at least 10% of entries are missing. Moderate shortfalls and
inferred heading language are evidence only. These identity holds occur before
approval and publishing and remain reviewable; the security and structure gates
are unchanged.

An optional `ArtifactValidationDecision` role (off by default) can add
`identity.hold.model_concern` synchronously after those deterministic checks and
before approval. Only ExternalProvider or BuiltInProvider EPUB, KEPUB, M4B, MP3
and OGG matches are eligible. Its `artifact-decision/1` reply must cite a Negative
fact for an identity contradiction, or Negative, Weak or Condition evidence for
an incompleteness, language or edition concern. Positive-only citations,
uncertainty, release and collection-acceptance replies cannot add a hold.
Unaccepted collections and other deterministic holds never reach this model.
The held file stays Unmatched, with the model note and citations available to
administrators. Identity retry preserves a model hold; the existing administrator
identity override remains the release path. Automatic model holds use the normal
candidate exclusion and attempt limit, with `[model]` at the start of the stored
decline reason. Budget: BackgroundValidation, 400 output tokens, timeout capped
at the smaller of the installation setting and 45 seconds. Any model error or
timeout preserves the deterministic result; a disabled role makes no model call.
Validated decisions and failures are recorded, and identical reusable findings
are reused for seven days. File-derived values are stripped of control/invisible
formatting and markup delimiters, bounded, and framed as untrusted data. Cloud
roles receive those values only with the separate excerpt-sharing opt-in.

EPUB/KEPUB hold reviews can also receive three transient excerpts: the first
copyright/imprint/impressum/colophon item among the first five or last three
spine items; the first linear prose item after front matter with at least 200
words (first 200 words); and the last linear item's last 150 words. Each is
limited to 1,200 characters, text nodes only, with script/style text, controls,
invisible formatting and angle brackets removed, whitespace collapsed, and
`BOOK_TEXT` delimiter tokens neutralized. Archive reads retain the inspectors'
entry/size/compression limits, a 64 MiB aggregate read bound, 8 MiB item bound,
DTD prohibition, and cancellation. No files are written. Audio has no tier C.

The Unknown `tc-copyright`, `tc-chapter1` and `tc-last` IDs are supporting
citations only. Exact last-page sample phrases and a copyright-page `abridged`
word produce separate Condition facts. A conservative English/German function-word
cue can produce a Weak chapter-language mismatch; short, mixed and unsupported
language prose stays Unknown. A checksum-valid printed ISBN that matches a
catalog ISBN produces a Positive fact before rendering. All derived values are
fixed observations, never copied text. No deterministic approval changes from
an excerpt alone; only a grounded model concern adds the existing model hold.

Excerpts are sent only in explicitly untrusted `BOOK_TEXT` blocks. They never
enter ArtifactEvidence, SemanticFinding claims, ledger/audit, exceptions, logs or
API responses. Only a digest, safe notes and citation IDs persist. Notes remain
at most 200 characters; eight consecutive normalized excerpt words reject the
whole response. Provider failure details are replaced with a fixed message when
excerpts were sent. Cached replies are regrounded and privacy-checked again.
Role off means no extraction or calls. Cloud without `AllowCloudExcerpts` means
no extraction or text blocks. Extraction shares the existing min(timeout,45s)
deadline: failure falls back to tier A/B when time remains; deadline expiry
leaves deterministic approval unchanged. Administrators see excerpt IDs only.

`AllowModelHoldRelease` is a separate administrator-only audited permission,
default false. With it and the decision role enabled, identity verification may
request a release for attested-only external or content-inconclusive holds with
no Negative facts. Positive technical facts alone cannot prove identity. A
`release` reply must cite Positive identity evidence. The author-only exception
requires the sole detailed Negative to be author difference, plus Positive title,
language and series-position or identifier agreement, cited together by a
`supports_declared_identity` claim. The recorder's derived contradiction summary
is expanded into its underlying detailed observations; an independent Negative
summary without those details still blocks. Independent completeness conditions
that would hold also block release.

An already accepted collection held because its TOC/heading check found no
requested-work match can receive `contains_requested` citing a real tier-B TOC
or heading fact. Only its expected title difference is allowed; author, language
and every other Negative still block. This confirms contents, never requester
consent. Cloud contents review needs the existing file-detail sharing opt-in.
Tier C is restricted to hold review. Hold calls still reject release/contains_requested.

The synchronous decision proposes a persisted finding; it does not itself trust
bytes. After a fresh check following the call, `ReleaseHoldByModelAsync` checks
the persisted asset, current permission/role, current facts, bytes/profile digest,
finding/citations and passed security evaluation again under a transaction.
It moves Unmatched through Processing to Trusted with policy
`model-hold-release-v1`. `AssetIdentityReleasedByModel`
(`asset.identity_released_by_model`) records actor null, model and finding id;
identity-override evidence records source `model`. Audit/evidence and approval
commit together before publishing. A failed persistence step restores the held
file location. No release fans out to bundle siblings: each needs its own
eligible hold and grounded finding. Model-concern holds remain administrator-only.
The admin view labels applied model releases and shows the review's cited facts.

When a scheduled external acquisition starts, its durable job records the median size of other pooled, requester-reviewable candidates in the same format, provided at least three peers have a positive known size. After staging, the exact job output links that snapshot to the asset, including across restarts. A download smaller than 30% of the median produces Weak `profile.compare.size_peer_outlier` evidence. This finding never deterministically holds a file or changes acquisition eligibility; the optional artifact decision reviewer may cite it for a concern. Two peers or unknown measurements produce no finding.

When a source is set to Suggest only or the remaining candidates need a
human review, Family Librarian does not ask a requester to guess between rows
whose available evidence provides no meaningful distinction. The requester
receives a concise explanation; the administrator receives the structured
candidate evidence and, only when the provider explicitly supplied a safe
browser inspection URL, a link to inspect that source page. Provider
provenance and inspection URLs never leave administrator-authorized APIs.

The explanation is an explicit review fact, not a client-side inference from
the category name. When no candidate has identifier or strict title-and-author
corroboration, it says that possible copies were found but their titles could
not be confirmed as the requested work, and that a librarian must verify the
source before acquisition. That is an identity review, not an edition
preference. For a Suggest only source, the explanation names that policy and
says that a librarian must verify and choose a copy before downloading.

CWA receives the original approved source file and performs any library/device
conversion later. FL does not yet receive CWA's converted EPUB as an artifact,
so it cannot structurally validate that converted file; Possible formats remain
review-required until that integration exists.

There is still no general `CheckingLibrary`, `Searching`, `Acquiring`, or
`Processing` request state machine; audiobook confirmation remains future work.
Existing-ownership checks at request creation now produce a confirmable warning,
but are not yet strong enough to automatically satisfy an irreversible
fulfillment decision. This is not a documentation error to "fix" by shrinking
the target model; it reflects where implementation has reached versus where this
document says it is headed.
Expanding `RequestStatus`/`RequestFormatStatus` toward the states above is
still required before the "existing-artifact fulfillment" flows in
`docs/01-product-architecture-spec.md` can work end to end.

---

## 5. Manual V1 Workflow

```text
User requests book
      |
      v
Metadata resolution
      |
      v
PendingAcquisition
      |
      v
Admin uploads file
      |
      v
Quarantine
      |
      v
Hash + identify + scan + validate
      |
      +--> Clean + valid + matching EPUB metadata
      |                         -> policy approval -> Approved staged artifact
      |
      +--> EPUB identity mismatch/missing metadata
      |                         -> retained unmatched for librarian identification
      |
      +--> Review required     -> identity check + admin approval
      |                         -> Approved staged artifact
      |
      +--> Malware detected    -> bytes destroyed; audit record retained
      |
      +--> Invalid format      -> rejected and retained until admin deletion
      |
      v
Approved staged artifact
      |
      v
Enabled library destination
      |
      v
Verify import and remove local media copy
      |
      v
Ready / Delivered
      |
      v
Completed history
```

This workflow must use the same internal objects that automated acquisition will later use.

---

## 6. Automated Acquisition Workflow

```text
PendingAcquisition
      |
      v
Acquisition engine selects providers
      |
      +--> Required scanner unavailable
      |        --> WaitingForSecurityScanner (do not search/acquire/stage files)
      |
      +--> Search provider A
      +--> Search provider B
      +--> Search provider C
      |
      v
Candidate(s)
      |
      v
Policy/manual selection
      |
      v
Acquire
      |
      v
Quarantine Asset
      |
      v
Security workflow
```

The acquisition system does not directly deliver files.

---

## 7. Security Workflow

The administrator **Security scans** page retains recent file activity after a
file leaves the actionable queue. It defaults to 50 files, with 25 and 100
options, ordered by latest file activity. Trusted, archived, and deleted-file
records remain eligible. Each row shows the latest evaluation, its start and
completion times, individual scanner/validator timestamps, and safe actions.
The server persists a pending evaluation before scanning, then each check result
and the final evaluation. An interrupted attempt remains pending in the audit
record while a recovered quarantined file is shown as requiring a retry.

The tab-wide authenticated SignalR connection sends admin-only topic notifications
after security/file state commits. The browser fetches a fresh authorized snapshot on
notification and reconnection; it displays disconnected/reconnecting state and
provides Refresh for manual recovery. The shared hub has no client-invokable operations
and sends no filenames, identifiers, or scan details. Every snapshot and action
continues to enforce current server-side authorization; mutations retain their
anti-forgery checks. No periodic data polling is used by this page.

Personal communications settings use the same connection: a Matrix identity-link
change invalidates the owning user's communications snapshot so the page reflects
verification or unlinking without a manual reload. The hub message carries only a
topic flag; the page reloads the status through its authenticated HTTP API.

```text
Asset enters quarantine
      |
      v
Calculate SHA-256
      |
      v
Detect real file type
      |
      v
Run required malware scanners
      |
      v
Run format validators
      |
      v
Evaluate policy
      |
      +--> Failed -> Rejected/Quarantined
      |
      +--> ReviewRequired -> Admin queue
      |
      +--> Passed -> Approval policy
```

Security failures must never be overridable through an unauthenticated notification action.

Admin review should be required for risky overrides.

Scanner unavailability is not a reviewable pass or an upload queue: it blocks
file ingress. A scanner outage after ingress holds the asset in quarantine until
the required scanner succeeds or the asset is rejected.

---

## 8. Audiobook Delivery Workflow

Audiobookshelf is a delivery target. It does not define the family's ownership
state. When the user chooses **Get Audiobook**, the workflow first checks the
canonical Work's owned-audiobook availability:

```text
Published and already in Audiobookshelf -> offer Listen
Approved but not in Audiobookshelf      -> publish, verify, then offer Listen
Not yet acquired                        -> acquire/process/publish according to policy
```

Generic media-library flow:

```text
Approved staged audiobook
      |
      v
Select user's MediaLibrary target
      |
      v
Delivery provider stages file
      |
      v
Provider imports/scans
      |
      v
Provider confirms external item
      |
      v
Store provider reference
      |
      v
Ready
```

For Audiobookshelf:

```text
copy/move into configured library
trigger scan
locate imported item
record ABS library item ID
return delivery success
```

Family Librarian remains authoritative for request/history/series data.

---

## 9. E-Reader Delivery Workflow

Generic flow:

```text
Approved staged ebook
      |
      v
Target device / target method
      |
      v
Determine supported format
      |
      v
Convert/prepare if required
      |
      v
Deliver
      |
      v
Verify if possible
      |
      v
Record completion
```

Possible target methods:

```text
Browser filesystem
WebUSB where viable
Desktop agent
Send to Kindle
Kobo
Generic mass storage
```

---

## 10. Publication / Future Release Workflow

When metadata indicates the requested Work is not yet published:

```text
Request
  ->
AwaitingPublication
```

Store:

```text
ExpectedPublicationDate
MetadataLastCheckedAt
MetadataProviderReferences
```

A scheduled metadata job may later transition:

```text
AwaitingPublication
  ->
PendingAcquisition
```

This feature can be deferred while preserving the state in the model.

---

## 11. Series Intelligence Workflow

When a Work has a SeriesEntry:

1. Load the user's UserSeriesState.
2. Load completed/delivered/requested series Works.
3. Identify gaps.
4. Generate deterministic recommendations.

Examples:

```text
Requested book #5
No history for #1-4
  ->
"Book 5 of the series. Would you like to see earlier books?"
```

```text
Completed #1-4
Requested #5
  ->
Normal request; optionally suggest #6 when appropriate.
```

```text
Completed #1-8
Book #9 newly published
  ->
NewRelease recommendation
```

AI may improve wording/ranking but should not determine the factual sequence.

---

## 12. History Behavior

Completed requests must remain queryable.

Default user screen:

```text
Active
Completed
```

History should prevent:

- duplicate delivery;
- duplicate recommendations;
- loss of series progress;
- loss of author-interest signals.

---

## 13. Audit Requirements

Record significant events:

```text
RequestCreated
MetadataResolved
MetadataCorrected
AcquisitionStarted
CandidateSelected
AssetUploaded
SecurityScanStarted
SecurityScanPassed
SecurityScanFailed
AssetApproved
AssetRejected
DeliveryStarted
DeliverySucceeded
DeliveryFailed
RequestCompleted
```

The audit log should capture:

```text
Timestamp
Actor
Action
EntityType
EntityId
Summary
DetailsJson
```

---

## 14. Data Retention

Recommended default:

- keep request history indefinitely;
- keep audit events indefinitely or configurable;
- keep rejected/quarantine assets for a configurable, time-limited period;
- never expose quarantined assets to user download;
- retain checksum and provenance to detect duplicate staged imports without
  retaining a permanent Family Librarian media copy.

---

## 15. Items to Validate During Implementation

- Whether one Work can belong to multiple Series in real metadata sources.
- Handling omnibus editions.
- Audiobook dramatizations versus standard narrations.
- Multi-author works.
- Pseudonyms.
- Series numbering with novellas/prequels.
- Different regional publication dates.
- Multiple audiobook editions/narrators.
- Whether a Request should directly target a Work or optionally a specific Edition.
