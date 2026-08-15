# Family Librarian — V1 Roadmap, Technical Spikes & Backlog

**Status:** Draft v0.1  
**Date:** 2026-08-08

---

## 1. Development Strategy

Build vertical slices that are useful before automated acquisition exists.

The official product is legal-first. A bundled provider is optional, and the
default installation must be useful without a third-party provider ecosystem.
Keep metadata, ownership, availability, commercial store offers, direct legal
acquisition, library storage, and delivery as separate capabilities. Provider
enablement/configuration and provider preference are also separate concerns.

Avoid starting with:

- acquisition automation;
- device-specific transfer;
- AI recommendation logic;
- a large plugin marketplace.

Start with the family request workflow and prove the core data model.

---

## 2. Phase 0 — Technical Spikes

These should be small disposable prototypes used to answer high-risk questions.

### Spike A — Metadata and Series Accuracy

Goal:

Determine whether the proposed metadata providers can reliably resolve messy real-world family requests into:

```text
Work
Author
Series
Series position
Publication status
Edition candidates
```

Test providers:

```text
Google Books
Open Library
Hardcover
```

Test set should include:

- exact titles;
- misspelled titles;
- title + author fragments;
- middle-of-series titles;
- novellas;
- recent releases;
- upcoming releases;
- books with multiple editions;
- audiobooks with different narrators.
- owned editions that must match an external result through ISBN/provider IDs;
- ambiguous title/author matches that must remain unresolved for review.

Success criteria:

- high confidence title/author resolution;
- usable series metadata;
- known failure cases documented;
- provider merge strategy proposed.

Deliverable:

```text
docs/spikes/metadata-series-spike.md
```

---

### Spike B — Audiobookshelf Delivery

Goal:

Prove the generic media-library delivery concept.

Workflow:

```text
sample M4B
  ->
staging
  ->
configured Audiobookshelf library
  ->
trigger scan
  ->
find imported item
  ->
record external ID
```

Success criteria:

- import can be automated;
- scan can be triggered;
- imported item can be identified deterministically;
- failure conditions understood;
- deep link behavior documented.

Deliverable:

```text
docs/spikes/audiobookshelf-spike.md
```

---

### Spike C — Browser E-Reader Delivery

Goal:

Determine whether a browser/PWA can provide a practical "plug reader in and transfer" experience.

Test:

```text
Chrome
Edge
Windows
macOS
actual Kindle hardware
```

Investigate:

```text
File System Access API
WebUSB
browser permission persistence
mounted-device behavior
newer Kindle behavior
```

If available, test Kobo or a generic USB reader as well.

Success criteria:

- determine whether browser-only delivery is viable;
- identify browser/OS/device support matrix;
- determine whether desktop-agent fallback is necessary.

Deliverable:

```text
docs/spikes/browser-device-delivery.md
```

---

## 3. Phase 1 — Foundation

### Repository / solution

Target .NET 10 / C# 14. Use hosted Blazor WebAssembly with MudBlazor; the ASP.NET Core host/API owns authentication, authorization, and secrets.

Create:

```text
FamilyLibrarian.sln

src/
  FamilyLibrarian.Web
  FamilyLibrarian.Web.Client
  FamilyLibrarian.Contracts
  FamilyLibrarian.Application
  FamilyLibrarian.Domain
  FamilyLibrarian.Infrastructure

tests/
```

### Infrastructure

- Dockerfile(s)
- docker-compose.yml
- PostgreSQL
- migrations
- health checks
- structured logging
- configuration validation
- secrets strategy

### Authentication

- local Identity
- initial admin bootstrap
- User/Admin roles
- provider-neutral OIDC operational design and configuration validation
- Authentik documentation as a tested target, not a default service
- test-host identity injection so ordinary automated tests do not contact an IdP

OIDC activation follows the local request workflow rather than blocking the
foundation. When it is implemented, each development, staging, and production
environment gets a distinct client registration, secret, and callback URI. A
shared Authentik server is allowed; a shared client registration is not. Local
Identity stays available as the administrator recovery path, while the OIDC
integration remains provider-neutral.

Definition of done:

