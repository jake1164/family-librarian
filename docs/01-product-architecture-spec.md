# Family Librarian — Product & Architecture Specification

**Status:** Draft v0.1  
**Date:** 2026-08-08  
**Purpose:** Define the product, scope, architecture, and deployment model for a self-hosted family book request, tracking, acquisition-orchestration, security, and delivery platform.

---

## 1. Problem Statement

The current manual workflow is:

1. Family emails a list of books they want.
2. The administrator searches to identify the exact intended books because requests are often ambiguous or incomplete.
3. The administrator sources the requested Kindle ebook(s).
4. While sourcing, the administrator looks for:
   - related books in the same series;
   - other books by the same author that may be of interest.
5. The ebook is copied into Calibre.
6. Metadata is updated in Calibre, including description, author, and other details.
7. The ebook is transferred to Kindle using Calibre.

The proposed system should replace as much of this workflow as practical while improving book discovery, series tracking, user notifications, security, audiobook support, and delivery.

---

## 2. Product Vision

Create a self-hosted, open-source, multi-user web application that allows family members to:

- sign in;
- search for books accurately;
- request ebooks, audiobooks, or both;
- see the status of their requests;
- receive recommendations related to series and authors;
- receive batched notifications when requested media is ready;
- receive or transfer approved media to supported reading/listening targets.

The system should also help solve a broader problem: remembering what users are reading, where they are in a series, which authors they follow, and when new books become available.

---

## 3. Core Product Principles

### 3.1 Open-source core

The core application must be usable without proprietary infrastructure.

A minimal installation should support:

- local user accounts;
- public metadata providers;
- manual acquisition;
- local malware scanning;
- email notifications;
- manual or generic delivery.

Optional providers may add Authentik, commercial malware scanning, push notifications, automated acquisition, audiobook servers, and e-reader integrations.

### 3.2 Standards over vendor lock-in

Where possible, integrations should use standard protocols.

Examples:

- OIDC/OAuth 2.0 for authentication;
- REST/HTTP for acquisition providers;
- provider interfaces for metadata, security, notifications, and delivery.

### 3.3 Core owns workflow; integrations provide capabilities

The application should own:

- users;
- book/work identity;
- authors;
- series;
- editions;
- requests;
- history;
- approval;
- security state;
- delivery state;
- recommendations/following.

Family Librarian is not a permanent ebook or audiobook store. It may hold media
only in short-lived quarantine, processing, and outbound-staging areas while it
checks and publishes an artifact. The enabled external library destination is
the permanent media store after verified import. Family Librarian retains the
request/history, bibliographic metadata, checksum, security/approval evidence,
and opaque destination reference needed to prove what happened, not a second
media copy.

External tools should remain replaceable. CWA and Audiobookshelf are the initial
destinations, not core assumptions: a future destination may support either or
both media types without adding vendor-specific fields to Work, Request, or
asset-provenance records.

### 3.4 Manual workflow remains valid

Automated acquisition is not required for the first useful release.

A manual provider should allow the administrator to upload a file into the same pipeline used later by automated acquisition.

---

## 4. Primary Users

### Regular User

Can:

- sign in;
- search books;
- request ebook, audiobook, or both;
- view active request status;
- view completed/history items;
- follow authors and series;
- accept or decline recommendations;
- manage notification preferences;
- manage permitted delivery targets.

### Administrator

Can additionally:

- view all requests;
- correct metadata;
- resolve ambiguous book matches;
- upload manually acquired assets;
- review acquisition candidates;
- review security results;
- approve/reject assets;
- configure providers and integrations;
- manage users/roles;
- inspect failed jobs;
- manage system settings.

---

## 5. Functional Scope

### 5.1 V1 Required Features

#### Authentication

- Local authentication must work out of the box.
- Generic OIDC support is optional and follows the proven local request loop;
  it must not block local development, self-hosting, or ordinary tests.
- Authentik should be a documented and opt-in tested OIDC configuration.
- Roles:
  - User
  - Admin

#### Book Search and Identification

- Search by title, author, ISBN, and loose user-entered text.
- Query one or more external metadata providers.
- Normalize results into the application's internal model.
- Present one unified search surface: users must not have to choose between a
  search of the family's catalogue and a search for a new title.
