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
