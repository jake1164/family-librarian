# Family Librarian — Initial Implementation Plan

**Status:** Active implementation plan — catalog-first sequencing
**Scope:** .NET 10/C# 14 hosted Blazor WebAssembly request workflow; no acquisition or delivery runtime

## 1. Outcome and scope

The first release slice replaces family email requests with this complete, manually operated workflow:

```text
Login -> Search -> Select/resolve a Work -> Review author and series context
      -> Request ebook/audiobook/both -> Persist request -> My Requests
      -> Admin Queue
```

It proves the product's most important foundations—identity, normalized bibliographic data, series-aware discovery, and a durable request queue—before files, scanners, acquisition services, or delivery integrations add operational risk.

## 2. Current-state assessment

### What exists

The repository now contains the M1 foundation and M2 catalog-search slice: the
six-project hosted Blazor WebAssembly solution, local Identity and PostgreSQL
infrastructure, a baseline migration, Docker Compose deployment, health checks,
the provider abstraction, a deterministic demo provider, and public development
search/detail screens. The first M3 increment adds a configurable, throttled Open
Library adapter and conservative ISBN/edition normalization. Canonical catalog
persistence, provider candidate grouping/merge decisions, request creation, and
the Admin Integrations settings surface remain unfinished.

### Design constraints carried into implementation

- The documents were written under the working name **BookFinder**. The authoritative product name is **Family Librarian** and the repository/runtime slug is `family-librarian`.
- The existing proposed layout separates `Web`, `Api`, and `Worker` from day one. That is too much deployment and project surface for this slice. A Blazor web host can expose the small HTTP API needed later; there is no background workload yet that warrants a worker.
- The roadmap labels file upload, scanning, Audiobookshelf, and notifications as V1 requirements, but they are outside the explicit initial-stage target. They must remain designed-for but absent from the default runtime and backlog for this stage.
- The documents correctly distinguish Work, Edition, and Request, but leave the request's target ambiguous. This slice should request a **Work**, with optional preferred formats, rather than require a specific Edition. An administrator can choose an Edition later.
- Provider documents list Google Books, Open Library, and Hardcover without an evidence-based series-quality decision. Series is a differentiator, so provider selection and merge rules must be validated using representative family searches before treating any provider as authoritative for series.
- The product-name-options document is now historical. It should record the chosen name rather than imply that namespaces, image names, and project names remain undecided.

### Required rebranding

Before code is created, use these names consistently:

| Concern | Canonical name |
| --- | --- |
| Product/UI/prose | `Family Librarian` |
| Repository, Compose service, image, data directory | `family-librarian` |
| .NET solution, assemblies, namespaces | `FamilyLibrarian` |
| Example public host | `family-librarian.example` |

This applies to headings, prose, examples, solution/project names, Docker services, URLs, and storage paths. It does not require retroactively renaming external product names such as Authentik or Audiobookshelf.

## 3. Recommended solution structure

Use a modular monolith. The ASP.NET Core host/API is the only application process in this stage; it serves the WebAssembly client as static application assets.

```text
FamilyLibrarian.sln
src/
  FamilyLibrarian.Domain/
  FamilyLibrarian.Contracts/
  FamilyLibrarian.Application/
  FamilyLibrarian.Infrastructure/
  FamilyLibrarian.Web/
  FamilyLibrarian.Web.Client/
tests/
  FamilyLibrarian.Domain.Tests/
  FamilyLibrarian.Application.Tests/
  FamilyLibrarian.IntegrationTests/
  FamilyLibrarian.Web.Tests/
```

| Project | Responsibility | Allowed dependencies |
| --- | --- | --- |
| `Domain` | Entities, value objects, request transition rules, domain errors; no EF, ASP.NET, or provider SDKs. | None |
| `Contracts` | Versioned API request/response models shared by API and WebAssembly client; no domain or infrastructure types. | None |
| `Application` | Commands/queries, authorization requirements, interfaces (`IBookMetadataProvider`, clock, persistence boundary), and transaction orchestration. | `Domain`, `Contracts` |
| `Infrastructure` | EF Core/PostgreSQL mappings and migrations, ASP.NET Identity persistence, metadata-provider HTTP clients, provider normalization, and implementations of application interfaces. | `Application`, `Domain` |
| `Web` | ASP.NET Core host/API, authentication middleware, endpoint adapters, dependency-injection composition, health checks, and WebAssembly static-asset hosting. | `Application`, `Infrastructure`, `Contracts`, `Web.Client` |
| `Web.Client` | Blazor WebAssembly pages/components, MudBlazor UI, API client, client-side validation, and presentation-only authentication state. | `Contracts` |
| Tests | Tests at the appropriate boundary; test-only references should mirror production dependency direction. | Relevant production projects |