- Before rendering a result, match its canonical Work and editions against the
  local catalogue and active request/acquisition records. Each result must
  distinguish Ebook and Audiobook availability independently, including owned,
  requested, waiting, acquiring/processing, and deliverable states.
- Display:
  - cover;
  - title;
  - author;
  - description;
  - publication date;
  - series;
  - series position;
  - clear Ebook and Audiobook indicators;
  - the appropriate simple action for each format: for example, **Get Ebook**,
    **Get Audiobook**, **Read**, **Listen**, or **Send to device**.
- Allow admin correction when provider data is wrong or ambiguous.

The family catalogue is a catalogue of Works, not separate search-result rows
for each edition or media type. A Work may have an owned Ebook while its
Audiobook is still being acquired. Ownership is distinct from delivery: an owned
asset might not yet be imported into the user's configured library or device.

#### Requests

Users can request:

- Ebook
- Audiobook
- Both

Users can view active requests and status.

Completed items should not be deleted; they should move to History.

A request records a user's intent to obtain one Work in one format. It does not
by itself imply manual administrator approval: policy may fulfill an already
owned or automatically acquirable format immediately. The user-facing UI should
not expose provider selection, indexer search, or release selection unless an
administrator is resolving an exception.

#### Series Intelligence

When a user requests a book in a series:

- show series name and position;
- identify previous/next books where known;
- warn when the requested title appears to be in the middle of a series;
- offer related series books;
- allow following a series.

#### Author Tracking

- Associate books with authors.
- Allow a user to follow an author.
- Store enough information to support future new-release monitoring.

#### Manual Acquisition

- Admin can attach/upload an acquired ebook or audiobook to a request.
- The upload is bound to the request's `Ebook` or `Audiobook` format; arbitrary
  files and cross-media uploads are rejected.
- An extension and declared MIME allowlist are only early filters. Content-type
  inspection and a media-specific validator must confirm that an ebook is an
  approved ebook format and an audiobook is an approved audiobook format before
  it can be published.
- The file enters quarantine, never a permanent Family Librarian library.

#### Linked Ebook Libraries

- A configured **Calibre-Web** instance is an optional linked ebook-library
  source. Its existing library is queried as part of the normal Ebook request
  flow; it is never bulk-imported into Family Librarian.
- A linked-library match means the ebook is available to stage for the request;
  it is not a trusted Family Librarian asset until the selected file completes
  the same quarantine, validation, malware-scanning, and approval pipeline as a
  manual upload.
- **Calibre-Web Automated (CWA)** is the first opt-in automated ebook-library
  destination and permanent ebook store. After approval, Family Librarian copies
  the staged artifact into a completed outbound staging file and atomically hands
  it to CWA's configured ingest directory. It verifies the imported book through
  the library's normal catalog surface before reporting it ready, then removes
  its staging/outbound copies according to retention policy.
- Plain Calibre-Web is initially a linked-library source and user-facing reading
  surface, not an assumed automated write API. Directly writing its database or
  automating its browser upload form is out of scope.
- A library destination is separate from an optional Family Librarian-managed,
  user-specific delivery target. CWA/Calibre-Web may make a book browsable,
  readable, downloadable, or independently sendable to an e-reader through its
  own features; Family Librarian does not duplicate that capability by retaining
  a permanent file.

#### Security Gate

Every acquired asset must pass:

- checksum generation;
- file type detection;
- format validation;
- malware scanning;
- approval policy.

No acquired file may enter a trusted delivery target before passing the security gate.
When malware is confirmed, the staged bytes are destroyed immediately and only
the security/audit evidence remains. A failed format check is retained in the
administrator security queue until it is explicitly deleted, so a bad artifact
can be reviewed without treating it as trusted content.
If a required malware scanner is unavailable or unhealthy, Family Librarian must
fail closed: it must not accept uploads, start provider downloads, or stage a
linked-library file. Existing user requests remain recorded and move to a
scanner-waiting state for backfill once scanner health is restored; catalog
search and request creation continue to work.

#### Audiobook Delivery

- Audiobookshelf is the first supported audiobook-library destination and
  permanent audiobook store.
- It must be implemented behind a generic library/delivery interface. Future
  destinations may support ebook, audiobook, or both.
- Audiobookshelf is not the authority for Family Librarian's request or
  bibliographic history. A verified destination reference establishes that the
  particular artifact was published there; the library supplies listening.

#### Ebook Delivery

V1 may support:

