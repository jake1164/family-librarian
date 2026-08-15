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

External tools should remain replaceable.

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
- The file enters quarantine, not the trusted library directly.

#### Linked Ebook Libraries

- A configured **Calibre-Web** instance is an optional linked ebook-library
  source. Its existing library is queried as part of the normal Ebook request
  flow; it is never bulk-imported into Family Librarian.
- A linked-library match means the ebook is available to stage for the request;
  it is not a trusted Family Librarian asset until the selected file completes
  the same quarantine, validation, malware-scanning, and approval pipeline as a
  manual upload.
- **Calibre-Web Automated (CWA)** is the first opt-in automated ebook-library
  destination. After approval, Family Librarian copies the retained trusted
  asset into a completed outbound staging file and atomically hands it to CWA's
  configured ingest directory. It then verifies the imported book through the
  library's normal catalog surface before reporting it ready.
- Plain Calibre-Web is initially a linked-library source and user-facing reading
  surface, not an assumed automated write API. Directly writing its database or
  automating its browser upload form is out of scope.
- A library destination is not a user delivery target. CWA/Calibre-Web may make
  a book browsable, readable, downloadable, or independently sendable to an
  e-reader; Family Librarian still records its own delivery and notification
  state.

#### Security Gate

Every acquired asset must pass:

- checksum generation;
- file type detection;
- format validation;
- malware scanning;
- approval policy.

No acquired file may enter a trusted delivery target before passing the security gate.

#### Audiobook Delivery

- Audiobookshelf should be the first supported media-library provider.
- It must be implemented behind a generic delivery interface.
- Audiobookshelf is a delivery/library destination, not the authority for
  whether the family owns an audiobook. An owned audiobook that is absent from
  Audiobookshelf should offer delivery/import; one already there should offer
  listening.

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
- Alternative audiobook/media-library servers.
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
destination contract receives only an approved trusted asset. Family Librarian
must retain that asset, copy it outside the watched directory, and atomically
move/rename the completed copy into the configured CWA ingest directory. It
must then verify the expected book and format are visible before the request
becomes ready. The integration is idempotent and records the external library
reference; a retry must not create an unreviewed duplicate.

CWA's optional post-ingest conversion, metadata rewriting, EPUB fixing, and
automatic e-reader send features are disabled for the initial integration.
They can be supported only when their completed output and status can be
verified under the Family Librarian security and notification workflow. CWA may
delete its ingest copy after processing, so it must never be the only retained
copy of an approved asset.

Plain Calibre-Web remains supported as a source and reading frontend. It is not
treated as an automated destination until it exposes a supported, versioned
write mechanism. Family Librarian never writes `metadata.db` directly and never
depends on browser-form automation.

Calibre-Web/CWA user accounts and permissions remain their own authorization
boundary. A Family Librarian ready notification may contain a deep link only
after the user can independently authenticate to the linked library; it must not
leak a service account's credentials or grant access through a shared URL.

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
from the core database, unrelated credentials, final library storage, and Docker
socket. Every artifact returns to Family Librarian-controlled staging before the
security pipeline chooses its final placement. Provider repositories are future
catalogs of provenance and immutable OCI image identities, not an initial
marketplace or automatic container-management system.

---

## 13. Security Architecture

All acquired media begins untrusted.

Suggested storage zones:

```text
/data/family-librarian/
  quarantine/
  processing/
  rejected/
  trusted/
```

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
Approval
  ->
Trusted asset
  ->
Delivery
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
8. The admin approves it.
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
- Asset storage layout.
- Whether acquisition engine lives in the main repository or a sibling repository.
- Plugin discovery/installation UX.
- Whether third-party providers are HTTP-only or whether trusted in-process .NET providers are also supported.