Do not create `FamilyLibrarian.Api`, `FamilyLibrarian.Worker`, an acquisition engine, or a plugin host yet. `Web` will map a small versioned HTTP surface around application use cases. A worker can be added only when a scheduled/background responsibility exists; it should reuse `Application` and `Infrastructure`, not become a second business domain.

## 4. Runtime, C#, and Blazor decision

Target **.NET 10 (`net10.0`) and C# 14**, the current supported LTS runtime/language pair. Confirm the supported version and current Microsoft Learn guidance when scaffolding, then pin the SDK with `global.json` and use matching ASP.NET Core/EF Core packages. Do not target .NET 6/C# 10-era runtime packages.

Build a **hosted Blazor WebAssembly application**: `FamilyLibrarian.Web.Client` is the browser UI and `FamilyLibrarian.Web` is the ASP.NET Core host/API. Use MudBlazor in the client. The host serves the compiled client from the same public origin, avoiding CORS in the default deployment.

This preserves the security boundary required by local authentication and generic OIDC:

- ASP.NET Core owns Identity, local credentials, OIDC confidential-client configuration, cookies, authorization, PostgreSQL, and provider secrets.
- The client uses an authenticated, same-origin API and receives only contracts/data it is allowed to display. It never contains secrets, database access, or authority to perform an Admin action.
- Client-side `AuthorizeView` and route guards improve UX only. Every API/application operation independently authorizes the caller and cookie-authenticated state changes use anti-forgery protection.

The initial client is already positioned for later PWA/device work. Add a service worker, install manifest, and device-specific browser APIs only when the later delivery spike justifies them; do not make them a condition of the request workflow.

## 5. First-slice domain model

Use `Guid` primary keys generated by the application. All entities carry `CreatedAtUtc`, `UpdatedAtUtc`, and a concurrency token where a user/admin update can conflict. Use UTC `DateTimeOffset`; use `DateOnly?` for imprecise publication dates and store its precision when required. API contracts are separate DTOs in `Contracts`; never serialize entities to the WebAssembly client.

### Identity and catalog

- **ApplicationUser** extends ASP.NET Core Identity's user with `DisplayName`, `Status`, `CreatedAtUtc`, and `LastLoginAtUtc`. Identity owns credentials, external logins, normalized email/user name, and roles.
- **Author**: canonical and sort names, optional biography. An author has many Works through `WorkAuthor`.
- **Work**: conceptual title, normalized title for matching, description, first/expected publication date, publication status, and optional cover URL/cache reference. It has many Authors, Editions, and SeriesEntries. Do not persist a duplicate `PrimaryAuthorId`; derive the primary author from `WorkAuthor.Ordinal = 0`.
- **WorkAuthor**: `WorkId`, `AuthorId`, `Ordinal`, and optional role. This keeps multi-author books and later pseudonym handling possible.
- **Edition**: `WorkId`, title, publisher, language, publication date/precision, and `EditionFormat` (hardcover, paperback, ebook, audiobook, other). ISBN is edition-level, not work-level. Narrator and detailed assets are deferred.
- **Series**: canonical name, optional description, and status (`Unknown`, `Active`, `Completed`).
- **SeriesEntry**: `SeriesId`, `WorkId`, `PositionLabel`, nullable numeric `PositionSort`, and `IsPrimary`. A label is authoritative display data (`"2.5"`, `"Prequel"`); `PositionSort` only supports ordering. A Work may participate in more than one Series.
- **ExternalReference**: a provider-owned identifier for `Work`, `Edition`, `Author`, or `Series`: provider ID, entity type, entity ID, external ID, optional source URL, observed timestamp, and optional compact raw-payload snapshot/version. It is provenance, never the canonical key.

### Requests