- authenticated download;
- manual transfer;
- optional Send-to-Kindle-style provider if practical.

Browser/PWA device delivery should be prototyped separately before becoming a hard dependency.

#### Notifications

- Batched notification model.
- Email provider initially.
- Pluggable notification providers.
- Support future actionable push notifications.

---

## 6. Stretch Goals

- Automated acquisition providers.
- Multiple acquisition providers with priority/policy.
- Future publication queue.
- New release monitoring for followed authors/series.
- Recommendation ranking using local AI.
- PWA/browser-based USB/filesystem e-reader transfer.
- Desktop device agent fallback.
- Kobo, PocketBook, generic EPUB device support.
- Alternative ebook, audiobook, or combined media-library servers.
- Pushover, ntfy, web push, Home Assistant, Discord, or other notification providers.
- Multiple malware engines and policy-based aggregate verdicts.
- Device-aware format conversion.
- User reading progress and "next in series" recommendations.

---

## 7. Recommended Technology Stack

### Core

- .NET 10 / C# 14 / ASP.NET Core
- Hosted Blazor WebAssembly with MudBlazor
- Entity Framework Core
- PostgreSQL

### Deployment

- Docker / Docker Compose
- Reverse proxy compatible
- HTTPS required for any external deployment and browser APIs

### Optional Supporting Services

- ClamAV
- VPN/private-egress gateway (Gluetun is the documented reference implementation)
- Audiobookshelf
- Calibre-Web or Calibre-Web Automated (CWA)
- Authentik
- ntfy
- SMTP provider
- LiteLLM/local LLM endpoint
- acquisition provider containers

---

## 8. Application Architecture

Start as a modular monolith.

Suggested solution structure:

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
  FamilyLibrarian.Domain.Tests
  FamilyLibrarian.Application.Tests
  FamilyLibrarian.IntegrationTests
```

The default runtime deployment contains only:

```text
family-librarian
postgres
```

Add services only when their planned slice requires them:

```text
family-librarian-worker
clamav
audiobookshelf
cwa
ntfy
acquisition-provider-*
vpn-gateway
```

---

## 9. High-Level Architecture

```text
                  Optional Identity Provider
             Generic OIDC / Authentik target
                            |
                            v
                  +------------------+
                  |    Family Librarian    |
                  | Blazor + API     |
                  +---------+--------+
                            |
                  +---------+----------+
                            |
                            v
                       PostgreSQL
