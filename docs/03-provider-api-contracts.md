# Family Librarian — Provider & API Contract Design

**Status:** Draft v0.1  
**Date:** 2026-08-08

---

## 1. Goal

Family Librarian should remain useful as external services change.

The application should define stable contracts for capabilities while allowing implementations to be replaced, added, or disabled.

The initial contract families are:

```text
IBookMetadataProvider
IAvailabilityProvider
IStoreOfferProvider
IOwnedLibraryProvider
ILibraryDestination
IAcquisitionProvider
IMalwareScanner
INotificationProvider
IDeliveryProvider
```

Not every contract must support third-party dynamic loading in V1. The important requirement is that core workflow code depends on the contract rather than a specific vendor.

---

## 2. Provider Design Principles

1. Providers declare capabilities.
2. Providers do not receive database credentials.
3. Secrets belong to the provider/integration configuration that needs them.
4. Provider failures must not corrupt workflow state.
5. Providers should expose health/status.
6. Core records the provider ID and version involved in important decisions.
7. Contracts should be versioned.
8. HTTP-based providers should be language-agnostic.
9. External providers should be independently containerizable where practical.
10. Bundled providers are individually enableable; bundling is not a requirement
    to contact a provider.
11. Provider enablement, authorization to use a provider, and policy preference
    are separate decisions.
12. A store offer, availability/borrow action, owned copy, and staged artifact
    are distinct outcomes.

---

## 3. Capability model and normalized options

A provider declares one or more capabilities instead of being forced into a
single acquisition-provider type:

```text
Metadata
Availability
StoreOffer
FreeContent / DirectAcquisition
Acquisition
OwnedLibrary
LibraryDestination
Delivery
```

Examples: a library integration may offer availability and an external borrow
action without yielding a file; Calibre-Web may report a linked ebook; CWA may
act as a library destination; Audiobookshelf may report owned content and act as
a library/delivery backend; and a public-domain provider may expose metadata and
direct acquisition. The UI offers only actions supported by the capability.

The unified search read model uses a neutral `FulfillmentOption` for results
from all of these capabilities. It carries provider/result identity, media type,
edition/format/language when known, ownership or availability state, cost and
currency, DRM/license facts when reported, acquisition method, external action
URI, and opaque provider data. Its acquisition methods include
`DirectDownload`, `Borrow`, `Purchase`, `ExternalAction`, `ProviderManaged`,
`ManualImport`, and `OwnedImport`.

`FulfillmentOption` must not be named `AcquisitionCandidate`: the latter remains
the job-scoped record representing a concrete artifact candidate after an
`AcquisitionJob` begins. This prevents a purchase link or a library hold from
being mistaken for a file the application controls.

Provider configuration defines whether and how a provider may run: enabled
state, allowed manual/automatic use, available users/roles, scoped credentials,
rate limits, and deployment-controlled network route. Policy later ranks only
permitted options. It may recommend an option but cannot silently purchase,
borrow, or download without a separately authorized supported action.

---

## 4. Metadata Provider

Conceptual interface:

```csharp
public interface IBookMetadataProvider
{
    string Id { get; }
    string Name { get; }

    Task<IReadOnlyList<BookSearchResult>> SearchAsync(
        BookSearchQuery query,
        CancellationToken cancellationToken);

    Task<BookMetadataResult?> GetWorkAsync(
        ExternalBookReference reference,
        CancellationToken cancellationToken);
}
```

Provider capabilities may include:

```text
TitleSearch
AuthorSearch
IsbnSearch
Series
Covers
Descriptions
PublicationDates
UpcomingReleases
EditionData
```

Candidate providers:

```text
Google Books
Open Library
Hardcover
```

### Metadata normalization

Provider responses must be normalized before persistence.

Core should preserve:

```text
ProviderId
ExternalId
Raw payload/version where useful
```

but internal Work/Author/Series IDs remain authoritative.

### Unified search enrichment

The host composes the user-facing search response; a metadata provider does not
decide local ownership or delivery state. For every normalized provider result,
the application must:

1. resolve or match the canonical Work and relevant Editions;
2. match local assets using retained internal/provider identifiers and ISBNs
   before considering title/author fuzzy matching;
