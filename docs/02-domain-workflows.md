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

The reference is retained only after an identifier-first or explicitly reviewed
match. A title/author-only match remains ambiguous until confirmed. A catalog
URL is a user-facing deep-link hint, never a credential-bearing download URL.

---

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
EgressPolicy
ScannerHealthAtStart?
Status
CreatedAt
StartedAt
CompletedAt
FailureReason?
```

A request may have multiple acquisition jobs.

`EgressPolicy` is a policy selected by the provider or deployment, not a VPN
provider identity. It should support at least:

```text
NORMAL
PRIVATE_REQUIRED
CUSTOM_PROXY
```

`PRIVATE_REQUIRED` means all provider-originated external traffic must use the
configured private-egress gateway. It must fail closed when that gateway is
unavailable; the job may wait for private egress or fail, but must never fall
back silently to normal host Internet access.

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

**Implementation status:** neither `DeliveryTarget` nor the record below exists
yet. No Kindle/device delivery code has been written; see
`docs/01-product-architecture-spec.md` §15.1 for the intended design,
including delivery-attempt history, retry/fallback, and the
submitted-vs-confirmed distinction for Send-to-Kindle.

---

### DeliveryAttempt (design name — not yet implemented)

Represents one attempt to deliver an Asset to a user's `DeliveryTarget`. This
is the record referred to as `Delivery` in earlier drafts of this document;
it is named `DeliveryAttempt` here specifically to avoid colliding with the
type that already exists in code today: `FamilyLibrarian.Domain.Publishing.Delivery`,
which represents an unrelated concept — one attempt to publish an approved
audiobook `MediaAsset` into Audiobookshelf (a `MediaLibraryImport`, not a
user-specific delivery). Resolving that name collision (rename the existing
type, or pick a different name for this one) is a required decision before
this record is implemented; do not introduce a second, differently-shaped
`Delivery` type without making that choice explicitly.

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

**Automatic public-domain ebook path:** a second background worker processes
pending ebook formats. The locally indexed Project Gutenberg source is enabled by default and is only eligible for
unattended acquisition when it returns exactly one result whose normalized title
starts with the canonical title and whose creator-name tokens exactly match the
canonical primary author. The worker then re-derives the candidate on the
server, downloads it into quarantine, runs malware and EPUB structure checks,
verifies package title/creator identity, and lets the existing approval and CWA
publishing path continue. A result that is missing, ambiguous, unavailable, or
cannot be acquired moves the request to `NeedsReview`; it is never guessed or
retried continuously.

This is intentionally limited to the bundled provider that explicitly opts in
to automatic acquisition. Project Gutenberg has effective `Once` behavior: each outcome
is recorded and it is not repeatedly queried. Admin-registered external
providers default to `Manual`, but an administrator may select `Daily` or
`Weekly` per enabled provider. A scheduled external lookup follows the declared
egress policy and records the outcome; a result moves the request to
`NeedsReview` and never downloads the external artifact automatically.

Project Gutenberg discovery reads the locally imported daily RDF catalogue, so it
continues to work when external catalogue APIs are blocked. The source's mirror
download failure is recorded by the automatic request worker as an
administrator-visible provider failure. The same source's optional
fulfillment-options lookup degrades to no options, so it must
not fail or delay the core Work and request detail views.

**Cancel and ask again:** reopening a cancelled request begins a fresh
acquisition cycle. Previous provider attempts remain visible in the audit
timeline, but they cannot suppress new automatic or scheduled checks for the
reopened request.

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
      +--> Enforce provider egress policy
      |      PRIVATE_REQUIRED + gateway unavailable
      |        --> WaitingForPrivateEgress / AcquisitionFailed
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
PrivateEgressUnavailable
PrivateEgressPolicyBlocked
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
- How a deployment proves a private-egress gateway is healthy before dispatching
  a `PRIVATE_REQUIRED` acquisition job.