```

Future slices may add a worker and optional metadata, acquisition, security, and
delivery providers behind the same application boundary.

---

## 10. Authentication Architecture

Authentication must not require Authentik.

Production modes are deliberately limited to:

```text
Local
Local + OIDC
```

Local Identity remains enabled in both modes as the first-start and recovery
path. Do not offer a deployable OIDC-only mode until a separately designed,
tested recovery mechanism proves that an IdP outage or bad registration cannot
permanently lock out the household administrator. A test-authentication scheme
may exist only in the test host/development composition; it must not be enabled
through ordinary deployment configuration or exposed by a production image.

OIDC is provider-agnostic and should be generic enough for:

- Authentik
- Keycloak
- Entra ID
- Okta
- other standards-compliant providers

Authentik is a recommended and tested target, not a dependency or source of
bespoke domain behavior. Use the standard ASP.NET Core confidential OIDC code
flow with PKCE, backed by the host's cookie session. The WebAssembly client does
not receive an OIDC client secret or perform authorization decisions.

Application authorization and role names remain internal. Map validated claims
from the configured issuer to Family Librarian roles using an explicit
allowlist; do not create Authentik-specific roles or grant administrative access
merely because a provider emitted an unreviewed claim.

External claims/groups may map to:

```text
User
Admin
```

Local administrator bootstrap must be available for first startup.

### OIDC environment and operational contract

Each development, staging, and production deployment should use a distinct OIDC
client registration, client ID/secret, and redirect/sign-out callback URI. They
may share one Authentik (or other) server, but must not share a registration:
separate registrations prevent a development callback or secret rotation from
changing production behavior.

The configured issuer/authority must be the canonical issuer reachable by both
the browser and the Family Librarian host/container. The browser needs the
public application callback URI; the host/container must also be able to fetch
OIDC discovery, token, and signing-key/JWKS endpoints. Split-horizon DNS or a
reverse proxy is acceptable only when both views resolve the same issuer safely.

Client secrets are deployment secrets. Keep them in an environment-specific
secret store or development user-secret/.env mechanism, never in source,
checked-in appsettings, browser configuration, logs, audit events, or API
responses. Configure redirect and sign-out callback paths in the OIDC client
registration as well as the application.

Developers do not need a personal Authentik deployment: local Identity is enough
for ordinary development. An OIDC contributor may use a shared development
client, a disposable IdP, or the isolated OIDC test environment. The normal unit,
application, and web/API test suite must inject controlled user/admin identities
and must not require Authentik, a network IdP, or OIDC credentials. A separate,
opt-in integration test may use a disposable standards-compliant provider
(Authentik in Docker is an acceptable target) to exercise discovery, redirect,
callback, external-login linking, and claim mapping.

---

## 11. Metadata Strategy

The application should not depend on one external catalog.

Potential metadata providers:

- Google Books
- Open Library
- Hardcover

The system should expose an internal provider abstraction such as:

```csharp
IBookMetadataProvider
```

The application database becomes authoritative after normalization.

Avoid using external provider IDs as the sole identity of a Work.

---

## 12. Acquisition Architecture

Acquisition is separate from the main Family Librarian application.

The acquisition engine may:

- be a separate project/repository;
- expose a REST API;
- host multiple providers;
- use providers implemented in any language;
- run providers in separate Docker containers.

Example provider categories:

- Manual upload
- Local folder
- Library availability
- Public domain
- Commercial provider
- custom external provider

Provider capabilities should declare whether the provider supports:

- ebook;
- audiobook;
- search;
- availability lookup;
- automated acquisition;
- account/API credentials.

The core application should not grant acquisition providers database access.

Future external sourcing integrations should be managed as configured, versioned
acquisition providers. Administrators can manage approved installed providers and
their server-side configuration, while search and acquisition preserve provenance
and still pass through the normal authorization, audit, and file-safety workflow.
This deliberately leaves room for future sources without making arbitrary plugins
or automated acquisition part of V1.

---

### 12.1 Linked ebook-library integration

Family Librarian separates a linked library's three roles:

```text
Library catalog/source     find an existing ebook
Library destination        publish an approved ebook to a library
Reader delivery            make an ebook available on a user's Kindle/device
```

The first supported catalog/source is Calibre-Web. Its server-side configured
OPDS/download surface is used with a least-privilege account; provider
credentials and library download URLs are never returned to the browser. A
canonical Work/Edition is matched using retained identifiers and ISBN first,
with title/author matching retained as an ambiguity-aware fallback. A match is
displayed as available in the linked library, not as an already-trusted local
asset.

The first automated ebook-library destination is CWA's ingest directory. The
destination contract receives an approved staged artifact. Family Librarian
copies it outside the watched directory and atomically moves/renames the completed
copy into the configured CWA ingest directory. It then verifies the expected
book and format are visible before the request becomes ready, records the
external library reference, and removes local media copies after the configured
short retention period. A retry must not create an unreviewed duplicate.

CWA's optional post-ingest conversion, metadata rewriting, EPUB fixing, and
automatic e-reader send features are disabled for the initial integration.
They can be supported only when their completed output and status can be
verified under the Family Librarian security and notification workflow. CWA's
managed Calibre library, rather than its transient ingest folder, is the
permanent ebook store and must be backed up accordingly.

Plain Calibre-Web remains supported as a source and reading frontend. It is not
treated as an automated destination until it exposes a supported, versioned
write mechanism. Family Librarian never writes `metadata.db` directly and never
depends on browser-form automation.

Calibre-Web/CWA user accounts and permissions remain their own authorization
boundary. A Family Librarian ready notification may contain a deep link only
after the user can independently authenticate to the linked library; it must not
leak a service account's credentials or grant access through a shared URL.

#### 12.1.1 CWA connectivity is independent of ingest transport

CWA integration has two independent concerns that must never collapse into one:

```text
CWA catalog/query connection    one HTTP(S)/OPDS endpoint + account, used for
                                 ownership lookup, post-ingest correlation, and
                                 (once implemented) canonical artifact retrieval

CWA ingest transport            how a newly acquired, approved file is handed to
                                 CWA's watched ingest folder: local/shared
                                 filesystem, or SFTP to a remote host