- **BookRequest**: `UserId`, `WorkId`, `Status`, requester note, request/last-status timestamps, and optional admin note. It represents one user's request for one Work.
- **RequestFormat**: child of `BookRequest` with `MediaType` (`Ebook`, `Audiobook`) and a format status. A request must contain one or both unique media types; it cannot use a flags enum as its persisted source of truth.
- **RequestStatusHistory**: request ID, from/to status, actor user ID or system actor, timestamp, and reason. This is a small, purposeful audit trail that makes status visible and preserves future workflow history.

Do not create `MediaAsset`, `AcquisitionJob`, `Delivery`, user reading-state/follow tables, notifications, security evaluations, or delivery targets in this stage. Reserve names/extension points in the design only. Future acquisition attaches to `BookRequest` and `RequestFormat`; fulfillment can later select an `Edition` without changing the request's meaning.

## 6. Request status model

The initial status enum is deliberately small:

```text
PendingAcquisition  -- created after a canonical Work is selected; visible in Admin Queue
NeedsReview         -- admin needs clarification or metadata correction
NotAvailable        -- admin cannot fulfill at present
Cancelled           -- requester cancels while not fulfilled
```

Allowed transitions are application commands, never arbitrary UI edits:

```text
create -> PendingAcquisition
PendingAcquisition <-> NeedsReview
PendingAcquisition | NeedsReview -> NotAvailable
PendingAcquisition | NeedsReview -> Cancelled
NotAvailable -> PendingAcquisition       (admin reopens)
Cancelled -> PendingAcquisition          (requester/admin reopens)
```

The later lifecycle adds `Acquiring`, `Acquired`, `SecurityReview`, `AwaitingApproval`, `PreparingDelivery`, `Ready`, `Delivered`, `Completed`, and failure states. Add those only alongside the acquisition/security/delivery work. The status history table and per-format children prevent that expansion from requiring a rewrite. Duplicate detection is not a status transition: it warns before create when the same user already has a non-terminal request for the same Work and overlapping format, but allows an explicit confirmation for a legitimate repeat request.

## 7. Authentication and authorization

Local Identity is mandatory; OIDC is optional and can coexist with it.

1. Configure ASP.NET Core Identity with PostgreSQL stores, strong password policy, lockout/rate limits, secure cookie settings, email as the local sign-in name, and confirmed-email policy configurable for self-hosted use. The host renders or redirects to the local-account/OIDC entry points and establishes a secure same-origin cookie; the client only reflects that authentication state.
2. Seed `User` and `Admin` roles through a migration-safe startup service. No external claim is treated as an authorization decision by itself.
3. Bootstrap the first administrator only when no admin exists, using one-time `BootstrapAdmin__Email` and `BootstrapAdmin__Password` secrets supplied outside source control. Never log them; clear/ignore the password after successful creation. Development can use a documented dev-only seed. For an internet-facing install, require bootstrap configuration rather than exposing an unauthenticated setup page.
4. Add a named generic OpenID Connect scheme only when complete issuer, client ID, client secret, and callback configuration are present. Local sign-in remains available in `Local + OIDC` mode.
5. Link external identities through ASP.NET Identity's external-login store using stable `(issuer, subject)` values. Match/link an existing local user only after an authenticated, explicit account-linking flow; do not link on email alone. Auto-provisioning is off by default and, when enabled, must require verified email and an allowlist/domain policy.
6. Map configured groups/claims to internal roles at sign-in with exact issuer validation and a documented allowlist. The default role is `User`; grant `Admin` only by explicit mapping or local administration. Policies protect `/admin`, admin endpoints, and all status changes.

Authentik support is configuration and test coverage, not a runtime dependency: document a standard confidential OIDC client, redirect URI, scopes `openid profile email`, issuer discovery URL, stable `sub` mapping, and an optional group-to-Admin claim mapping. Keep an Authentik compose profile/example outside the default `app + postgres` topology and execute a documented smoke test against it.

## 8. Metadata and search architecture

`IBookMetadataProvider` belongs in `Application` and exposes capability metadata, `SearchAsync(BookSearchQuery)`, and `GetDetailsAsync(ProviderBookReference)`. Its contracts return normalized **candidate** data, not domain entities: title, authors, editions/ISBNs, description, cover URL, publication data/precision, series candidates, external IDs, source URL, completeness flags, and provenance.

`Infrastructure` hosts provider-specific clients and mappers. Provider payloads never leak into components or the domain. Preserve provider ID and external reference for every accepted item and retain a bounded JSON snapshot only where it aids debugging/re-normalization; do not make raw payload the read model.

