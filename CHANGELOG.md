# Changelog

All notable changes to Family Librarian are documented here. Newest release at the top.

---

## [v1.0.0-alpha.1] — 2026-09-10

The first alpha release of Family Librarian. Everything below was built from scratch over the last five weeks: local and OIDC/Authentik sign-in, metadata search across Google Books/Open Library/Project Gutenberg, Calibre-Web Automated and Audiobookshelf ingestion, a request/queue pipeline with real-time updates, Kindle delivery with delivery confirmation, malware scanning on every incoming file, SMTP notifications, and Postgres backup/restore — this entry is the baseline the rest of the changelog builds on.

### Authentication and administration

- Local sign-in via ASP.NET Core Identity, with an admin-driven password reset path for locked-out users and auditing of failed login attempts
- Optional generic OIDC support, with Authentik as the first tested and documented identity provider — OIDC supplements local sign-in rather than replacing it
- Authentik-originated accounts activate immediately after sign-in, using the IdP's own access grant as the gate rather than a separate admin-approval step (#14)
- First-run bootstrap creates the initial administrator from `.env`, and only while no administrator exists yet

### Book search and metadata

- Unified metadata search across Google Books and Open Library, with progressively "loosened" matching so partial or messy author/title input still finds the right book
- Language-aware search favors results in the reader's preferred language instead of surfacing mismatched translations
- Edition title, cover, and publisher are swapped as a single unit, so a translated Open Library edition can't show its own title paired with the original edition's cover and publisher
- Added a first-party Project Gutenberg integration for public-domain titles, with incremental daily catalog updates, download-status indicators, and resilience against Gutenberg/Cloudflare outages
- Consolidated the multi-step "pick the right edition" flow into a single display page, with pagination on search results, a "back to top" affordance on long pages, and outbound links to the original source page
- The back button after a search returns to the existing result list instead of re-running a fresh search

### Library sources and ingestion

- Calibre-Web Automated (CWA) integration: OPDS-based ownership lookups, automated ingest of approved files, and post-ingest verification that the book actually appears in the library
- Audiobookshelf integration for audiobook ingest and delivery
- Added a separate **Public URL** setting for CWA/Audiobookshelf so "open in library" links resolve for a family member's home browser instead of pointing at the Docker-internal hostname
- Added SFTP as an ingest transport for CWA, with a sweep that removes any stale `.{guid}.uploading` temp file older than 15 minutes — left behind by a dropped connection or crash mid-upload — before a new upload starts
- Source health reporting separates a single failing book (bad match, transient error) from a genuinely broken source, so one bad title doesn't make the whole source look down
- Uploads are tracked per user, so a manually-acquired file is attributed to the request that needed it

### Request and queue pipeline

- Fulfilled requests move to a History list instead of being deleted, and a dashboard task/status view tracks in-flight acquisition and processing work
- Added a **Needs Attention** queue that surfaces requests stuck in an exceptional state (failed match, scanner hit, etc.) separately from normal in-progress work
- Concurrent requests for the same book are deduplicated, so two users requesting it at the same time trigger a single download and processing run instead of two
- Real-time UI updates switched from polling to SignalR — a shared per-tab connection with reconnect/backoff and a connection-status indicator, so queue and request status now update live
- A book that has already been received no longer shows resend options to the requester

### Kindle delivery

- Built the full "Send to Kindle" pipeline: per-user delivery targets, tracked delivery attempts, and delivery history visible to both requesters and admins
- Added bounded automatic retry with cooldowns for a send that fails or whose outcome is unknown, and delivery-confirmation checks that distinguish "submitted to Amazon" from "confirmed delivered"
- Added the ability to send an already-owned book straight to a Kindle without re-running acquisition and scanning
- Sending an already-owned book to a Kindle requires explicit confirmation when the match was only by title/author rather than a verified identifier like ISBN, so the wrong edition can't be delivered on a low-confidence match
- Delivery actions are scoped to the requesting user's own target

### Notifications

- Added SMTP-based outbound email notifications with per-event routing
- Outbound SMTP TLS connections no longer fail against a household's own mail relay with an internal CA: MailKit's online revocation (CRL/OCSP) check is disabled while full chain, trust, expiry, and hostname validation remain in place
- Outbound notification delivery is tracked and persisted per-message immediately after sending, so a dispatcher restart mid-batch can't cause a notification to be resent to a family member who already received it

### Security

- Every incoming file passes through a virus/malware scan before it reaches a library, including ARM64 (Apple Silicon / Raspberry Pi-class) ClamAV compatibility and container image scanning in CI
- Malware-handling policy: confirmed malware is destroyed immediately with an auditable record; invalid-format rejections sit in an admin review queue until explicitly deleted; quarantined files can be retried or deleted; any scanner reporting malware fails the file closed even if that scanner was optional

### Backup and restore

- Added backup/restore for the Postgres database and related integration data, with a real recovery procedure replacing the earlier stale docs
- Backup/restore scripts validate before acting: disabled integrations are skipped, a non-empty restore target is refused, checksum mismatches are detected, and a failed verification aborts the restore
- `create-backup.sh` runs correctly under bash <4.4, including macOS's stock system bash, with both optional integrations disabled — the documented default configuration

### Dashboard and UI

- Added availability badges (owned/requested/waiting/etc.) that turn green as soon as content is actually available and don't show a stale state mid-search
- Ebook/audiobook chips on search and detail pages now link out to the external book page
- Cleaned up the footer, consolidated scattered refresh messaging, added a task bar, reduced the visual size of the security-scan panel on the dashboard, and cleaned up the request/history trail for readability

### Testing

- Added CI: a zero-warning build, the full test suite including Testcontainers-based Postgres integration tests, NuGet vulnerable-package scanning, the backup/restore script test suite, and container image vulnerability scanning (CRITICAL/HIGH CVEs) on the published image
- Added Playwright browser-based end-to-end tests
- Added a migration/upgrade test suite validating schema upgrades between versions

**Container images**

```text
ghcr.io/jake1164/family-librarian:v1.0.0-alpha.1
ghcr.io/jake1164/family-librarian:alpha
```