```

Whether CWA runs on the same host as Family Librarian (a shared filesystem is
available) or on separate hardware (a NAS, a remote server) only changes which
ingest transport is configured. It never changes how Family Librarian talks to
CWA's catalog: every ownership check, every post-ingest verification, and every
future artifact retrieval goes over the same OPDS/HTTP connection in both
topologies. A shared filesystem is at most an optimization for the ingest
handoff itself, never a substitute for the catalog connection, and SFTP
directory contents are never read as evidence of what CWA owns.

```text
Local CWA                                Remote CWA
  FL --shared filesystem--> ingest          FL --SFTP--> ingest
  FL --HTTP(S)/OPDS-------> catalog         FL --HTTP(S)/OPDS-------> catalog
```

This is already how the implementation is built: `CwaSettings` holds one
`OpdsBaseUrl`/OPDS credential pair alongside an independently selectable
`TransportMode` (`Local` or `Sftp`), `ICwaCatalogClient` performs only OPDS
reads, and `ICwaIngestTransport`/`ICwaIngestTransportFactory` perform only the
file handoff. Neither depends on the other. This document is being updated to
make that separation an explicit, documented requirement rather than an
implicit consequence of how the settings happened to be modeled.

**Known gap:** the OPDS connection is currently optional even when CWA is
enabled — `CwaSettingsService` only requires ingest-transport fields before
allowing `IsEnabled = true`. An administrator can enable CWA with a working
ingest transport and no OPDS URL configured at all, in which case ownership
lookup (`CwaOwnedLibraryProvider`) and post-ingest correlation
(`CwaPublishingService`) both silently return "not found" forever, and new
requests will keep re-acquiring and re-ingesting books CWA already has. Since
this document requires HTTP(S)/OPDS catalog access in every CWA deployment,
enabling CWA without a working OPDS connection should be treated as a
misconfiguration, not a supported ingest-only mode.

**Known gap:** there is currently no way to retrieve an existing artifact's
bytes back out of CWA. `ICwaCatalogClient.FindBookIdAsync` resolves only a
book ID (used for ownership display and post-ingest verification); nothing in
the CWA integration can fetch the underlying EPUB. This blocks every flow that
needs to act on an already-owned book — direct-device transfer, browser
download, or Send-to-Kindle from an existing copy — for both local and remote
CWA. Local CWA read access to the Calibre library's files could serve as an
optimization once this exists, but the primary mechanism should be an
OPDS/HTTP download so remote CWA works identically without SFTP. See
§15 (Delivery Architecture) and `docs/03-provider-api-contracts.md` §4
("Linked ebook-library providers") for the proposed contract shape.

### 12.2 Private acquisition egress

Family Librarian **SHALL NOT** depend on a specific commercial VPN provider.
Private acquisition networking is optional and configured at the deployment
layer through a generic VPN/private-egress gateway. The application must not
contain provider-specific logic for Proton VPN, Mullvad, PIA, NordVPN,
Surfshark, IVPN, or any other commercial VPN service.

The application has two distinct traffic paths:

```text
Family Librarian
  +--> normal traffic --> LAN / normal Internet
  |      metadata, OIDC/authentication, notifications, Audiobookshelf,
  |      BookLore, and other LAN services
  |
  +--> private acquisition traffic --> VPN/private-egress gateway
                                         --> selected VPN provider / Internet
```

The main Family Librarian container does not need to run entirely inside the
VPN. A provider that is configured to require private egress must route its
complete interaction through the configured gateway: authentication, search,
result and detail retrieval, artifact and download-URL resolution, and the
artifact download itself. It must never protect only the final file transfer.

Supported integration mechanisms are gateway-neutral:

- HTTP proxy;
- SOCKS5 proxy;
- deployment-provided container/network-namespace routing; or
- router-level or custom WireGuard/OpenVPN gateway routing.

Gluetun is the documented reference implementation because it can provide a
VPN tunnel, DNS and leak handling, firewall/kill-switch behavior, HTTP and
SOCKS5 proxies, and shared Docker network namespaces. It is not a dependency:
another gateway with one of the mechanisms above is equally valid. Selecting or
switching the VPN provider is a gateway/deployment concern and must not require
a Family Librarian application release. A gateway that supports custom
WireGuard or OpenVPN configuration also provides an escape hatch for VPN
providers outside its built-in provider list.

Private-required work must fail closed. If the selected gateway is unavailable
or cannot be verified, acquisition must be blocked and represented as a waiting
or failed operation (for example, `WAITING_FOR_PRIVATE_EGRESS` or
`PRIVATE_EGRESS_UNAVAILABLE`); it must not silently retry through normal
Internet egress.

The component that makes an external request owns its privacy boundary. When
Family Librarian calls an isolated provider over an internal API, that provider's
own authentication, discovery, artifact resolution, and transfer requests still
need the required private egress. Routing only the main application through a
VPN does not protect those separate containers.

Preferred deployment examples are:

```text
No VPN
  Family Librarian --> normal Internet
  Suitable for metadata, public-domain sources, manual imports, and
  legitimate store integrations.