3. load active request/acquisition state and the current user's delivery state;
4. return one Work result with independently enriched Ebook and Audiobook
   availability.

The response must be able to represent `Owned`, `Requested`,
`WaitingForAvailability`, `Acquiring`, `Processing`, and delivery availability
for each media type. It should offer product actions such as `GetEbook`,
`GetAudiobook`, `Read`, `Listen`, and `Deliver`, rather than exposing
acquisition-provider mechanics to ordinary users. Matching logic must retain an
ambiguity outcome for alternate titles, translations, boxed sets, editions, and
abridged versus unabridged recordings.

---

### Linked ebook-library providers

`IOwnedLibraryProvider` supplies catalog/source capabilities; it does not make
an external library's database authoritative for Family Librarian's workflow
records. A conceptual
Calibre-Web implementation searches the server-side configured OPDS/catalog
surface, returns a `LibraryItemReference`, and stages a specifically selected
format into Family Librarian quarantine only after request authorization.

```csharp
public interface IOwnedLibraryProvider
{
    string Id { get; }
    OwnedLibraryCapabilities Capabilities { get; }

    Task<IReadOnlyList<LibraryItemReference>> SearchAsync(
        LibrarySearchQuery query,
        CancellationToken cancellationToken);

    Task<StagedArtifact> StageAsync(
        LibraryItemReference item,
        CancellationToken cancellationToken);
}
```

`StageAsync` returns only to controlled quarantine. It must not return a
browser-visible credential-bearing URL, create a trusted `MediaAsset`, or bypass
the file-safety and approval pipeline. Matching uses identifiers and ISBN first;
title/author matching remains explicitly ambiguous when it cannot prove the
edition/format.

**Missing capability: retrieving an already-owned artifact.** Everything above
covers finding and staging a book from a *linked, not-yet-trusted* library.
There is a second, currently unmet need: fetching the bytes of a book that is
already the family's own trusted CWA artifact (an `OwnedImport`
`FulfillmentOption`, or a `LibraryImport` with `Status = Available`), so it can
be delivered without re-acquisition. Neither `IOwnedLibraryProvider` nor
`ICwaCatalogClient` can do this today — `ICwaCatalogClient.FindBookIdAsync`
only resolves an ID. This blocks Send-to-Kindle-from-an-existing-copy,
direct-device transfer, and browser download for any book already in CWA,
and it blocks them identically for local and remote CWA, since neither
should depend on filesystem or SFTP access to read a book back out.

The shape should mirror the pattern `IDirectAcquisitionProvider.FetchAsync`
already establishes for a different capability — fetch a stream for a
previously returned, provider-opaque reference:

```csharp
public interface IOwnedLibraryProvider
{
    string Id { get; }

    Task<IReadOnlyList<FulfillmentOption>> FindOwnedMatchesAsync(
        Guid workId, RequestMediaType mediaType, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches the canonical artifact bytes for a previously returned
    /// <see cref="OptionKind.Owned"/> option (or an equivalent
    /// <c>LibraryImport</c> reference). Implementations should prefer an
    /// HTTP/OPDS download mechanism so remote deployments work without
    /// filesystem or SFTP access; local deployments may use direct filesystem
    /// access as an optimization behind the same contract.
    /// </summary>
    Task<OwnedArtifact> FetchOwnedArtifactAsync(
        FulfillmentOption ownedOption, CancellationToken cancellationToken);
}

public sealed record OwnedArtifact(Stream Content, string Filename, string Format);
```

For CWA specifically, this means adding an OPDS/HTTP download call to
`CwaCatalogClient` (Calibre-Web's OPDS feed exposes acquisition/download
links per entry — the same links `CwaCatalogClient` already parses to extract
a book ID) rather than reading the Calibre library's files or `metadata.db`
directly, and rather than any SFTP-based retrieval. This is required before
any direct-device or browser-delivery work can proceed for an already-owned
book; see `docs/01-product-architecture-spec.md` §15.1.