A fresh Docker installation can create/log into an admin account without any
external identity provider, and the normal test suite needs neither Authentik nor
any network OIDC service.

---

## 4. Phase 2 — Book Catalog and Search

Implement:

- Work
- Edition
- Author
- Series
- SeriesEntry
- external identifier records
- WorkFormatAvailability/search-enrichment read model

Provider contract:

```text
IBookMetadataProvider
```

Initial provider(s):

```text
Google Books
Open Library
```

UI:

```text
Search
Search results
Book detail
Series context
```

Search results are one unified family catalogue: each canonical Work displays
Ebook and Audiobook ownership/request/acquisition state independently and
offers simple format actions. The UI must not require users to choose a separate
"local library" search before searching metadata providers.

Definition of done:

A user can type a real-world request and select the intended canonical Work,
immediately seeing whether the family owns each requested format.

---

## 5. Phase 3 — Requests

Implement:

- BookRequest
- RequestFormat
- request status transitions
- active requests
- completed history
- duplicate detection
- per-format library checks before acquisition, including configured linked
  Calibre-Web catalog sources

UI:

```text
My Requests
Request Book
Request Detail
History
```

Series UX:

```text
This is book 5 of 9.
You do not have books 1-4 in your history.

[Show series]
[Request this book]
```

Definition of done:

Family can replace email requests with the web app. Creating a request records
intent; it does not require a provider decision or create an acquisition job.
Later policy may recommend an owned/available option without changing the
user-facing "Get Ebook" / "Get Audiobook" action.

---

## 6. Phase 4 — Admin Queue + Manual Acquisition

Implement:

- admin queue
- request detail
- Manual Acquisition provider
- file upload
- AcquisitionJob
- AcquisitionCandidate
- MediaAsset

Storage:

```text
quarantine
processing
rejected
trusted
```

Definition of done:

Admin can fulfill a request manually without using Calibre as the request-tracking system.

---

## 7. Phase 5 — Security Pipeline

Implement:

```text
IMalwareScanner
IAssetValidator
SecurityEvaluation
SecurityScanResult
FormatValidationResult
Approval
```

Initial scanner:

```text
ClamAV
```

Initial validators:

```text
FileTypeValidator
EpubValidator
AudioValidator
```

Rules:

- no trusted delivery before scan completion;
- no direct user access to quarantine;
- required scanner health is checked before accepting an upload, provider
  download, or linked-library staging operation;
- scanner unavailability fails closed for all file acquisition: do not accept
  file bytes into an upload queue or start/continue an acquisition job;
- users may still create requests, which enter `WaitingForSecurityScanner` and
  are backfilled through the normal workflow after recovery;
- SHA-256 stored;
- scanner/version stored;
- errors become Hold/Review, not automatic pass.

Definition of done:

Every uploaded/acquired file travels through the same enforced security pipeline.
Required scanner unavailability prevents file acquisition rather than merely
preventing delivery.

---

## 7.5 Phase 5.5 — Linked Ebook Libraries

Implement after the security pipeline is trustworthy:

- `IOwnedLibraryProvider` lookup for a configured Calibre-Web catalog via its
  server-side OPDS/catalog surface, with identifier/ISBN matching first and
  ambiguity retained for title/author fallback;
- request-authorized staging of a linked ebook into Family Librarian quarantine,
  without bulk-importing the user's existing Calibre-Web library;
- `ILibraryDestination` and `LibraryImport` state, distinct from user delivery;
- an opt-in CWA destination that copies a completed approved asset from outbound
  staging and atomically hands it to CWA's ingest directory;
- post-ingest verification of the expected work and format through the library
  catalog, idempotency/duplicate handling, retained external reference, and a
  safe retry path; and
- ready notifications containing a configured Calibre-Web/CWA deep link only
  after verification and only for users independently authorized by that
  library.

The initial CWA integration must disable CWA conversion, metadata rewriting,
EPUB fixing, and automatic e-reader send. Family Librarian retains its trusted
asset even though CWA removes its processed ingest copy. Do not write
`metadata.db` directly, automate Calibre-Web's browser upload form, or make CWA
a default Compose dependency. Plain Calibre-Web is an initial source/reading
frontend; CWA is the first automated destination.