Private acquisition gateway
  Family Librarian --> normal Internet / LAN
  private in-process provider --> HTTP or SOCKS5 gateway --> VPN --> Internet

Private acquisition stack
  Family Librarian --> isolated provider internal API
  isolated provider --> VPN gateway --> Internet
```

For Docker deployments, the last model commonly places the external acquisition
container in the gateway's shared namespace (for example,
`network_mode: "service:gluetun"`). The gateway then governs that container's
egress using its own firewall/kill switch, while Family Librarian can still use
an internal Docker/LAN API path. Keep tunnel privileges isolated to the gateway:

```text
family-librarian: normal container privileges
vpn-gateway:      NET_ADMIN, /dev/net/tun, and VPN-specific privileges
```

Family Librarian itself must not require `NET_ADMIN`, `NET_RAW`, privileged
mode, or direct WireGuard/OpenVPN tunnel management. Future higher-risk or
untrusted providers may run out of process behind the same gateway boundary;
that is an available isolation direction, not a V1 VPN prerequisite.

### 12.3 Provider, availability, and policy model

Family Librarian is a legal-first discovery, ownership, acquisition, and
delivery platform. A useful default installation must not depend on external
community providers: metadata, manual import, legitimate free content,
owned-library detection, and store/library discovery are all independent
capabilities. Official providers remain individually enableable or disableable.

The core distinguishes metadata, availability, store offers, direct legal
acquisition, owned libraries, library backends, and delivery. A unified Work
result combines the permitted outcomes as format-specific options so users can
ask “How can I get this?” without needing separate local-library, store, or
library-service searches. An offer or library availability is not an asset, and
an owned asset is not necessarily delivered to a target.

Provider configuration answers whether an installed capability may run and for
whom; policy answers which permitted option is preferred for a user and media
type. Start with explainable profiles and simple ordering only after multiple
real options exist. More complex rules (price/wait limits, author/series/title
overrides, or automatic actions) require separate evidence and authorization
design. A recommendation never silently completes a purchase, borrow, or
download.

External providers use a versioned, language-neutral protocol and are isolated
from the core database, unrelated credentials, destination-library storage, and
Docker socket. Every artifact returns to Family Librarian-controlled staging
before the security pipeline publishes it to an enabled destination. Provider
repositories are future catalogs of provenance and immutable OCI image
identities, not an initial marketplace or automatic container-management system.

---

## 13. Security Architecture

All acquired media begins untrusted.

Suggested storage zones:

```text
/data/family-librarian/
  quarantine/
  processing/
  outbound/
```

These are transient working areas, not a library. Rejected content may be kept
in quarantine only for an explicit, time-limited investigation/cleanup policy;
approved content is deleted locally after the destination verifies import.

Minimum pipeline:

```text
Acquire
  ->
Quarantine
  ->
Hash / identify file type
  ->
Malware scan
  ->
Format validation
  ->
Metadata/content verification
  ->
Automatic approval when every required check passes; administrator review only for exceptions
  ->
Approved staged artifact
  ->
Publish to enabled library destination
  ->
Verify destination reference
  ->