**OPDS integration stability.** `CwaCatalogClient` is identifier-first,
2026-08-31: for each ISBN-13 known for the requested Work, it queries CWA's
`/opds/search/{query}` endpoint using the ISBN itself as the query string,
and accepts the result only when that query returns exactly one distinct
book ID — a numeric ISBN string does not suffer the title-collision ambiguity
a substring match does. Whether CWA's search actually indexes ISBN is
unconfirmed (CWA does not expose a richer machine-readable ownership API);
if it doesn't, the ISBN query simply returns zero or many results and this
falls through to the title/author fallback, so there is no correctness risk
in trying it regardless. The title/author fallback string-matches the entry
title (substring, case-insensitive) and, if known, the author, but now
collects every matching entry rather than the first: a single distinct book
ID is accepted, while more than one distinct ID is treated as ambiguous and
returns "not found" rather than guessing. It also rejects an otherwise-
matching entry whose title carries a known derivative/combined-work marker
(e.g. "Summary of Debt of Honor", "Debt of Honor / Executive Orders") — see
`docs/family-librarian-book-matching-design-findings.md` §5/§6/§8. This
guard is a fixed, non-exhaustive keyword list, not the fuller
`ExpectedBookProfile`/`CandidateMatchResult` evidence model that document
describes for the separate acquisition-provider matching pipeline — that
remains future work if CWA correlation needs it. Still weaker than full
identifier-first matching: a missing `<author>` element on a CWA entry is not
treated as a mismatch (CWA's OPDS template does not reliably include one),
and no per-edition ISBN is threaded through — any ISBN known for the Work is
tried, not specifically the edition being published. Both are accepted,
documented gaps rather than oversights.

`ILibraryDestination` publishes an already approved staged artifact and verifies
the result. The destination becomes the permanent store for the media; Family
Librarian retains only provenance and transient staging bytes. It is deliberately
distinct from `IDeliveryProvider`: publishing a book to a shared library does
not mean that it has been delivered to a particular user or Kindle.

```csharp
public interface ILibraryDestination
{
    string Id { get; }
    LibraryDestinationCapabilities Capabilities { get; }

    Task<LibraryImportResult> ImportAsync(
        ApprovedAsset asset,
        LibraryImportRequest request,
        CancellationToken cancellationToken);

    Task<LibraryImportStatus> GetImportStatusAsync(
        LibraryImportReference reference,
        CancellationToken cancellationToken);
}
```

The first destination implementation is CWA. Family Librarian writes a complete
copy from controlled staging to a private outbound staging location and performs
an atomic handoff to CWA's configured ingest directory; it never streams a
partially written acquisition into the watched directory. The adapter records
the result only after finding the expected book/format in the CWA catalog, then
the host removes the local media copies according to retention policy.

The initial CWA integration disables CWA post-ingest conversion, metadata
rewriting, EPUB fixing, and auto-send. They may become supported only when their
final output and completion status can be verified by the integration. A CWA
destination is opt-in. Plain Calibre-Web is a source/catalog frontend in this
scope, not an assumed write API; direct `metadata.db` writes and HTML-form
automation are prohibited. Future ebook or audiobook destinations (including a
combined media library) implement the same capability contract and own their
own import, verification, format, and deep-link behavior.

---

## 5. Acquisition Provider

Long-term recommendation: external HTTP provider protocol.

### Provider Manifest

```http
GET /manifest
```

Example:

```json
{
  "protocolVersion": "1",
  "id": "example-provider",
  "name": "Example Provider",
  "version": "1.2.0",
  "capabilities": [
    "ebook",
    "audiobook",
    "search",
    "availability",
    "acquire"
  ],
  "egressPolicy": "PRIVATE_REQUIRED"
}
```

`egressPolicy` is optional for backward compatibility; if omitted, the default
is `NORMAL`. It describes the required egress class, not a commercial VPN
provider. Valid initial policy values are:

```text
NORMAL
PRIVATE_REQUIRED
CUSTOM_PROXY
```

### Health

```http
GET /health
```

### Search

```http
POST /search
```

Request:

```json
{
  "requestId": "req_123",
  "mediaType": "audiobook",
  "work": {
    "title": "Example Book",
    "authors": ["Example Author"],
    "series": "Example Series",
    "seriesPosition": "3",
    "identifiers": {
      "isbn13": "..."
    }
  }
}
```

Response:

```json
{
  "candidates": [
    {
      "providerReference": "abc123",
      "title": "Example Book",
      "author": "Example Author",
      "format": "m4b",
      "sizeBytes": 123456789,
      "durationSeconds": 28800,
      "metadata": {}
    }
  ]
}
```

### Acquire

```http
POST /acquire
```

The provider should return or stage an asset through a controlled mechanism defined by the acquisition engine.

The provider must not place files directly into a destination library; it returns
them only to Family Librarian-controlled staging.

### Capability Examples

```text
ebook
audiobook
search
availability
acquire
requires-account
requires-api-key
manual
```

### Scheduled provider checks

An installed provider must not acquire automatic polling merely by advertising a
manifest capability. The core configuration is the authority for whether an
otherwise-approved provider may be checked automatically and how often. The
initial policy vocabulary is deliberately small:

```text
ONCE     A stable/public-domain source is checked once per requested format.
DAILY    A changing, approved source may be checked no more than once per day.
WEEKLY   A lower-priority approved source may be checked no more than once per week.
MANUAL   No background lookup; an administrator explicitly checks it.
```

The bundled Project Gutenberg implementation has effective `ONCE` behavior. Registered
external providers default to `MANUAL`; an administrator may explicitly select
`DAILY` or `WEEKLY` for each enabled provider. The application owns that policy,
not the provider manifest.

Each automatic lookup creates an append-only, administrator-only
provider-attempt entry containing provider ID, request format, attempt time,
outcome (`match`, `no-match`, `ambiguous`, `blocked`, or `failed`), a safe
summary, and next eligible check time. It must not record credentials, complete
untrusted provider payloads, or downloadable artifact URLs. A provider's
declared egress policy applies to every scheduled check and must still fail
closed; a failed private route never permits a normal-egress retry.

The newest attempt for each provider is also projected into the administrator's
in-app attention summary when its outcome is `failed` or `blocked`. This is a
safe operational indicator, not a replacement for the append-only ledger: it
contains only provider display name/ID, the bounded safe summary, and the time.
It must never expose credentials, provider payloads, requesters, or artifact URLs.

Provider availability is not page availability. Calls that populate optional
availability, store-offer, direct-acquisition, owned-library, or external-source
options isolate transport failures and provider-owned timeouts per provider.
They return the remaining options (or none) while preserving caller-requested
cancellation. Background acquisition calls still surface the same failures to
the provider-attempt ledger and administrator attention projection.

### Private-egress policy

Family Librarian **SHALL NOT** depend on a specific commercial VPN provider.
An acquisition provider may declare `PRIVATE_REQUIRED` or inherit that policy
from its server-side configuration. For an in-process provider, the policy
applies to every outbound interaction with its source: authentication, search,
result and detail lookup, artifact resolution, download-URL resolution, and
direct download. A private provider must not leak any of those steps via normal
host egress.

The generic private-acquisition configuration is server-side and represents a
gateway endpoint rather than a VPN service:

```text
Private Acquisition Network
  Enabled
  Gateway type: HTTP proxy | SOCKS5 proxy | external route (future)
  Gateway endpoint
  Require private egress
  Fail closed
  Health/status where available
```

For example, an HTTP proxy endpoint can be `http://gluetun:8888`, but the
provider never needs to know which VPN service, if any, backs that gateway.
Gluetun is the documented reference implementation, not a hard dependency;
custom WireGuard/OpenVPN gateways, router-level routing, and compatible proxy
gateways remain valid.

When private egress is required, an unavailable or unhealthy gateway blocks the
operation. The engine records a policy-blocked, waiting, or error state and can
retry or notify an administrator later; it must not silently fall back to normal
Internet access. `CUSTOM_PROXY` similarly requires an explicitly configured
proxy and must not imply an automatic fallback policy.

### Future external sourcing providers

External sourcing is a planned extension of the acquisition-provider boundary,
not part of the initial catalog/search slice. The application should leave room
for an administrator to enable and configure additional **vetted** sourcing
providers in the future, alongside built-in providers such as Manual, library
availability, public-domain, or commercial integrations.

The future integration model should preserve these boundaries:

- a provider has a stable ID, protocol version, declared capabilities, and a
  server-side configuration schema;
- an administrator may enable, disable, configure, and test an installed provider,
  but stored provider credentials and sensitive connection details are never sent
  back to the browser after submission;
- search results remain candidates with provider provenance and must not
  automatically create, download, or import an asset;
- acquisition remains an explicit, authorized workflow step with audit history,
  policy checks, and malware/file validation before an item can enter the trusted
  library; and
- external implementations communicate over the versioned HTTP protocol or run in
  isolated containers. The main application does not load arbitrary provider code.

Provider-source implementations remain independent of VPN-provider
implementations. For example, a private source provider may require
`PRIVATE_REQUIRED` and use the generic gateway; it must not embed Proton- or
Mullvad-specific tunnel logic.

This creates a clear future administration/settings surface for source management
without committing V1 to acquisition automation, a plugin marketplace, or support
for unreviewed sources.

---

## 6. Acquisition Provider Isolation

Preferred long-term deployment:

```text
family-librarian-acquisition
  |
  +--> provider-a container
  +--> provider-b container
  +--> provider-c container
```

Benefits:

- provider language independence;
- failure isolation;
- tighter filesystem access;
- secrets scoped per provider;
- easier third-party distribution.

A trusted built-in Manual Provider may exist in the acquisition engine.

For an external component that actually contacts private services, prefer
putting the entire component behind the private-egress gateway rather than
relying only on its application-level proxy setting. A Docker/Gluetun reference
deployment can use `network_mode: "service:gluetun"` for an isolated private
provider. The gateway's firewall/kill switch then governs that component's
outbound traffic.

Family Librarian may call those components over internal APIs, but that does not
protect the components' own outbound traffic: each provider's complete
interaction, including authentication, discovery, artifact resolution, and
transfer, must be routed by the gateway when required. The main application
retains normal LAN/Internet networking and must not need
`NET_ADMIN`, `NET_RAW`, privileged mode, or tunnel-management responsibility.

The same boundary supports a future out-of-process model:

```text
Family Librarian --> provider API --> private provider container
                                  --> VPN/private-egress gateway --> Internet
```

That is a future isolation option, not a V1 requirement solely for VPN support.

External providers receive only the minimum scoped configuration, credentials,
network route, and temporary staging access they require. They never receive the
Family Librarian database, user-account data, OIDC credentials, unrelated
provider credentials, a final-library filesystem path, or Docker-socket access.
The provider returns an artifact to controlled staging; it never selects the
destination-library path. Trusted bundled .NET providers may use in-process
dependency isolation where appropriate, but that is not a security sandbox and
must not be offered for arbitrary community code.

Provider repositories are catalogs, not a requirement to run a marketplace or a
binary registry. A repository may initially be a static signed JSON document
listing provider identity, protocol version, capabilities, source/license,
publisher/trust metadata, and an immutable OCI image digest. Multiple catalogs
may be configured, but installation is an explicit administrator deployment
step. Family Librarian must not mount or control the Docker socket merely to
install or update external providers.

---

## 7. Malware Scanner Provider

Conceptual contract:

```csharp
public interface IMalwareScanner
{
    string Id { get; }
    string Name { get; }

    Task<MalwareScanResult> ScanAsync(
        Stream asset,
        AssetScanContext context,
        CancellationToken cancellationToken);
}
```

Result:

```text
Status:
  Clean
  Detected
  Error
  Unavailable

EngineVersion
SignatureVersion
ThreatName
Details
Duration
```

Initial provider:

```text
ClamAV
```

Potential future providers:

```text
Commercial multi-engine scanner
Private malware analysis service
YARA-based local scanner
```

### Policy

Scanner configuration must support:

```text
RequiredScanners
OptionalScanners
PassPolicy
OnUnavailable
OnDetected
```

Example:

```yaml
security:
  required_scanners:
    - clamav

  pass_policy: all-required-pass
  on_unavailable: hold
  on_detected: quarantine
```

`on_unavailable: hold` is fail-closed acquisition policy, not merely a later
approval result. When any required scanner is unhealthy, the host rejects manual
file uploads before reading file bytes and prevents acquisition providers and
linked-library adapters from downloading or staging files. It records a
`WaitingForSecurityScanner` request/job state instead. Metadata/catalog search
remains permitted. The pilot implementation also refuses creation of a request
format that is not ready because its scanner or destination is unhealthy; it
does not create a request that cannot be processed safely.

If a scanner becomes unavailable after an ingress operation has started, the
asset stays quarantined and no destination, download, or notification operation
may use it. Scanner recovery triggers a controlled, auditable retry; it never
causes an unscanned fallback or requires the user to re-request the book.

---

## 8. Format Validator Contract

Separate malware detection from format validation.

Conceptual:

```csharp
public interface IAssetValidator
{
    string Id { get; }

    bool Supports(MediaType mediaType, string format);

    Task<AssetValidationResult> ValidateAsync(
        AssetContext asset,
        CancellationToken cancellationToken);
}
```

Initial validators:

```text
FileTypeValidator
EpubValidator
AudioValidator
```

Potential checks:

- MIME sniffing;
- archive structure;
- EPUB manifest;
- unexpected embedded file types;
- ffprobe parsing;
- file-size limits;
- duration sanity;
- extension/type mismatch.

---

## 9. Notification Provider

Conceptual:

```csharp
public interface INotificationProvider
{
    string Id { get; }
    NotificationCapabilities Capabilities { get; }

    Task<NotificationResult> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken);
}
```

Capabilities:

```text
Email
Push
ActionButtons
DeepLinks
DeliveryReceipt
Batch
```

Implemented provider:

```text
SMTP outbound email (optional)
```

Possible future providers:

```text
ntfy
Pushover
Web Push
Home Assistant
Discord
Telegram
```

---

## 10. Actionable Notification Security

Never embed permanent service credentials in a notification.

Use a short-lived action record:

```text
NotificationAction
  ActionId
  UserId
  ActionType
  EntityId
  TokenHash
  ExpiresAt
  UsedAt
```

Example URL:

```text
https://family-librarian.example/action/<opaque-token>
```

Actions should be classified:

```text
OneTapSafe
Authenticated
AdminOnly
```

Examples:

### OneTapSafe

- request next book;
- accept recommendation;
- request whole series.

### Authenticated

- approve normal acquisition candidate;
- change delivery preference.

### AdminOnly

- override malware warning;
- accept malformed file;
- change provider security policy.

---

## 11. Delivery Provider

Use one generic delivery contract.

Conceptual:

```csharp
public interface IDeliveryProvider
{
    string Id { get; }
    DeliveryCapabilities Capabilities { get; }

    Task<DeliveryPreparationResult> PrepareAsync(...);
    Task<DeliveryResult> DeliverAsync(...);
}
```

Provider categories:

```text
Device
Cloud
MediaLibrary
```

Capabilities may include:

```text
Ebook
Audiobook
Streaming
Download
UsbFilesystem
CloudSend
MediaLibraryImport
OfflineSync
UserMapping
DeepLink
```

`MediaLibraryImport` is a delivery capability for a media service such as
Audiobookshelf. It is not the `ILibraryDestination` contract used to publish an
ebook to CWA before any user-specific delivery occurs. A provider may implement
either capability, or both, for ebook, audiobook, or both media types.

---

## 12. Audiobookshelf Provider

Implementation responsibilities:

1. Determine configured library.
2. Stage approved audio.
3. Atomically move/copy into library path.
4. Trigger library scan.
5. Locate imported item.
6. Record external item ID.
7. Return a deep link if practical.
8. After verified import, allow Family Librarian to delete its transient staging
   copies while retaining checksum, validation/approval evidence, and the
   provider reference.

Family Librarian should not store Audiobookshelf-specific fields in core
Work/Request entities. This rule applies equally to CWA and every future
destination.

Store external references against Delivery/DeliveryTarget.

The provider reports whether a specific owned asset is available in its library;
it does not determine ownership. The application may therefore import an owned
audiobook that is not in Audiobookshelf, or directly offer a deep link for one
that is already present, while retaining the option to support another
media-library provider later.

---

## 13. E-Reader Device Providers

Possible providers:

```text
KindleBrowserFilesystem
KindleSendTo
KoboBrowserFilesystem
GenericMassStorage
DesktopAgent
FutureEbookOrAudioLibrary
```

Do not expose a core method named:

```text
SendToKindle()
```

Core should request:

```text
Deliver(asset, target)
```

The provider decides the mechanism.

None of `KindleBrowserFilesystem`, `KindleSendTo`, `KoboBrowserFilesystem`,
`GenericMassStorage`, or `DesktopAgent` exist in the codebase yet — this
section, and `docs/01-product-architecture-spec.md` §15.1 / `docs/02-domain-workflows.md`
("DeliveryAttempt"), describe the intended shape for when that work starts
(`post-v1-roadmap.md` Milestone G / Spike C), not current behavior.

A destination such as a Kindle should support more than one `IDeliveryProvider`
implementation (`KindleSendTo`, then later `KindleBrowserFilesystem`), with a
per-user preferred/fallback ordering resolved the same way
`ProviderPolicyProfile` already resolves acquisition preference (§3 above) —
this is a second, independent policy scope (`delivery`), not a reuse of the
acquisition ranking rules, since "prefer the free acquisition source" and
"prefer Send-to-Kindle over a manual download" are unrelated decisions.

`DeliverAsync` must report a status that separates *submitted* from
*confirmed*: Amazon's Send-to-Kindle path only ever gives a submission
acknowledgement, never proof the file reached the device. A provider that
cannot confirm delivery should return a status such as `SubmittedToAmazon`
rather than `Delivered`, so the request/history UI can offer "Didn't receive
it" -> retry or fall back to another method without losing the original
request. Each attempt (provider, method, timing, status, failure reason)
should be retained rather than overwritten, mirroring how `AcquisitionJob`
already lets one request accumulate multiple acquisition attempts.

A browser/WebUSB device detector is a future capability of a
`DirectDevice`-style provider specifically, not a new core contract: it
changes which delivery method is *offered* (and can move a delivery attempt to
an `AwaitingDevice` status while the artifact finishes acquisition/processing
with no device connected), but it must not change how acquisition or CWA
publishing work. `PrepareAsync`/`DeliverAsync` already give a provider room to
implement that without a new interface.

---

## 14. Authentication Provider Boundary

Authentication differs slightly from the other provider types because ASP.NET Core middleware will own much of it.

Production application modes:

```text
Local
Local + OIDC
```

`Local` remains available in `Local + OIDC` as the administrator recovery path.
There is intentionally no ordinary deployment setting for OIDC-only or an
authentication bypass. Test hosts may register a deterministic test scheme that
issues only fixture User/Admin identities; that scheme is test-only and must not
be selectable from normal application configuration.

OIDC configuration is generic and uses a confidential authorization-code client
with PKCE plus the host's cookie session. Authentik is a recommended/tested
target, but it is not a dependency and no Authentik-specific group or role is
stored in the domain. A configured issuer's validated claims/groups map through
an explicit allowlist to Family Librarian's own `User` and `Admin` roles.

Authentik gets:

- tested documentation;
- example claim/group mappings;
- sample Docker/reverse-proxy configuration.

It should not receive bespoke business logic unless unavoidable.

Every development, staging, and production environment uses a separate OIDC
registration, client ID/secret, and redirect/sign-out URI. One IdP installation
may host all of those registrations. The configured canonical issuer must be
reachable both by the browser and by the Family Librarian process/container: the
browser follows authorization redirects and returns to the public callback URI,
while the host uses discovery, token, and signing-key/JWKS endpoints. Do not
paper over this distinction with different issuer values; use safe DNS/reverse
proxy routing instead.

Client secrets belong only in the environment's secret provider (or a local
developer secret mechanism). They are never checked in, returned to the browser,
or logged. Normal automated tests use injected identities and never require an
Authentik instance. An opt-in integration suite may run a disposable,
standards-compliant OIDC provider to validate discovery, callback, external-login
linking, and claims mapping; Authentik in Docker is an acceptable test target.