### Provider recommendation and merge policy

Start with **Open Library** and **Google Books** behind the common interface, configurable independently. Use ISBN match as the strongest cross-provider edition signal; otherwise use normalized title + ordered author similarity only to group candidates for user/admin review. Google Books is useful for broad search, covers, and ISBN/edition enrichment; Open Library is useful for work/author/edition structure. Neither should be assumed to provide complete or correctly ordered series metadata.

Run the metadata/series spike before committing production mapping and merge rules, but implement the provider interface and fake provider in parallel with the foundation. If the representative corpus shows that neither source has adequate series coverage, evaluate Hardcover (subject to its terms/credentials) or another viable provider as a supplemental, optional series source—never as a domain dependency. The result selects provider priority by field, not one global winner.

Search flow:

1. Query enabled providers in parallel with timeouts, cancellation, and per-provider diagnostics.
2. Normalize and group obvious equivalents without silently merging ambiguous results.
3. Display candidates with source/provenance and the best available author/series summary.
4. On selection, fetch details, then an application command resolves an existing Work by exact external reference/ISBN or creates a new canonical Work, its relationships, and provenance in one transaction.
5. When sources disagree, retain the selected/provider-preferred value and provenance; flag conflict for admin correction rather than overwrite an edited canonical field. Initial admin correction can be a focused catalog correction action, not a full catalog-management system.

Providers that require an API key or token are disabled until configured. The
normal product path is a focused Admin Integrations UI: an administrator can
enable/disable a metadata provider, submit or replace a write-only credential,
test the connection, and see redacted health/last-use status. Stored secrets are
encrypted by the host and are never returned to the WebAssembly client or written
to logs/audit payloads. Deployment-provided secrets remain a supported read-only
override for operators who use an external secret manager; the UI identifies them
as externally managed. Detailed lifecycle and Data Protection key-ring requirements
are defined in `docs/03-provider-api-contracts.md`.

Series risk deserves an explicit UI rule: show a series position only when a source provides it and label uncertain/incomplete context. Do not infer an ordinal from search order. The initial series page should show known entries and gaps, not claim complete series coverage.

## 9. PostgreSQL and EF Core plan

Use one PostgreSQL database and one EF Core `AppDbContext` (with Identity) for the modular monolith. Use schemas to clarify ownership:

```text
identity: ASP.NET Identity users, roles, user roles/logins/tokens
catalog: authors, works, work_authors, editions, series, series_entries, external_references
requests: book_requests, request_formats, request_status_history
audit: audit_events
app: EF migrations history and future integration configuration
```

Metadata integration configuration uses an allowlisted schema per installed
provider. Persist ordinary settings separately from protected secret values; the
database never contains plaintext provider credentials. The configuration record
retains provider ID, enabled state, configuration version, protected-value purpose
and format version, created/changed timestamps, and actor/audit references. Provider
management routes address known installed provider IDs and do not accept arbitrary
executable code or unrestricted target URLs.

Key constraints/indexes:

- unique normalized `identity` email and user name (Identity defaults); unique role name;
- `work_authors (work_id, author_id)` unique and `(work_id, ordinal)` unique;
- `editions (isbn13)` unique when non-null, plus an ISBN-10 partial unique index if retained;
- `series_entries (series_id, work_id)` unique; index `(series_id, position_sort, position_label)` for context order;
- `external_references (provider_id, entity_type, external_id)` unique, plus `(entity_type, entity_id)` lookup index;
- `request_formats (request_id, media_type)` unique;
- `book_requests (user_id, work_id, status)` and `(status, updated_at_utc)` indexes for My Requests and Admin Queue;
- partial unique index for overlapping active requests requires validation because formats are children. Start with transactional duplicate checking plus a PostgreSQL advisory/row lock; introduce a partial/exclusion constraint only after the desired repeat-request policy is proven.

Use explicit Fluent API mappings, snake_case table/column names, `timestamptz`, and a PostgreSQL concurrency token (`xmin`) or explicit version column. Keep domain event/audit persistence transactional with the command that changes status. Use soft deletion only for catalog records that may be corrected/merged (`IsRetired`, `ReplacedById`); never hard-delete completed requests or status history. Identity user deletion should normally be disable/anonymize under a documented retention policy, not cascade into requests.

