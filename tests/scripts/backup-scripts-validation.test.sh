#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "$0")/../.." && pwd)
# shellcheck source=lib/backup-test-helpers.sh
source "$repository_root/tests/scripts/lib/backup-test-helpers.sh"

root_directory=$(mktemp -d)
trap 'rm -rf "$root_directory"' EXIT

create_backup="$repository_root/scripts/backups/create-backup.sh"
restore_backup="$repository_root/scripts/backups/restore-backup.sh"
verify_backup="$repository_root/scripts/backups/verify-backup.sh"

# Produces one valid backup set (stub docker, both libraries disabled to keep
# fixtures minimal) that the restore-side scenarios below can each start
# from without repeating the create-backup.sh setup.
make_valid_backup() {
  local work=$1
  mkdir -p "$work/bin"
  make_fake_docker "$work/bin"
  printf '%s\n' \
    "BACKUP_OUTPUT_DIRECTORY=$work/backups" \
    'CWA_BACKUP_MODE=directories' \
    "CWA_CONFIGURATION_DIRECTORY=$work/cwa-config" \
    "CWA_LIBRARY_DIRECTORY=$work/cwa-library" \
    'AUDIOBOOKSHELF_BACKUP_MODE=disabled' \
    'DEPLOYMENT_SECRET_RECOVERY_REFERENCE=test-runbook' \
    > "$work/backup.env"
  mkdir -p "$work/cwa-config" "$work/cwa-library"
  printf 'cwa-config' > "$work/cwa-config/value"
  printf 'cwa-library' > "$work/cwa-library/value"
  PATH="$work/bin:$PATH" "$create_backup" \
    --config "$work/backup.env" --project-directory "$repository_root" > "$work/created-path"
  cat "$work/created-path"
}

# --- create-backup.sh config validation -------------------------------

test_create_backup_rejects_missing_required_variable() {
  local work=$root_directory/missing-required-variable
  mkdir -p "$work/bin"
  make_fake_docker "$work/bin"
  printf '%s\n' \
    "BACKUP_OUTPUT_DIRECTORY=$work/backups" \
    'CWA_BACKUP_MODE=disabled' \
    'AUDIOBOOKSHELF_BACKUP_MODE=disabled' \
    > "$work/backup.env"
  # DEPLOYMENT_SECRET_RECOVERY_REFERENCE is intentionally left unset.
  assert_command_fails "missing DEPLOYMENT_SECRET_RECOVERY_REFERENCE" \
    env PATH="$work/bin:$PATH" "$create_backup" --config "$work/backup.env" --project-directory "$repository_root"
}

test_create_backup_rejects_invalid_backup_mode() {
  local work=$root_directory/invalid-backup-mode
  mkdir -p "$work/bin"
  make_fake_docker "$work/bin"
  printf '%s\n' \
    "BACKUP_OUTPUT_DIRECTORY=$work/backups" \
    'CWA_BACKUP_MODE=sometimes' \
    'AUDIOBOOKSHELF_BACKUP_MODE=disabled' \
    'DEPLOYMENT_SECRET_RECOVERY_REFERENCE=test-runbook' \
    > "$work/backup.env"
  assert_command_fails "CWA_BACKUP_MODE=sometimes" \
    env PATH="$work/bin:$PATH" "$create_backup" --config "$work/backup.env" --project-directory "$repository_root"
}

test_create_backup_rejects_missing_configured_directory() {
  local work=$root_directory/missing-configured-directory
  mkdir -p "$work/bin"
  make_fake_docker "$work/bin"
  printf '%s\n' \
    "BACKUP_OUTPUT_DIRECTORY=$work/backups" \
    'CWA_BACKUP_MODE=directories' \
    "CWA_CONFIGURATION_DIRECTORY=$work/does-not-exist" \
    "CWA_LIBRARY_DIRECTORY=$work/does-not-exist-either" \
    'AUDIOBOOKSHELF_BACKUP_MODE=disabled' \
    'DEPLOYMENT_SECRET_RECOVERY_REFERENCE=test-runbook' \
    > "$work/backup.env"
  assert_command_fails "CWA_CONFIGURATION_DIRECTORY does not exist on disk" \
    env PATH="$work/bin:$PATH" "$create_backup" --config "$work/backup.env" --project-directory "$repository_root"
  [[ ! -d "$work/backups" ]]
}

