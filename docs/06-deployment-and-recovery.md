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

CWA ingest transport (local/shared filesystem, or SFTP for a CWA host that
does not share a filesystem with Family Librarian) is configured independently
of the HTTP(S)/OPDS catalog connection that Family Librarian also needs
against the same CWA instance for ownership lookup and post-ingest
verification. Both are required in every deployment topology, local or
remote: configure the OPDS base URL and account alongside whichever ingest
transport applies, and confirm the OPDS connection test succeeds, not only the
ingest connection test. A deployment with a working ingest transport but no
reachable OPDS endpoint will keep re-acquiring books CWA already has and will
never confirm that an ingested file actually imported successfully.

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

Use the versioned full-backup tooling in `scripts/backups/` for every normal
backup. It creates an atomic, permission-restricted backup set containing the
PostgreSQL custom-format dump, selected CWA/Audiobookshelf configuration and
library archives, a versioned manifest, and SHA-256 checksums. The PostgreSQL
password stays inside the Compose container rather than appearing in shell
history.

Copy `scripts/backups/backup.env.example` to a directory outside the repository
that only the backup operator can read. Configure each installed permanent
library as `directories` and supply the host-visible configuration and library
paths; set a library to `disabled` only when that integration is not installed.
The secret recovery reference is a runbook/secret-manager identifier, never a
raw certificate, password, or access token.

```bash
install -d -m 700 /etc/family-librarian
cp scripts/backups/backup.env.example /etc/family-librarian/backup.env
chmod 600 /etc/family-librarian/backup.env
# Edit the copied file, then create and verify a backup set:
scripts/backups/create-backup.sh --config /etc/family-librarian/backup.env
scripts/backups/verify-backup.sh --backup /var/backups/family-librarian/family-librarian-YYYYMMDDTHHMMSSZ
```

The scripts intentionally run on the deployment host rather than inside the web
container. A web-container scheduler would need Docker-daemon access to run
`pg_dump` and reach arbitrary CWA/Audiobookshelf directories, effectively
granting the browser-facing app host-level control. For a remote library, mount
or otherwise expose the directories through a trusted host path, or use that
library's own backup facility and do not call the resulting set complete until
it is included and verified.

Protect every backup set with the same care as the database: it contains account
emails, request history, provider configuration ciphertext, and the persisted
Data Protection key ring. Store at least one encrypted copy away from the host
running the application, and verify restores periodically.

### Schedule daily or weekly backups

The scheduler is opt-in and host-managed. Generate the cron line for the chosen
frequency, inspect it, then add it to the backup operator's crontab. Daily runs
at 02:15 local server time; weekly runs at 02:15 every Sunday. The script only
prints a line—it never edits a crontab itself.

```bash
scripts/backups/print-cron-entry.sh \
  --frequency daily \
  --repository /srv/family-librarian \
  --config /etc/family-librarian/backup.env \
  --log /var/log/family-librarian-backup.log

# Use --frequency weekly for the weekly alternative.
```

Test a backup manually before enabling a schedule, monitor the generated log,
and arrange off-host replication/encryption separately. Scheduling a backup is
not evidence that it can be restored.

The Compose volume is durable only while the Docker volume exists. `docker
compose down` preserves it; `docker compose down -v` destroys it. Do not use
the latter as an upgrade or troubleshooting step unless a complete local reset
is intended.

## Restore a backup

Restoring replaces PostgreSQL. The restore tooling requires an explicit
`--confirm-replace-postgres`, verifies every checksum first, and refuses to
write CWA/Audiobookshelf data into a non-empty target directory. Stop the
external CWA/Audiobookshelf services before restoring their files; the script
does not control containers outside this Compose project.

```bash
scripts/backups/restore-backup.sh \
  --backup /var/backups/family-librarian/family-librarian-YYYYMMDDTHHMMSSZ \
  --confirm-replace-postgres \
  --cwa-configuration-target /srv/restore/cwa-config \
  --cwa-library-target /srv/restore/cwa-library \
  --audiobookshelf-configuration-target /srv/restore/audiobookshelf-config \
  --audiobookshelf-library-target /srv/restore/audiobookshelf-library
```