Create checked-in, reviewed EF migrations. Development may apply them on startup for convenience; production Compose must run the same image in an explicit one-shot `migrate` service before `app` starts. Never use `EnsureCreated` or generate migrations at container startup. Back up PostgreSQL before destructive/data migrations and include an idempotent data-migration path for catalog merges.

## 10. Application/API and UI boundary

Application use cases define authorization and business rules. MudBlazor WebAssembly components orchestrate interaction, navigation, formatting, and validation display; they do not query EF or call providers directly. Endpoint handlers are thin adapters around the same use cases and are the required client contract.

Minimum operations:

| Operation | Boundary |
| --- | --- |
| Search enabled metadata providers | authenticated query / `GET /api/v1/catalog/search` |
| Fetch candidate details and normalized series context | authenticated query / `GET /api/v1/catalog/candidates/{provider}/{id}` |
| Resolve selected candidate to canonical Work | authenticated command / `POST /api/v1/works/resolve` |
| Get Work/author/series context | authenticated query / `GET /api/v1/works/{id}` and series route |
| List metadata-provider status/configuration | Admin query / `GET /api/v1/admin/integrations/metadata` (secret state only) |
| Enable/disable, replace/clear credential, test provider | Admin commands under `/api/v1/admin/integrations/metadata/{provider}` |
| Create request with formats | authenticated command / `POST /api/v1/requests` |
| List current user's requests and history | authenticated query / `GET /api/v1/me/requests` |
| List admin queue and request detail | Admin query / `GET /api/v1/admin/requests` and detail route |
| Change an initial request status | Admin/requester command as allowed / `POST .../transitions` |

The WebAssembly client invokes only these API endpoints through a typed client and shared `Contracts` DTOs; it never invokes application handlers or provider implementations directly. Publish only the focused metadata-provider management surface when Admin authorization exists; do not publish acquisition, asset, delivery, arbitrary plugin-installation, or general-purpose configuration APIs now.

## 11. UI plan

- **Login:** local sign-in, optional "Sign in with <provider>" action, lockout/error handling, and no disclosure of enabled admin privileges.
- **Home / My Requests:** active requests first, current per-format summary, clear status explanation, cancellation/reopen where allowed, and link to search. History can be a tab/filter rather than a separate page.
- **Search / Add Book:** forgiving title/author/ISBN input, loading/error states by provider, and no persistence merely from searching.
- **Search Results:** grouped candidates, clear source, title/author/cover/edition facts, and an explicit selection action. Avoid false certainty when a series is unknown.
- **Book Detail:** canonical Work detail, known editions, authors, source confidence/provenance where useful, and request action.
- **Series Context:** known position and ordered entries, previous/next only when supported by data, and a gentle warning for earlier known entries; no follow/recommendation controls yet.
- **Request Confirmation:** select ebook, audiobook, or both; optional short note; duplicate warning; confirmation creates the durable request.
- **Admin Queue:** authorized list by status/age, requester, Work, requested formats, and concise source/series context.
- **Admin Request Detail:** status-history timeline, metadata conflict/review information, admin note, and allowed status transitions.
- **Admin Metadata Integrations:** focused provider enablement, write-only
  credential create/replace/clear, test connection, and redacted status/last-use
  diagnostics. It does not expose stored secrets or a general merge-rule/plugin
  editor.

Use accessible, responsive MudBlazor components with plain language suitable for a family, and verify keyboard navigation, labels, focus handling, and semantic HTML. Prefer route/component tests around the request confirmation and admin authorization paths.

## 12. Docker and development environment

Default Compose topology:

```text
family-librarian-migrate  (one-shot migration command using the application image)
family-librarian          (ASP.NET Core API + hosted Blazor WebAssembly assets)
postgres                  (PostgreSQL)
```

`postgres` has a named data volume and `pg_isready` health check. The app has liveness (`/health/live`) and readiness (`/health/ready`, including database connectivity) checks and waits for successful migration plus database health. The app image runs non-root, exposes an internal HTTP port, has no database port published in production, and writes logs to stdout as structured JSON. Add a local development override that publishes PostgreSQL only to loopback if tools need it. Development Compose builds the image locally; release CI builds once and publishes immutable tags to `ghcr.io/jake1164/family-librarian` through repository GitHub Actions credentials, then Compose deploys the selected image tag.

Configuration is standard ASP.NET Core configuration with validated options at startup:

