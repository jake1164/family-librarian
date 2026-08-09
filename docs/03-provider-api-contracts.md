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

---

## 3. Metadata Provider

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

---

## 4. Acquisition Provider

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
  ]
}
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

The provider must not place files directly into the trusted library.

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

This creates a clear future administration/settings surface for source management
without committing V1 to acquisition automation, a plugin marketplace, or support
for unreviewed sources.

---

## 5. Acquisition Provider Isolation

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

---

## 6. Malware Scanner Provider

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

---

## 7. Format Validator Contract

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

## 8. Notification Provider

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

Initial providers:

```text
Email
ntfy
```

Possible future providers:

```text
Pushover
Web Push
Home Assistant
Discord
Telegram
```

---

## 9. Actionable Notification Security

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

## 10. Delivery Provider

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
LibraryImport
OfflineSync
UserMapping
DeepLink
```

---

## 11. Audiobookshelf Provider

Implementation responsibilities:

1. Determine configured library.
2. Stage approved audio.
3. Atomically move/copy into library path.
4. Trigger library scan.
5. Locate imported item.
6. Record external item ID.
7. Return a deep link if practical.

Family Librarian should not store Audiobookshelf-specific fields in core Work/Request entities.

Store external references against Delivery/DeliveryTarget.

---

## 12. E-Reader Device Providers

Possible providers:

```text
KindleBrowserFilesystem
KindleSendTo
KoboBrowserFilesystem
GenericMassStorage
DesktopAgent
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

---

## 13. Authentication Provider Boundary

Authentication differs slightly from the other provider types because ASP.NET Core middleware will own much of it.

Required application modes:

```text
Local
OIDC
Local + OIDC
```

OIDC configuration should be generic.

Authentik gets:

- tested documentation;
- example claim/group mappings;
- sample Docker/reverse-proxy configuration.

It should not receive bespoke business logic unless unavoidable.

---

## 14. Service-to-Service Authentication

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

## 15. Versioning

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

## 16. Provider Configuration UX

Admin Integrations screen should show:

```text
Metadata
Acquisition
Security
Notifications
Delivery
Authentication
```

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

### Credential lifecycle

The normal self-hosted setup path for a provider API key or token is an
Admin-only Integrations UI backed by same-origin host API commands. A newly entered
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

## 17. V1 Provider Implementation Targets

### Required

```text
Metadata:
  Google Books and/or Open Library

Acquisition:
  Manual

Security:
  ClamAV
  File type validator
  EPUB validator
  Audio validator

Notifications:
  Email

Delivery:
  Authenticated download
  Audiobookshelf
```

### Strong Candidate for Early Addition

```text
OIDC
ntfy
Hardcover metadata
```

### Prototype Only

```text
Browser filesystem Kindle/Kobo transfer
WebUSB
```

---

## 18. Testing Strategy for Providers

Every contract should ship with a provider conformance test suite.

Examples:

```text
MetadataProviderContractTests
AcquisitionProviderProtocolTests
MalwareScannerContractTests
NotificationProviderContractTests
DeliveryProviderContractTests
```

A third-party provider author should be able to verify:

```text
"My provider complies with protocol version 1."
```

without requiring Family Librarian's internal source code or database.
