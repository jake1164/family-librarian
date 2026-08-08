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
- Generic OIDC support must be available.
- Authentik should be a documented and tested OIDC configuration.
- Roles:
  - User
  - Admin

#### Book Search and Identification

- Search by title, author, ISBN, and loose user-entered text.
- Query one or more external metadata providers.
- Normalize results into the application's internal model.
- Display:
  - cover;
  - title;
  - author;
  - description;
  - publication date;
  - series;
  - series position;
  - edition/format where available.
- Allow admin correction when provider data is wrong or ambiguous.

#### Requests

Users can request:

- Ebook
- Audiobook
- Both

Users can view active requests and status.

Completed items should not be deleted; they should move to History.

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
- Audiobookshelf
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

Initial runtime deployment may contain:

```text
family-librarian
family-librarian-worker
postgres
clamav
```

Optional services:

```text
audiobookshelf
ntfy
acquisition-provider-*
```

---

## 9. High-Level Architecture

```text
                     Identity Provider
               Local Auth / OIDC / Authentik
                            |
                            v
                  +------------------+
                  |    Family Librarian    |
                  | Blazor + API     |
                  +---------+--------+
                            |
                  +---------+----------+
                  |                    |
                  v                    v
             PostgreSQL          Background Worker
                                      |
             +------------------------+------------------------+
             |                 |              |               |
             v                 v              v               v
        Metadata          Acquisition      Security        Delivery
        Providers          Providers       Providers       Providers
             |                 |              |               |
       Google/OpenLib     Manual/HTTP     ClamAV/etc    ABS/Kindle/etc
       Hardcover/etc       plugins
```

---

## 10. Authentication Architecture

Authentication must not require Authentik.

Supported modes should conceptually include:

```text
Local
OIDC
Local + OIDC
```

OIDC should be generic enough for:

- Authentik
- Keycloak
- Entra ID
- Okta
- other standards-compliant providers

Application authorization remains internal.

External claims/groups may map to:

```text
User
Admin
```

Local administrator bootstrap must be available for first startup.

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
9. The audiobook can be delivered to Audiobookshelf or the ebook can be downloaded/delivered through the available provider.
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
- Asset storage layout.
- Whether acquisition engine lives in the main repository or a sibling repository.
- Plugin discovery/installation UX.
- Whether third-party providers are HTTP-only or whether trusted in-process .NET providers are also supported.
