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
4. Delivery destinations must be replaceable.
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

Represents a user's desire to obtain media for a Work.

```text
RequestId
UserId
WorkId
RequestedAt
Status
Priority
Notes
```

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
      -> Searching -> Found -> [PendingApproval] -> Acquiring
         -> Processing -> Importing -> Available
      -> WaitingForAvailability
```

Names can vary in implementation, but the model must preserve the distinction
between intent, ownership, acquisition, processing/import, delivery, and final
availability. A combined Ebook + Audiobook request therefore has two independent
format states rather than one opaque request state.

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

---

### AcquisitionJob

Represents an attempt to obtain media.

```text
AcquisitionJobId
RequestId
MediaType
ProviderId
EgressPolicy
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

---

### Delivery

Represents one attempt to deliver an Asset.

```text
DeliveryId
RequestId
AssetId
DeliveryTargetId
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
Delivering
Delivered
Failed
Cancelled
```

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
      v
Admin approval
      |
      v
Trusted asset
      |
      v
Delivery provider
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

---

## 8. Audiobook Delivery Workflow

Audiobookshelf is a delivery target. It does not define the family's ownership
state. When the user chooses **Get Audiobook**, the workflow first checks the
canonical Work's owned-audiobook availability:

```text
Owned and already in Audiobookshelf -> offer Listen
Owned but not in Audiobookshelf     -> import/deliver, then offer Listen
Not owned                           -> acquire/process/import according to policy
```

Generic media-library flow:

```text
Trusted audiobook Asset
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
Trusted ebook Asset
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
- keep rejected/quarantine assets for a configurable period;
- never expose quarantined assets to user download;
- optionally deduplicate trusted assets using SHA-256.

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