test_create_backup_rejects_quoted_recovery_reference() {
  local work=$root_directory/quoted-recovery-reference
  mkdir -p "$work/bin"
  make_fake_docker "$work/bin"
  # A double-quoted or single-quoted RHS would have its quotes stripped by
  # `source` before create-backup.sh's own value check ever runs — the config
  # file has to backslash-escape the quote to get a literal `"` character
  # into the sourced variable, the same way an operator hand-editing the file
  # would need to.
  printf '%s\n' \
    "BACKUP_OUTPUT_DIRECTORY=$work/backups" \
    'CWA_BACKUP_MODE=disabled' \
    'AUDIOBOOKSHELF_BACKUP_MODE=disabled' \
    'DEPLOYMENT_SECRET_RECOVERY_REFERENCE=test\"runbook' \
    > "$work/backup.env"
  assert_command_fails "DEPLOYMENT_SECRET_RECOVERY_REFERENCE containing a literal quote character" \
    env PATH="$work/bin:$PATH" "$create_backup" --config "$work/backup.env" --project-directory "$repository_root"
}

# A failing pg_dump must not leave a partial, half-written backup set behind
# under BACKUP_OUTPUT_DIRECTORY — only the mktemp staging directory, cleaned
# up by the script's own EXIT trap.
test_create_backup_leaves_no_partial_backup_when_pg_dump_fails() {
  local work=$root_directory/failed-pg-dump
  mkdir -p "$work/bin"
  make_fake_docker "$work/bin" "" 1
  printf '%s\n' \
    "BACKUP_OUTPUT_DIRECTORY=$work/backups" \
    'CWA_BACKUP_MODE=disabled' \
    'AUDIOBOOKSHELF_BACKUP_MODE=disabled' \
    'DEPLOYMENT_SECRET_RECOVERY_REFERENCE=test-runbook' \
    > "$work/backup.env"
  assert_command_fails "pg_dump failing inside create-backup.sh" \
    env PATH="$work/bin:$PATH" "$create_backup" --config "$work/backup.env" --project-directory "$repository_root"
  [[ -z "$(find "$work/backups" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" ]]
}

# --- restore-backup.sh safety checks -----------------------------------

test_restore_requires_confirm_flag() {
  local work=$root_directory/requires-confirm-flag
  local backup_directory
  backup_directory=$(make_valid_backup "$work")
  assert_command_fails "restore without --confirm-replace-postgres" \
    env PATH="$work/bin:$PATH" "$restore_backup" --backup "$backup_directory" \
    --cwa-configuration-target "$work/restore/cwa-config" --cwa-library-target "$work/restore/cwa-library"
}

test_restore_requires_target_for_backed_up_component() {
  local work=$root_directory/requires-target
  local backup_directory
  backup_directory=$(make_valid_backup "$work")
  # cwa-library-target is intentionally omitted even though CWA was backed up.
  assert_command_fails "restore missing --cwa-library-target" \
    env PATH="$work/bin:$PATH" "$restore_backup" --backup "$backup_directory" --confirm-replace-postgres \
    --cwa-configuration-target "$work/restore/cwa-config"
  [[ ! -e "$work/restore/cwa-library" ]]
}

# The core safety guarantee the docs advertise: restore must never write
# into a target directory that already has something in it.
test_restore_refuses_non_empty_target_directory() {
  local work=$root_directory/non-empty-target
  local backup_directory
  backup_directory=$(make_valid_backup "$work")
  mkdir -p "$work/restore/cwa-config"
  printf 'pre-existing-operator-data' > "$work/restore/cwa-config/keep-me"

  assert_command_fails "restore into a non-empty target directory" \
    env PATH="$work/bin:$PATH" "$restore_backup" --backup "$backup_directory" --confirm-replace-postgres \
    --cwa-configuration-target "$work/restore/cwa-config" --cwa-library-target "$work/restore/cwa-library"

  [[ $(<"$work/restore/cwa-config/keep-me") == pre-existing-operator-data ]]
  [[ ! -e "$work/restore/cwa-library" ]]
}