Definition of done:

A family member can request an ebook already visible in their configured
Calibre-Web library without a bulk import, or an administrator can manually
upload and approve an ebook that is then verified in an opt-in CWA library before
the requester is notified.

---

## 7.6 Phase 5.6 — Provider Options and Policy

Implement after the manual pipeline is trustworthy:

- generic provider identity, capabilities, enable/disable, scoped configuration,
  redacted health, and audit;
- `FulfillmentOption` search enrichment that distinguishes owned copies,
  library/subscription availability, store offers, external actions, and direct
  legal acquisition;
- separately configured availability, store-offer, free-content, owned-library,
  and delivery capabilities;
- an explainable initial policy profile with per-user/media-type ordering of
  permitted providers (`Prefer`, `Deprioritize`, `Skip`); and
- legal bundled providers incrementally, only where the service interface and
  terms support the advertised action.

Do not build a generic rules language, automatic purchasing/borrowing, or an
official marketplace in this phase. A store offer is not an acquisition and an
availability result is not a file.

Definition of done:

One Work/format can show owned state and multiple permitted ways to obtain it;
the selected recommendation is explainable, providers remain independently
disableable, and no policy performs a financial or borrowing action silently.

---

## 8. Phase 6 — Audiobook Delivery

Implement generic:

```text
IDeliveryProvider
```

First media-library provider:

```text
Audiobookshelf
```

Admin configuration:

```text
server URL
credentials/token
library
filesystem mapping
test connection
```

Workflow:

```text
Approved audiobook
  ->
Audiobookshelf provider
  ->
import
  ->
scan
  ->
verify
  ->
Ready
```

Definition of done:

A user can listen to an owned audiobook through Audiobookshelf, or have an owned
but not-yet-imported audiobook delivered there. Newly acquired audiobooks follow
the same security and import path; Audiobookshelf remains a replaceable delivery
provider, not the ownership system.

---

## 9. Phase 7 — Notifications

Implement:

```text
INotificationProvider
NotificationBatch
NotificationAction
```

First provider:

```text
Email
```

Early optional provider:

```text
ntfy
```

Policies:

```text
Immediate:
  decisions / failures / security review

Batch:
  ready books / recommendations / releases
```

Definition of done:

Multiple books becoming ready result in one user notification according to policy.

---

## 10. Phase 8 — Series & Author Tracking

Implement:

```text
UserWorkState
UserSeriesState
UserAuthorState
Recommendation
```

Features:

- follow author;
- follow series;
- mark complete/read;
- identify missing earlier series items;
- suggest next book;
- avoid recommending already completed/dismissed works.

Definition of done:

Family Librarian can answer:

```text
What book is next for this user in this series?
```

without relying on an external reading tracker.

This is a primary differentiating capability, not a cosmetic add-on.

---

## 11. Phase 9 — Future Releases

Implement:

- AwaitingPublication state;
- expected publication date;
- metadata refresh jobs;
- new-release recommendations;
- followed author/series notifications.

Definition of done:

Family Librarian can retain interest in an unreleased book and surface it when appropriate.

---

## 12. Phase 10 — Automated Acquisition

Only after the manual end-to-end workflow is stable.

Implement:

- acquisition engine/service;
- provider manifest;
- HTTP provider protocol;
- provider health;
- provider capabilities;
- search;
- acquire;
- retry/policy;
- generic private-egress policy and health gate;
- fail-closed behavior for private-required providers;
- scoped service authentication.

External providers run out of process and receive only scoped credentials,
network access, and temporary staging access. They never receive the application
database, unrelated provider secrets, trusted library filesystem, or Docker
socket. The protocol is language-neutral and versioned from its first release;
it supports capability-specific search/availability, asynchronous jobs,
cancellation, and controlled staged-artifact return.

Provider repositories are a later catalog specification, not a prerequisite for
this milestone. A static signed catalog may advertise provider metadata,
source/license, capabilities, and immutable OCI image digests, but the operator
installs providers explicitly. Do not add an official marketplace, automatic
container installation/updates, or a Docker-socket mount to the main app.

