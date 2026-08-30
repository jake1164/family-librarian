#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "$0")/../.." && pwd)
# shellcheck source=lib/backup-test-helpers.sh
source "$repository_root/tests/scripts/lib/backup-test-helpers.sh"

root_directory=$(mktemp -d)
trap 'rm -rf "$root_directory"' EXIT

# Full round trip with both CWA and Audiobookshelf enabled: create, verify,
# restore, and confirm every directory component's bytes come back intact.
test_full_round_trip_with_both_libraries_enabled() {
  local work=$root_directory/full-round-trip
  mkdir -p "$work/bin" "$work/cwa-config" "$work/cwa-library" "$work/abs-config" "$work/abs-library"
  make_fake_docker "$work/bin"

  printf 'cwa-config' > "$work/cwa-config/value"
  printf 'cwa-library' > "$work/cwa-library/value"
  printf 'abs-config' > "$work/abs-config/value"
  printf 'abs-library' > "$work/abs-library/value"

  printf '%s\n' \
    "BACKUP_OUTPUT_DIRECTORY=$work/backups" \
    'CWA_BACKUP_MODE=directories' \
    "CWA_CONFIGURATION_DIRECTORY=$work/cwa-config" \
    "CWA_LIBRARY_DIRECTORY=$work/cwa-library" \
    'AUDIOBOOKSHELF_BACKUP_MODE=directories' \
    "AUDIOBOOKSHELF_CONFIGURATION_DIRECTORY=$work/abs-config" \
    "AUDIOBOOKSHELF_LIBRARY_DIRECTORY=$work/abs-library" \
    'DEPLOYMENT_SECRET_RECOVERY_REFERENCE=test-runbook' \
    > "$work/backup.env"

  PATH="$work/bin:$PATH" "$repository_root/scripts/backups/create-backup.sh" \
    --config "$work/backup.env" --project-directory "$repository_root" > "$work/created-path"
  local backup_directory
  backup_directory=$(<"$work/created-path")
  "$repository_root/scripts/backups/verify-backup.sh" --backup "$backup_directory"

  PATH="$work/bin:$PATH" "$repository_root/scripts/backups/restore-backup.sh" \
    --backup "$backup_directory" --confirm-replace-postgres \
    --cwa-configuration-target "$work/restore/cwa-config" \
    --cwa-library-target "$work/restore/cwa-library" \
    --audiobookshelf-configuration-target "$work/restore/abs-config" \
    --audiobookshelf-library-target "$work/restore/abs-library"

  [[ $(<"$work/restore/cwa-config/value") == cwa-config ]]
  [[ $(<"$work/restore/cwa-library/value") == cwa-library ]]
  [[ $(<"$work/restore/abs-config/value") == abs-config ]]
  [[ $(<"$work/restore/abs-library/value") == abs-library ]]
}

# A library set to "disabled" must be skipped end to end: no archive is
# produced for it, the manifest records "disabled", restore requires no
# target for it, and the still-enabled library restores normally alongside
# it. Deliberately asymmetric (CWA disabled, Audiobookshelf enabled) so a
# bug that ignores CWA_BACKUP_MODE and always backs up CWA cannot pass by
# coincidence the way a "both disabled" or "both enabled" case could.
test_disabled_library_is_skipped_and_recorded() {
  local work=$root_directory/disabled-library
  mkdir -p "$work/bin" "$work/abs-config" "$work/abs-library"
  make_fake_docker "$work/bin"

  printf 'abs-config' > "$work/abs-config/value"
  printf 'abs-library' > "$work/abs-library/value"

  printf '%s\n' \
    "BACKUP_OUTPUT_DIRECTORY=$work/backups" \
    'CWA_BACKUP_MODE=disabled' \
    'AUDIOBOOKSHELF_BACKUP_MODE=directories' \
    "AUDIOBOOKSHELF_CONFIGURATION_DIRECTORY=$work/abs-config" \
    "AUDIOBOOKSHELF_LIBRARY_DIRECTORY=$work/abs-library" \
    'DEPLOYMENT_SECRET_RECOVERY_REFERENCE=test-runbook' \
    > "$work/backup.env"

  PATH="$work/bin:$PATH" "$repository_root/scripts/backups/create-backup.sh" \
    --config "$work/backup.env" --project-directory "$repository_root" > "$work/created-path"
  local backup_directory
  backup_directory=$(<"$work/created-path")
  "$repository_root/scripts/backups/verify-backup.sh" --backup "$backup_directory"

  [[ ! -e "$backup_directory/components/cwa-configuration.tar.gz" ]]
  [[ ! -e "$backup_directory/components/cwa-library.tar.gz" ]]
  grep -q '"cwa": { "mode": "disabled" }' "$backup_directory/manifest.json"
  grep -q '"audiobookshelf": { "mode": "directories" }' "$backup_directory/manifest.json"

  PATH="$work/bin:$PATH" "$repository_root/scripts/backups/restore-backup.sh" \
    --backup "$backup_directory" --confirm-replace-postgres \
    --audiobookshelf-configuration-target "$work/restore/abs-config" \
    --audiobookshelf-library-target "$work/restore/abs-library"

  [[ $(<"$work/restore/abs-config/value") == abs-config ]]
  [[ $(<"$work/restore/abs-library/value") == abs-library ]]
  [[ ! -e "$work/restore/cwa-config" ]]
}

