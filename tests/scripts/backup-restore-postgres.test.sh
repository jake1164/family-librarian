#!/usr/bin/env bash
set -euo pipefail

# Every other test in this suite fakes out `docker` entirely, which proves
# create-backup.sh/restore-backup.sh call the right commands but never proves
# those commands actually round-trip data through a real PostgreSQL — the
# whole point of pg_dump/pg_restore. This test runs both scripts unstubbed
# against a real, disposable `docker compose` project: a real Postgres plus
# minimal stand-ins for the `migrate`/`family-librarian` services so
# restore-backup.sh's `stop`/`up -d migrate family-librarian` calls have
# something real to act on without building the full application image.
#
# Skips (does not fail) when Docker is unavailable, matching this repo's
# existing PostgresFixture convention for host-integration tests
# (tests/FamilyLibrarian.Web.Tests/Harness/PostgresFixture.cs) so a machine
# without Docker does not look like a broken build.
if ! docker info >/dev/null 2>&1; then
  echo "SKIP: Docker is not available; skipping the real PostgreSQL backup/restore round trip." >&2
  exit 0
fi

repository_root=$(cd "$(dirname "$0")/../.." && pwd)
create_backup="$repository_root/scripts/backups/create-backup.sh"
restore_backup="$repository_root/scripts/backups/restore-backup.sh"

work=$(mktemp -d)
compose_file="$work/compose.yaml"

cleanup() {
  docker compose -f "$compose_file" --project-directory "$work" down --volumes --remove-orphans >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

# `migrate` and `family-librarian` are Alpine stand-ins, not the real
# application image: this test is about proving the PostgreSQL round trip,
# and restore-backup.sh's stop/up calls just need services with these exact
# names to act on.
cat > "$compose_file" <<EOF
name: family-librarian-backup-test-$(basename "$work")
services:
  postgres:
    image: postgres:17-alpine
    environment:
      POSTGRES_DB: family_librarian
      POSTGRES_USER: family_librarian
      POSTGRES_PASSWORD: test_password
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U family_librarian -d family_librarian"]
      interval: 2s
      timeout: 5s
      retries: 30
      start_period: 5s
  migrate:
    image: alpine:3
    command: ["true"]
    restart: "no"
    depends_on:
      postgres:
        condition: service_healthy
  family-librarian:
    image: alpine:3
    command: ["sleep", "infinity"]
    depends_on:
      postgres:
        condition: service_healthy
EOF

compose() {
  docker compose -f "$compose_file" --project-directory "$work" "$@"
}

psql_exec() {
  compose exec -T postgres psql -v ON_ERROR_STOP=1 -U family_librarian -d family_librarian "$@"
}

compose up -d --wait postgres
compose up -d family-librarian

psql_exec -c "CREATE TABLE probe (value text); INSERT INTO probe (value) VALUES ('before-restore');"

printf '%s\n' \
  "BACKUP_OUTPUT_DIRECTORY=$work/backups" \
  'CWA_BACKUP_MODE=disabled' \
  'AUDIOBOOKSHELF_BACKUP_MODE=disabled' \
  'DEPLOYMENT_SECRET_RECOVERY_REFERENCE=test-runbook' \
  > "$work/backup.env"

"$create_backup" --config "$work/backup.env" --compose-file "$compose_file" --project-directory "$work" \
  > "$work/created-path"
backup_directory=$(<"$work/created-path")

"$repository_root/scripts/backups/verify-backup.sh" --backup "$backup_directory"

# Prove the restore actually reverts data, not just that pg_restore exits 0.
psql_exec -c "UPDATE probe SET value = 'corrupted-before-restore';"

"$restore_backup" --backup "$backup_directory" --confirm-replace-postgres \
  --compose-file "$compose_file" --project-directory "$work"

restored_value=$(psql_exec -tA -c "SELECT value FROM probe;")
[[ "$restored_value" == "before-restore" ]] || {
  echo "FAIL: expected 'before-restore' after restore, got '$restored_value'" >&2
  exit 1
}

echo "== real PostgreSQL backup/restore round trip: OK =="
