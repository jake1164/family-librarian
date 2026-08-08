# Family Librarian — V1 Roadmap, Technical Spikes & Backlog

**Status:** Draft v0.1  
**Date:** 2026-08-08

---

## 1. Development Strategy

Build vertical slices that are useful before automated acquisition exists.

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
- generic OIDC configuration
- Authentik documentation

Definition of done:

A fresh Docker installation can create/log into an admin account without any external identity provider.

---

## 4. Phase 2 — Book Catalog and Search

Implement:

- Work
- Edition
- Author
- Series
- SeriesEntry
- external identifier records

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

Definition of done:

A user can type a real-world request and select the intended canonical Work.

---

## 5. Phase 3 — Requests

Implement:

- BookRequest
- RequestFormat
- request status transitions
- active requests
- completed history
- duplicate detection

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

Family can replace email requests with the web app.

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
- SHA-256 stored;
- scanner/version stored;
- errors become Hold/Review, not automatic pass.

Definition of done:

Every uploaded/acquired file travels through the same enforced security pipeline.

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

A user can request an audiobook and, after manual acquisition/admin approval, listen to it through Audiobookshelf.

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
- scoped service authentication.

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

### Milestone C — Audiobook End-to-End

- Audiobookshelf delivery;
- notifications.

### Milestone D — Reading Intelligence

- series tracking;
- authors;
- next-book suggestions;
- followed series.

### Milestone E — Automated Acquisition

- HTTP provider ecosystem.

### Milestone F — Device Delivery

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
- approval requirements;
- delivery retry/idempotency;
- provider contract tests.

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
- non-root containers where practical.

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