Family Librarian should not require changes to its security/delivery workflow.

Definition of done:

Replacing:

```text
Admin manually uploads media
```

with:

```text
Acquisition provider supplies media
```

requires no bypass of quarantine/security/approval.

---

## 13. Phase 11 — E-Reader Delivery

Use results from the device-delivery spike.

Potential implementation order:

1. authenticated download;
2. browser filesystem;
3. Send-to-Kindle-style provider;
4. desktop device agent if needed;
5. Kobo;
6. generic mass-storage reader.

Do not encode Kindle-specific behavior in core entities.

---

## 14. Phase 12 — Recommendation / AI Enhancements

Only after deterministic book/series behavior works.

AI use cases:

- rank ambiguous metadata matches;
- improve recommendation ranking;
- explain why a book is being suggested;
- summarize series context;
- map messy natural-language input to search candidates.

AI should never become the source of truth for:

```text
ISBN
publication date
series position
canonical author
```

---

## 15. Suggested First Vertical Slice

The first production-quality implementation should be:

```text
Login
  ->
Search
  ->
Resolve Work
  ->
Show Series Context
  ->
Request
  ->
Admin Queue
```

This slice validates:

- authentication;
- Blazor UX;
- PostgreSQL;
- domain model;
- metadata integration;
- Work/Edition/Series model;
- request workflow.

It creates value before file handling is introduced.

---

## 16. Recommended Milestones

### Milestone A — Family Request Replacement

- local/OIDC auth;
- search;
- request;
- status;
- admin queue.

### Milestone B — Secure Manual Fulfillment

- file upload;
- quarantine;
- ClamAV;
- validators;
- approval;
- history.

### Milestone C — Linked Ebook Library

- Calibre-Web catalog/source;
- CWA opt-in ingest destination;
- verified ready deep-link notifications.

### Milestone D — Audiobook End-to-End

- Audiobookshelf delivery;
- notifications.

### Milestone E — Reading Intelligence

- series tracking;
- authors;
- next-book suggestions;
- followed series.

### Milestone F — Automated Acquisition

- HTTP provider ecosystem.

### Milestone G — Device Delivery

- Kindle/Kobo/browser/agent according to spike results.

---

## 17. V1 Screens

Keep the first UI small.

### User

1. Home / My Requests
2. Search / Add Book
3. Book Detail
4. Series Detail
5. History
6. User Settings

### Admin

7. Admin Queue
8. Admin Request / Asset Review
9. Integrations
10. System Settings

Some screens may share routes/components.

---

## 18. Testing Priorities

Highest-value automated tests:

- request state transitions;
- authorization;
- Work/Edition normalization;
- series-gap detection;
- duplicate request logic;
- quarantine enforcement;
- malware scanner failure behavior;
- required-scanner outage blocks uploads, provider downloads, and linked-library
  staging while preserving and later resuming user requests;
- approval requirements;
- delivery retry/idempotency;
- provider contract tests; and
- private-egress fail-closed behavior and no-fallback tests for private providers.

---

## 19. Security Backlog

- CSRF protection where applicable;
- secure cookies;
- OIDC state/nonce validation;
- least-privilege service tokens;
- secret encryption/storage;
- MIME sniffing;
- upload limits;
- zip-bomb protections for EPUB;
- path traversal protections;
- archive extraction sandboxing;
- rate limiting;
- action-token replay protection;
- audit logging;
- dependency/container scanning;
- non-root containers where practical; and
- private acquisition gateway health checks and auditable fail-closed policy
  enforcement; keep VPN tunnel privileges out of the main application container.

---

## 20. Decision Gates

Before starting automated acquisition:

- manual fulfillment must be stable;
- security pipeline must be enforced;
- domain model must survive real usage.

Before writing a desktop device agent:

- browser device-delivery spike must fail or have unacceptable limitations.

Before writing custom audiobook streaming:

- confirm Audiobookshelf cannot meet the requirement.

Before adding a new programming language:

- confirm it solves a concrete ecosystem/runtime problem better than .NET.
