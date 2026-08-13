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