# The manifest is the contract restore-backup.sh (and any future tooling)
# parses, so its content is worth asserting directly rather than only
# exercising it indirectly through a successful restore.
test_manifest_contains_expected_fields() {
  local work=$root_directory/manifest-fields
  mkdir -p "$work/bin"
  make_fake_docker "$work/bin"

  printf '%s\n' \
    "BACKUP_OUTPUT_DIRECTORY=$work/backups" \
    'CWA_BACKUP_MODE=disabled' \
    'AUDIOBOOKSHELF_BACKUP_MODE=disabled' \
    'DEPLOYMENT_SECRET_RECOVERY_REFERENCE=vault:family-librarian/prod-cert' \
    > "$work/backup.env"

  PATH="$work/bin:$PATH" "$repository_root/scripts/backups/create-backup.sh" \
    --config "$work/backup.env" --project-directory "$repository_root" > "$work/created-path"
  local backup_directory
  backup_directory=$(<"$work/created-path")
  local backup_id
  backup_id=$(basename "$backup_directory")

  grep -q "\"format\": \"family-librarian-full-backup\"" "$backup_directory/manifest.json"
  grep -q "\"formatVersion\": 1" "$backup_directory/manifest.json"
  grep -q "\"backupId\": \"${backup_id}\"" "$backup_directory/manifest.json"
  grep -q '"deploymentSecretRecoveryReference": "vault:family-librarian/prod-cert"' "$backup_directory/manifest.json"
  [[ $(<"$backup_directory/postgres.dump") == fake-postgres-dump ]]
}

# create-backup.sh runs `umask 077` specifically so a backup set — which
# contains account emails, request history, and the Data Protection key
# ring per docs/06-deployment-and-recovery.md — is not group/world readable.
# That guarantee is worth checking directly rather than trusting the umask
# call is still in effect after every future edit to the script.
test_created_files_are_not_group_or_world_accessible() {
  local work=$root_directory/permissions
  mkdir -p "$work/bin"
  make_fake_docker "$work/bin"

  printf '%s\n' \
    "BACKUP_OUTPUT_DIRECTORY=$work/backups" \
    'CWA_BACKUP_MODE=disabled' \
    'AUDIOBOOKSHELF_BACKUP_MODE=disabled' \
    'DEPLOYMENT_SECRET_RECOVERY_REFERENCE=test-runbook' \
    > "$work/backup.env"

  PATH="$work/bin:$PATH" "$repository_root/scripts/backups/create-backup.sh" \
    --config "$work/backup.env" --project-directory "$repository_root" > "$work/created-path"
  local backup_directory
  backup_directory=$(<"$work/created-path")

  for entry in "$backup_directory" "$backup_directory/postgres.dump" \
    "$backup_directory/manifest.json" "$backup_directory/checksums.sha256"; do
    local bits
    bits=$(file_permission_bits "$entry")
    [[ "${bits: -2}" == "00" ]] || {
      echo "FAIL: $entry is group/world accessible (mode $bits)" >&2
      exit 1
    }
  done
}

echo "== full round trip (both libraries) =="
test_full_round_trip_with_both_libraries_enabled
echo "== disabled library is skipped =="
test_disabled_library_is_skipped_and_recorded
echo "== manifest fields =="
test_manifest_contains_expected_fields
echo "== created file permissions =="
test_created_files_are_not_group_or_world_accessible