Omit a destination pair only if the backup manifest records that integration as
disabled. The destination directories must be new or empty; after the restore,
attach them to the appropriate external services according to their own
deployment documentation. Use a backup created from the same PostgreSQL major
version or a compatible newer `pg_restore` client. The script starts the
one-shot migration job and Family Librarian, but do not reopen the deployment
until the migration logs and both health endpoints have passed.

## Initial-product full backup and recovery milestone

The backup/restore tooling above implements the repeatable backup-set format,
integrity verification, explicit PostgreSQL replacement, and safe external-file
restore targets. It is not a completed recovery milestone until an operator has
configured it for the real household topology and proved a disposable restore.
Before Family Librarian's initial product is considered complete, the operator
must be able to create and restore a full backup covering:

- PostgreSQL, including the persisted Data Protection key ring and stored
  configuration ciphertext;
- CWA configuration and its complete Calibre library;
- Audiobookshelf configuration and its complete library; and
- the deployment-specific secret and certificate recovery procedure, without
  placing raw secrets in the backup manifest or repository.

The shipped procedure or tooling must identify the component versions and
backup time, preserve integrity information, and be proven by restoring all
components together in a disposable environment. The recovery proof includes
the migration job and health checks, one CWA ownership/OPDS lookup, and one
Audiobookshelf library lookup. A settings-only archive may speed up development
or lab setup, but it is not a substitute for this full recovery capability.

## Reverse proxy and secrets

Place a TLS-terminating reverse proxy in front of port 8080 for an internet
reachable deployment. The application uses cookie authentication, so ensure the
proxy forwards the canonical scheme and host and does not cache API responses.
Use the public HTTPS URL consistently in browser bookmarks and, when optional
OIDC is enabled, in the registered callback and sign-out URIs.

Set `ReverseProxy__TrustedNetworks` to the proxy's own address or network in
CIDR notation. The forwarded-headers middleware only honors `X-Forwarded-For`/
`X-Forwarded-Proto` from an address in that list — unconfigured, only loopback
is trusted, so a proxy running elsewhere (a separate host, a separate
container) is silently ignored rather than trusted by default. Do not point
this at a network wider than the actual proxy: any address inside it can then
spoof its own client IP into request logs and the invitation rate limiter's
per-caller partitioning.

Keep PostgreSQL credentials, bootstrap credentials, OIDC secrets, and provider
credentials outside the repository. Provider credentials entered through the
administrator UI are encrypted using the persisted Data Protection key ring in
PostgreSQL; that is why a database backup is required for both credential and
session continuity after recovery.

Set `DataProtection__KeyEncryptionCertificate__Path` (and `__Password` if the
PFX is protected) to encrypt that key ring at rest. Without it, the key ring
itself is stored in PostgreSQL unencrypted — meaning the credentials it
protects and the key that unlocks them live in the same backup, which makes
the encryption decorative against anyone who obtains one. The application logs
a warning at startup when this is unset rather than refusing to start, since a
first deployment has no certificate to provide yet; treat that warning as a
todo, not routine output, once real provider credentials are in use.

## Settings-only archive

An administrator can create an encrypted settings-only archive from **Settings
backup** in the application. It contains current integration, provider, OIDC,
private-egress, and acquisition-policy configuration, including the existing
Data Protection ciphertext for credentials. It does not contain accounts,
catalogue data, requests, audit history, notifications, jobs, files, or the
Data Protection key ring. It is not a replacement for the PostgreSQL backup
and restore procedure above.

Import is intentionally create-only: use it only to seed a fresh instance with
none of those settings configured. Before import, the target validates that all
credential ciphertext can be opened by its Data Protection key ring. An archive
with secrets can therefore be imported only when the target has the matching
key ring; copying only the key-encryption certificate is not sufficient. Keep
the archive and passphrase separate, restrict access to both, and use the full
PostgreSQL backup for disaster recovery or a complete migration.