Remove local media copy
```

Malware scanning must be provider-based.

Initial implementation:

```text
ClamAV
```

Future implementations might include commercial or multi-engine services.

The security policy should support:

- required scanners;
- optional scanners;
- all/any pass policy;
- scanner unavailable behavior;
- detected threat behavior.

For any deployment that enables manual or automated file acquisition, required
scanner unavailability blocks the whole acquisition boundary—not merely trusted
asset approval. The host checks scanner readiness before accepting an upload or
starting a download/stage operation. If scanner health changes during transfer,
the incomplete or completed file remains quarantined and the request waits for
scanner recovery; it must not be retried through an unscanned path.

### 13.1 Baseline hardening requirements

Beyond the acquisition pipeline above, the application must provide:

- anti-forgery protection for all cookie-authenticated state changes, and
  OIDC state/nonce validation for the federated sign-in path;
- secure cookie flags (`HttpOnly`, `Secure`, `SameSite`) for the authentication
  session;
- least-privilege, narrowly scoped service tokens for service-to-service and
  external-provider calls, and encrypted at-rest storage for provider secrets;
- upload size limits, MIME sniffing/content-type verification, and zip-bomb and
  path-traversal protection for archive-based formats (EPUB), with any archive
  extraction sandboxed away from the trusted filesystem;
- rate limiting and replay protection on unauthenticated or action-token
  endpoints (for example, invitation redemption and notification actions);
- audit logging for authorization-sensitive and status-transition events;
- dependency and container image scanning in the build pipeline, and non-root
  containers where the base image and tooling support it.

---

## 14. Notification Architecture

Notifications should use a provider abstraction.

Candidate providers:

- Email
- ntfy
- Pushover
- Web Push
- Home Assistant
- Discord
- others

Notifications fall into two categories:

### Immediate

- user decision required;
- admin/security attention;
- failed workflow requiring intervention.

### Batched

- books ready;
- recommendations;
- new author/series releases;
- routine status updates.

Actionable notification support should use short-lived, scoped, single-use action tokens rather than permanent API credentials.

---

## 15. Delivery Architecture

Delivery must be generic.

Suggested top-level contract:

```text
IDeliveryProvider
```

Provider families may include:

```text
Device
Cloud
MediaLibrary
```

Examples:

```text
Device:
  Kindle USB
  Kobo USB
  Generic Mass Storage

Cloud:
  Send to Kindle

MediaLibrary:
  Audiobookshelf
  future alternatives

Ebook library frontend:
  Calibre-Web
  Calibre-Web Automated (CWA)
```

Audiobookshelf should be the first audiobook delivery provider.

Kindle should be the first e-reader target but must not be hardcoded into the domain model.

### 15.1 Kindle/e-reader delivery model (forward design, not yet implemented)

No delivery/device code exists in the repository today: there is no
`DeliveryTarget`, no `IDeliveryProvider` implementation, no Send-to-Kindle or
direct-device transfer, and no artifact-retrieval path. `LibraryImport` and
`Delivery` currently only track publishing an approved `MediaAsset` into CWA
or Audiobookshelf; neither represents delivering a book to a specific user's
device. This section records the intended design so that when device delivery
work starts (`post-v1-roadmap.md`'s Milestone G), it has a documented target
rather than being designed from scratch against a vague spec.

**Destination vs. method.** A user's e-reader (for example, "Jason's Kindle
Paperwhite") is a `DeliveryTarget`. Getting a book to it can use more than one
method:

```text
SendToKindle       Amazon's Send-to-Kindle email/API path
DirectDevice        browser-mediated transfer (WebUSB/File System Access, once
                     Spike C determines viability) or a desktop-agent fallback
BrowserDownload      authenticated manual download
```

These methods are not separate destinations, and CWA/SFTP/local-folder ingest
are not delivery methods at all — they remain purely how a file reaches the
library backend, described in §12.1.1.

**Preference and fallback.** A user should be able to configure a preferred
method and a fallback per destination (for example, prefer `SendToKindle`,
fall back to `DirectDevice`). A request may override the default for one
delivery. `Automatic` means "apply the user's configured policy."

**Submitted vs. confirmed.** Amazon's Send-to-Kindle path gives Family
Librarian a submission acknowledgement, never proof the book appeared on the
device. The domain must keep these distinct — `SubmittedToAmazon` is not
`Delivered`/`ConfirmedOnDevice` — so a silent Amazon failure can be recognized
and offered a fallback (`Didn't receive it` -> resend or direct transfer)
without the request needing to be recreated.

**Delivery attempts, not one mutable status.** A request/asset may accumulate
more than one delivery attempt (an `Amazon` submission the user never
received, then a successful `DirectDevice` transfer). The design must retain
each attempt's method, timing, status, and failure reason rather than
overwriting a single delivery field, mirroring how `AcquisitionJob` already
allows a request to accumulate more than one acquisition attempt.

