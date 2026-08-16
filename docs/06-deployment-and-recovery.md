# Family Librarian — Deployment, Backup, and Recovery

Family Librarian runs as three Compose services:

```text
postgres             persistent PostgreSQL data
migrate              one-shot EF Core migration job
family-librarian     ASP.NET Core API and hosted Blazor WebAssembly application
```

`migrate` must finish successfully before `family-librarian` starts. It uses the
same application image with the `--migrate` command, so the schema reviewed in
source control is the schema deployed by Compose. Do not replace it with
`EnsureCreated`, and do not run migrations from every application replica.

## Optional linked ebook libraries

Calibre-Web and Calibre-Web Automated (CWA) are optional integrations, not
services in the default Compose deployment. A configured Calibre-Web source is
reached over the server-side catalog/OPDS interface with a least-privilege
account; its credentials belong in the deployment's secret mechanism and are
never supplied to the browser.

CWA is the initial automated ebook-library destination. Its three main volumes
must remain distinct: CWA configuration, the Calibre library, and the watched
ingest directory. The managed Calibre library is the permanent ebook store.
Family Librarian uses only short-lived quarantine/processing/outbound storage
and gets write access only to a dedicated outbound staging location plus the CWA
ingest directory. It must not mount or write CWA's `metadata.db` directly.

The application copies a complete approved file to outbound staging and then
atomically hands it to the ingest directory. Do not download directly into that
watched directory: CWA documents that partial files can create duplicate imports
or database corruption. Disable CWA's automatic conversion, metadata rewriting,
EPUB fixing, and auto-send for the initial integration; enable them only after a
future adapter can verify the final processed result.

Back up the complete Calibre library directory (`metadata.db` and all book
folders) and CWA configuration separately from PostgreSQL. Test restoring those
volumes together before relying on CWA as the household's ebook library. On
network shares, use CWA's documented network-share mode and prove ingest and
recovery behavior in the deployment-specific compatibility test.

Audiobookshelf is the initial permanent audiobook store. Back up its persistent
configuration and library volumes separately from PostgreSQL, and test their
restore with the same care. These are initial adapters, not an exclusive list:
each future ebook, audiobook, or combined-library destination must document its
own isolated credentials, import verification, format support, and backup/
recovery procedure.

## Required malware scanner for file acquisition

ClamAV is optional only for deployments that do not acquire or accept ebook or
audiobook files. Once manual upload, a linked-library stage, or an acquisition
provider is enabled, the deployment must configure a required scanner and include
its health in the acquisition readiness check.

An unavailable required scanner fails closed: the host continues catalog search
and request creation, but rejects uploads before accepting file bytes and does
not begin provider downloads or linked-library staging. Existing requests remain
in `WaitingForSecurityScanner` for automatic, auditable backfill after scanner
health recovers. If the scanner fails during ingress, retain the affected file in
quarantine and do not publish it to CWA, Audiobookshelf, a download endpoint, or
a notification.

## Deploy or upgrade

1. Back up PostgreSQL before changing the image, Compose configuration, or
   application version.
2. Fetch the selected immutable application image/tag and the matching Compose
   configuration.
3. Supply production values through the deployment's secret mechanism. At
   minimum, set the PostgreSQL password and bootstrap credentials if the
   installation has not created an administrator yet. Never commit these
   values.
4. Start the stack:

   ```bash
   docker compose up -d
   ```

5. Confirm the migration job completed and the application is healthy:

   ```bash
   docker compose ps
   docker compose logs migrate
   curl --fail http://localhost:8080/health/live
   curl --fail http://localhost:8080/health/ready
   ```

The `migrate` service is intentionally expected to be exited with status zero.
The application waits for that successful completion and the PostgreSQL health
check. A failed migration is a deployment failure: leave the application
stopped, retain the logs and backup, and investigate before attempting another
upgrade.

EF Core records each applied migration in its migration history table, so the
one-shot job applies only pending migrations. Family Librarian tests the forward
upgrade path from the M5 request-workflow schema to the current schema. As with
all EF Core migrations, do not downgrade a production database casually: a
rollback executes migration `Down` operations and may lose data. See
[Microsoft's migration deployment guidance](https://learn.microsoft.com/ef/core/managing-schemas/migrations/applying).

## Back up PostgreSQL

Create an output directory owned by the operator, then make a compressed custom
format PostgreSQL backup. The command keeps the database password inside the
container rather than placing it in a command-line argument or shell history.

```bash
mkdir -p backups
docker compose exec -T postgres \
  pg_dump -U family_librarian -d family_librarian -Fc \
  > backups/family-librarian-$(date +%F-%H%M%S).dump
```

Protect the backup with the same care as the database: it contains account
emails, request history, provider configuration ciphertext, and the persisted
Data Protection key ring. Store at least one encrypted copy away from the host
running the application, and verify restores periodically.

The Compose volume is durable only while the Docker volume exists. `docker
compose down` preserves it; `docker compose down -v` destroys it. Do not use
the latter as an upgrade or troubleshooting step unless a complete local reset
is intended.

## Restore a backup

Restoring replaces the database contents. First stop the application so no
request, account, or provider-setting write races the restore:

```bash
docker compose stop family-librarian
docker compose exec -T postgres \
  pg_restore -U family_librarian -d family_librarian \
  --clean --if-exists --no-owner --no-privileges \
  < backups/family-librarian-YYYY-MM-DD-HHMMSS.dump
docker compose up -d
```

Use a backup created from the same PostgreSQL major version or a compatible
newer `pg_restore` client. After restart, inspect `migrate` and verify both
health endpoints before allowing normal use. The migration job may apply
forward-only schema changes if the restored backup predates the deployed image.

## Reverse proxy and secrets

Place a TLS-terminating reverse proxy in front of port 8080 for an internet
reachable deployment. The application uses cookie authentication, so ensure the
proxy forwards the canonical scheme and host and does not cache API responses.
Use the public HTTPS URL consistently in browser bookmarks and, when M6.5 OIDC
is added, in the registered callback and sign-out URIs.

Keep PostgreSQL credentials, bootstrap credentials, OIDC secrets, and provider
credentials outside the repository. Provider credentials entered through the
administrator UI are encrypted using the persisted Data Protection key ring in
PostgreSQL; that is why a database backup is required for both credential and
session continuity after recovery.