---

## 15. Service-to-Service Authentication

For Family Librarian-owned services:

Preferred mechanisms:

```text
OAuth2 client credentials
or
scoped service tokens
```

If an external OIDC issuer is configured, service identities may use it.

The application should also have an open-source-friendly fallback so Authentik is not required.

Service credentials should be scoped.

Example:

```text
acquisition.read-jobs
acquisition.submit-result
delivery.report-status
```

---

## 16. Versioning

HTTP plugin protocol:

```text
protocolVersion
```

Recommended compatibility policy:

```text
Major = breaking
Minor = additive
Patch = documentation/bug behavior
```

Provider manifest should advertise protocol support.

---

## 17. Provider Configuration UX

Configuration and operational administration should be separate. Settings must
be organized into dedicated areas, rather than a single Integrations screen that
links between unrelated concerns:

```text
Metadata providers
Sources and private acquisition network
Security
Notifications
Publishing destinations
Linked ebook libraries
Authentication
```

Operational pages belong in the administrator workspace: queue, security, and
publishing activity/delivery history. A settings page configures a destination;
it does not also contain its activity log.

For each integration:

```text
Enabled
Status
Version
Capabilities
Configuration
Test Connection
Last Successful Use
Last Error
```

Secrets must never be returned to the browser after storage.