- `ConnectionStrings__FamilyLibrarian`;
- `Authentication__Local` password/lockout settings;
- `BootstrapAdmin__Email` and `BootstrapAdmin__Password` (secret, never committed);
- `Authentication__Oidc__*` only when OIDC is enabled;
- `MetadataProviders__OpenLibrary__*` and `MetadataProviders__GoogleBooks__*` as
  deployment-provided, read-only overrides; API keys/tokens are secrets and the
  normal self-hosted configuration path is the Admin Integrations UI;
- persistent, backed-up ASP.NET Core Data Protection key-ring configuration for
  cookies and application-protected provider credentials; production key-ring
  encryption-at-rest must be explicit when a custom persistence location is used;
- `ForwardedHeaders`/public base URL configuration for a reverse proxy.

Commit `.env.example` with placeholders and use an ignored `.env`/secret manager for development. Do not put passwords, client secrets, or API keys in Compose. The default stack assumes the reverse proxy terminates HTTPS in external deployments; local development may use HTTP or a trusted development certificate. Document forwarded headers, secure cookies, and the canonical public URL because OIDC callback URLs and cookie security depend on them. Authentik is an optional Compose profile/example, not a default service.

## 13. Testing strategy

| Layer | Highest-value coverage |
| --- | --- |
| Domain unit tests | request transition matrix, valid requested formats, SeriesEntry ordering/labels, Work/Edition distinction |
| Application tests | duplicate-request behavior, canonicalization decisions, provider fallback/timeouts, conflict handling, ownership and Admin authorization |
| Provider mapper/contract tests | fixture-based Google Books/Open Library normalization, ISBN matching, missing/contradictory series data, fake provider behavior |
| PostgreSQL integration tests | migrations from empty database, all unique constraints/indexes, concurrent duplicate attempt, Identity/external-login linking, audit/history transactionality |
| Web/API tests | unauthenticated redirects/401s, User cannot access admin queue or integration settings, provider secret is never readable after write, Admin integration anti-forgery/validation, endpoint validation, and OIDC callback configuration using a test handler |
| WebAssembly component/browser tests | MudBlazor component behavior, local login -> Admin configures a fake credential without read-back -> search fake/controlled provider -> resolve -> request -> My Requests -> Admin Queue |

Run database integration tests against disposable PostgreSQL (for example, Testcontainers) rather than an in-memory EF provider. Keep metadata HTTP tests fixture/fake based in CI; provider live smoke tests are opt-in to prevent flaky rate-limited builds. Include an optional Authentik compose smoke test in release validation, proving discovery, login, external identity linkage, and configured Admin mapping without making it a CI/runtime prerequisite.

## 14. Ordered implementation milestones

### Implementation sequencing decision

The first visible vertical slice is catalog search, not authentication. Local
Identity remains in the solution but is disabled by default while the catalog is
being proven; catalog endpoints and UI are intentionally available without a
session in this development stage. Do not expose this mode to the internet or
collect family requests in it.

Authentication becomes a required boundary when requests are introduced,
because request ownership and the future Admin Queue require a server-verified
user. At that point, enable the existing local Identity path first. Generic
OIDC, Authentik validation, account linking, and claim mapping remain later
hardening work rather than prerequisites for search.

### M0 — Decision record and metadata-series spike

**Objective:** eliminate naming ambiguity and validate the one high-risk input to the first slice.

- Complete the rebrand and add this plan.
- Build a representative, privacy-safe family search corpus covering exact/messy title, ISBN, multi-author, series middle, novella, recent/upcoming, ebook, and audiobook cases.
- Timebox provider experiments for Open Library and Google Books; evaluate a supplemental series source only if results require it.
- Record coverage, conflicts, terms/credential constraints, field-priority recommendation, and known UI caveats in `docs/spikes/metadata-series-spike.md`.

**Dependencies:** none. **Acceptance:** names are consistent; the spike supplies a documented provider/series decision or explicitly limits the first UI's claims.

### M1 — Runnable foundation

**Objective:** a clean checkout starts a secure local app and PostgreSQL.

- Create the six-project solution, shared API-contract conventions, hosted WebAssembly/MudBlazor shell, Dockerfile, Compose, environment example, health checks, and structured logging.
- Add EF Core/PostgreSQL context, baseline migration, migration runner, and test PostgreSQL harness.
- Establish CI for build, formatting/analyzers, unit tests, and migration integration test.