**Existing-artifact fast path.** When a search shows a book as already owned
(today, via `FulfillmentOption`/`CwaOwnedLibraryProvider`), choosing a
delivery method must be able to skip acquisition, scanning, and CWA ingest
entirely and go straight to retrieving the canonical artifact and delivering
it. This requires the artifact-retrieval capability noted as a gap in §12.1.1.

**Device presence changes offered choices, not acquisition.** If a browser
detects a connected Kindle while a book is still being acquired, the request
should be able to reach an `AwaitingDevice` state once the artifact is ready,
and complete a direct transfer later without the user repeating the request.
Device connectivity must never be coupled to acquisition duration or a live
browser session.

**Naming conflict to resolve before implementation.** `Domain.Publishing.Delivery`
already exists and means "one attempt to publish an approved audiobook
`MediaAsset` to Audiobookshelf" (`DeliveryStatus`: `Uploading`/`Verifying`/
`Delivered`/`Failed`, one row per asset). That is a `MediaLibraryImport`-style
concept (moving a file into a shared library), not the user/device-specific
delivery-attempt concept described above. Reusing the name `Delivery` for the
new, unrelated user-facing concept would collide with the existing type and
its `DeliveryResponse`/`DeliveryView`/`IDeliveryRepository` contracts. This
needs an explicit naming decision when device delivery is designed — for
example, renaming the existing Audiobookshelf concept (e.g. to
`MediaLibraryDelivery`) to free up `Delivery`/`DeliveryAttempt` for the
user-facing concept, or choosing a different name for the new one. This
document intentionally does not decide that rename now; it is called out so
it is not made accidentally.

---

## 16. AI Usage

AI should augment, not replace, structured bibliographic data.

Good AI uses:

- rank ambiguous search results;
- explain series context;
- summarize recommendations;
- rank related books;
- infer likely intended title from messy input.

Structured metadata should remain authoritative for:

- ISBN;
- release dates;
- author identities;
- series position;
- publication status.

Local AI should be callable through a replaceable API layer such as an OpenAI-compatible endpoint.

---

## 17. Competitive / Reference Projects

The standing comparison set is:

1. Shelfarr
2. Shelfmark
3. BookLore
4. LazyLibrarian
5. Listenarr
6. Readarr
7. Calibre-Web Automated
8. Audiobookshelf

Family Librarian's intended differentiation includes:

- first-class series/author tracking;
- future-release awareness;
- security/quarantine workflow;
- pluggable acquisition boundary;
- independent pluggable delivery;
- family-oriented request UX;
- provider-neutral architecture.

---

## 18. Initial Success Criteria

A V1 release is useful when:

1. A family member signs in.
2. They search for a book.
3. Family Librarian identifies the correct Work and series context.
4. They request ebook, audiobook, or both.
5. An administrator sees the request.
6. The admin manually provides a media file.
7. The file passes quarantine/security validation.
8. A clean, valid file is approved by policy; the admin only handles exceptional results.
9. The audiobook can be delivered to Audiobookshelf, while an approved ebook can be published to a configured CWA library or downloaded/delivered through an available provider.
10. The user receives a batched ready notification.
11. The request appears in completed history.
12. Family Librarian retains the information needed for future series/author recommendations.

---

## 19. Non-Goals for V1

- Reimplement all of Calibre.
- Build an audiobook streaming server.
- Build a native mobile app.
- Implement every acquisition source.
- Support every reader device.
- Depend on AI for factual book metadata.
- Require Authentik.
- Require any paid service.
- Implement full automated new-release monitoring before the request workflow works.

---

## 20. Open Questions

- Public name availability/trademark checks before a broad public release.
- Initial Blazor choice: hosted Blazor WebAssembly using MudBlazor, with an ASP.NET Core host/API that owns authentication and secrets.
- Exact metadata provider ranking/merge algorithm.
- How robustly series metadata can be resolved across providers.
- Browser/PWA viability for Kindle/Kobo transfer.
- Exact Audiobookshelf deep-link/user mapping behavior.
- Calibre-Web OPDS matching fidelity, CWA ingest idempotency, and CWA/Family
  Librarian user-account/deep-link mapping.
- Transient-media retention/cleanup policy and destination-backup verification.
- Whether acquisition engine lives in the main repository or a sibling repository.
- Plugin discovery/installation UX.
- Whether third-party providers are HTTP-only or whether trusted in-process .NET providers are also supported.