The Private Acquisition Network settings use the generic gateway fields above,
not VPN-provider credentials or provider-selection controls. VPN tunnel
configuration, DNS, kill-switch, IPv4/IPv6 leak prevention, reconnection, and
WireGuard/OpenVPN details belong to the external gateway/deployment. Health
status must distinguish an unavailable required gateway from a general provider
failure, without exposing gateway credentials.

### Credential lifecycle

The normal self-hosted setup path for a provider API key or token is its
Admin-only, concern-specific settings UI backed by same-origin host API commands. A newly entered
secret necessarily exists transiently in the administrator's browser while being
submitted over HTTPS; the host must never send a stored secret back to the client.

The UI and API must follow these rules:

- require server-side Admin authorization and anti-forgery protection for enable,
  disable, create, replace, clear, and test-connection operations;
- accept a secret only as a write-only value, clear the input after submission,
  and return only state such as `NotConfigured`, `Configured`, or
  `ExternallyManaged`, plus a last-changed timestamp;
- never place secrets in URLs, browser storage, API response DTOs, validation
  messages, logs, audit payloads, health output, or raw provider snapshots;
- encrypt application-managed secrets at rest with a deployment-specific,
  persistent protection key and use purpose separation per provider/secret type;
- persist and back up the protection key independently of the application
  container. If ASP.NET Core Data Protection is used, its key ring must survive
  container replacement and be protected at rest; losing the key ring makes
  protected provider secrets unrecoverable;