**Dependencies:** M0 naming. **Acceptance:** `docker compose up` with supplied development secrets produces a healthy app/database and a fresh database is created by reviewed migrations.

### M2 — Catalog-search vertical slice (unauthenticated development mode)

**Objective:** put useful book discovery on screen before introducing accounts.

- Implement the provider interface and a deterministic fake provider that does
  not send family search terms to a third party.
- Implement public development-only search and candidate-detail endpoints plus
  accessible Search Results and Book Detail screens.
- Keep the UI explicit that no request can be created and that series facts are
  only as complete as the active provider says they are.

**Dependencies:** M1. **Acceptance:** a local user can search by title, author,
or ISBN and inspect known editions and series facts without logging in.

### M3 — Catalog foundation and metadata search

**Objective:** turn a selected provider candidate into a canonical,
provenance-preserving catalog record.

- Implement catalog entities, mappings, migrations, the selected keyless real
  provider, provider adapters for later credentialed providers, normalization,
  candidate grouping, and the conflict policy from M0.
- Implement resolve/create Work and author/series read queries.
- Make provider-backed catalog operations authenticated when local Identity is
  introduced for requests; search may remain public only in an explicitly
  development-only configuration.

Credentialed providers remain disabled until the Admin configuration surface is
available in M4.

**Dependencies:** M1, M0 provider decision. **Acceptance:** a user can search a
representative title/ISBN, select a candidate, revisit the canonical Work, and
sees only supported series facts/provenance.

### M4 — Local authentication and request workflow

**Objective:** introduce the smallest real identity boundary needed for owned
requests and future administration.

- Configure Identity, roles, first-admin bootstrap, login/logout, policy-based authorization, and audit-friendly last-login update.
- Build a minimal authenticated shell and the focused Admin Metadata Integrations
  API/UI. Support enable/disable, write-only credential create/replace/clear,
  server-side connection testing, encrypted storage, external read-only secret
  overrides, redacted health, and audit events without secret values.
- Defer generic OIDC configuration, external-login linking/provisioning rules,
  and Authentik setup/smoke-test documentation until the manual request loop is
  proven.

**Dependencies:** M1, M3. **Acceptance:** local login works with no IdP, a
non-admin is denied admin access, and an admin can configure and test a
credentialed metadata provider without any endpoint returning the stored secret;
the configuration survives a Compose restart with its protected key ring.

### M5 — Request workflow and user experience

**Objective:** replace email requests for an identified Work.

- Implement BookRequest, RequestFormat, status history, transition rules, duplicate detection, migrations, commands, and queries.
- Build request confirmation and My Requests active/history views; add clear cancellation/reopen behavior.
- Add tests for formats, transitions, duplicates, user ownership, and persistence.

**Dependencies:** M3, M4. **Acceptance:** a user can request ebook, audiobook, or both for a canonical Work; it persists across restart and is visible only to that user with accurate status.

### M6 — Admin queue and vertical-slice hardening

**Objective:** finish the promised end-to-end manual review loop.

- Implement Admin Queue, Admin Request Detail, status/review actions, notes, filters, and audit events.
- Add authorization, concurrency, migration-upgrade, provider-failure, responsive/accessibility, and browser E2E coverage.
- Document deployment, backup/restore expectation, and a manual Authentik test run.

**Dependencies:** M5. **Acceptance:** an admin sees each new request in the queue, can review/update it within allowed transitions, and the complete login-to-queue flow passes from a clean Compose deployment.

## 15. Blocking decisions and deferred work

Only the metadata/series spike blocks production implementation of catalog mapping and honest series UX. It should start before M3 and can run while M1/M2 are built. The exact initial provider field-priority rules, series confidence display, and whether a supplemental series source is acceptable remain validation decisions.

The following are intentionally deferred: automated/manual acquisition runtime, file upload/storage/quarantine, malware scanning/format validation, any security pipeline, Audiobookshelf integration, authenticated download/device delivery, PWA/WebUSB/Kindle work, notification providers, following/reading progress, new-release polling, recommendations/AI, generic acquisition-provider HTTP protocol, background workers, and a plugin marketplace. Preserve only the request-to-work/format relationship, provenance, clean application boundaries, and versioned endpoint shape needed to add those capabilities later.

Audiobookshelf and browser-device spikes remain later decision gates; they do not block this request/catalog slice.