test_restore_refuses_target_that_is_a_file() {
  local work=$root_directory/target-is-a-file
  local backup_directory
  backup_directory=$(make_valid_backup "$work")
  printf 'not a directory' > "$work/not-a-directory"

  assert_command_fails "restore target that already exists as a plain file" \
    env PATH="$work/bin:$PATH" "$restore_backup" --backup "$backup_directory" --confirm-replace-postgres \
    --cwa-configuration-target "$work/not-a-directory" --cwa-library-target "$work/restore/cwa-library"
}

# --- verify-backup.sh integrity checks, chained through restore --------

test_verify_backup_reports_each_missing_file() {
  local work=$root_directory/missing-files
  local backup_directory
  backup_directory=$(make_valid_backup "$work")

  for required_file in manifest.json checksums.sha256 postgres.dump; do
    local scratch=$work/missing-$required_file
    cp -R "$backup_directory" "$scratch"
    rm "$scratch/$required_file"
    assert_command_fails "verify-backup.sh with $required_file missing" \
      "$verify_backup" --backup "$scratch"
  done
}

test_verify_backup_detects_checksum_mismatch() {
  local work=$root_directory/checksum-mismatch
  local backup_directory
  backup_directory=$(make_valid_backup "$work")
  printf 'tampered' >> "$backup_directory/postgres.dump"
  assert_command_fails "verify-backup.sh over a tampered postgres.dump" \
    "$verify_backup" --backup "$backup_directory"
}

# restore-backup.sh runs verify-backup.sh before touching anything else; a
# corrupted backup must abort the restore rather than partially apply it.
test_restore_aborts_when_verification_fails() {
  local work=$root_directory/restore-aborts-on-corruption
  local backup_directory
  backup_directory=$(make_valid_backup "$work")
  printf 'tampered' >> "$backup_directory/postgres.dump"

  assert_command_fails "restore over a backup that fails verification" \
    env PATH="$work/bin:$PATH" "$restore_backup" --backup "$backup_directory" --confirm-replace-postgres \
    --cwa-configuration-target "$work/restore/cwa-config" --cwa-library-target "$work/restore/cwa-library"

  [[ ! -e "$work/restore" ]]
}

echo "== create-backup.sh rejects a missing required variable =="
test_create_backup_rejects_missing_required_variable
echo "== create-backup.sh rejects an invalid backup mode =="
test_create_backup_rejects_invalid_backup_mode
echo "== create-backup.sh rejects a missing configured directory =="
test_create_backup_rejects_missing_configured_directory
echo "== create-backup.sh rejects a quoted recovery reference =="
test_create_backup_rejects_quoted_recovery_reference
echo "== create-backup.sh leaves no partial backup when pg_dump fails =="
test_create_backup_leaves_no_partial_backup_when_pg_dump_fails
echo "== restore-backup.sh requires --confirm-replace-postgres =="
test_restore_requires_confirm_flag
echo "== restore-backup.sh requires a target for each backed-up component =="
test_restore_requires_target_for_backed_up_component
echo "== restore-backup.sh refuses a non-empty target directory =="
test_restore_refuses_non_empty_target_directory
echo "== restore-backup.sh refuses a target that is a file =="
test_restore_refuses_target_that_is_a_file
echo "== verify-backup.sh reports each missing file =="
test_verify_backup_reports_each_missing_file
echo "== verify-backup.sh detects a checksum mismatch =="
test_verify_backup_detects_checksum_mismatch
echo "== restore-backup.sh aborts when verification fails =="
test_restore_aborts_when_verification_fails