- audit who changed, cleared, enabled, disabled, or tested an integration without
  recording the secret value;
- perform connection tests on the server and return a redacted, actionable status;
  and
- support deployment-provided secrets as a read-only alternative. When an
  environment/secret-manager value takes precedence, show `ExternallyManaged` and
  do not allow the UI to reveal or overwrite it.

Replacing a credential is the rotation operation. Clearing one disables provider
calls that require it until a new credential is configured. Provider adapters
receive only their scoped configuration and never database or unrelated provider
credentials.

For the .NET 10 host, the protection-key persistence and encryption design must be
reviewed against the current
[ASP.NET Core Data Protection guidance](https://learn.microsoft.com/aspnet/core/security/data-protection/configuration/default-settings?view=aspnetcore-10.0),
especially for Docker deployments.

---

## 18. V1 Provider Implementation Targets

### Required

```text
Metadata:
  Google Books and/or Open Library

Acquisition:
  Manual

Linked ebook libraries:
  Calibre-Web (catalog/source via configured OPDS surface)
  Calibre-Web Automated (CWA, opt-in ingest destination)

Security:
  ClamAV
  File type validator
  EPUB validator
  Audio validator

Notifications:
  SMTP outbound email (optional)

Delivery:
  CWA (initial ebook library destination)
  Audiobookshelf (initial audiobook library destination)
  Authenticated download (optional, later)
```

### Strong Candidate for Early Addition

```text
Generic OIDC (implemented, optional)
ntfy
Hardcover metadata
```

### Prototype Only

```text
Browser filesystem Kindle/Kobo transfer
WebUSB
```

---

## 19. Testing Strategy for Providers

Every contract should ship with a provider conformance test suite.

Examples:

```text
MetadataProviderContractTests
AcquisitionProviderProtocolTests
PrivateEgressPolicyTests
MalwareScannerContractTests
NotificationProviderContractTests
DeliveryProviderContractTests
LinkedLibraryProviderContractTests
```

A third-party provider author should be able to verify:

```text
"My provider complies with protocol version 1."
```

without requiring Family Librarian's internal source code or database.

Private-egress conformance tests must confirm that `PRIVATE_REQUIRED` blocks
the whole provider interaction when its gateway is unavailable and that no
normal-egress fallback is attempted.
