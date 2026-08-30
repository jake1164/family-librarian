#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: create-backup.sh --config PATH [--compose-file PATH] [--project-directory PATH]

Creates one complete Family Librarian backup set. The config is a shell-style
environment file based on backup.env.example. It describes only host-visible
paths and a recovery reference; it must never contain raw credentials.
EOF
}

config_path=""
compose_file="compose.yaml"
project_directory=""

while (($#)); do
  case "$1" in
    --config) config_path=${2:?}; shift 2 ;;
    --compose-file) compose_file=${2:?}; shift 2 ;;
    --project-directory) project_directory=${2:?}; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
  esac
done

[[ -n "$config_path" ]] || { usage >&2; exit 2; }
[[ -f "$config_path" ]] || { echo "Backup configuration does not exist: $config_path" >&2; exit 2; }

# shellcheck disable=SC1090
source "$config_path"

: "${BACKUP_OUTPUT_DIRECTORY:?BACKUP_OUTPUT_DIRECTORY is required}"
: "${CWA_BACKUP_MODE:?CWA_BACKUP_MODE is required}"
: "${AUDIOBOOKSHELF_BACKUP_MODE:?AUDIOBOOKSHELF_BACKUP_MODE is required}"
: "${DEPLOYMENT_SECRET_RECOVERY_REFERENCE:?DEPLOYMENT_SECRET_RECOVERY_REFERENCE is required}"

case "$CWA_BACKUP_MODE" in disabled|directories) ;; *) echo "CWA_BACKUP_MODE must be disabled or directories." >&2; exit 2 ;; esac
case "$AUDIOBOOKSHELF_BACKUP_MODE" in disabled|directories) ;; *) echo "AUDIOBOOKSHELF_BACKUP_MODE must be disabled or directories." >&2; exit 2 ;; esac
[[ "$DEPLOYMENT_SECRET_RECOVERY_REFERENCE" != *'"'* && "$DEPLOYMENT_SECRET_RECOVERY_REFERENCE" != *$'\n'* ]] || {
  echo "DEPLOYMENT_SECRET_RECOVERY_REFERENCE cannot contain quotes or newlines." >&2; exit 2;
}

required_directories=()

if [[ "$CWA_BACKUP_MODE" == directories ]]; then
  : "${CWA_CONFIGURATION_DIRECTORY:?CWA_CONFIGURATION_DIRECTORY is required when CWA backup is enabled}"
  : "${CWA_LIBRARY_DIRECTORY:?CWA_LIBRARY_DIRECTORY is required when CWA backup is enabled}"
  required_directories+=("$CWA_CONFIGURATION_DIRECTORY" "$CWA_LIBRARY_DIRECTORY")
fi

if [[ "$AUDIOBOOKSHELF_BACKUP_MODE" == directories ]]; then
  : "${AUDIOBOOKSHELF_CONFIGURATION_DIRECTORY:?AUDIOBOOKSHELF_CONFIGURATION_DIRECTORY is required when Audiobookshelf backup is enabled}"
  : "${AUDIOBOOKSHELF_LIBRARY_DIRECTORY:?AUDIOBOOKSHELF_LIBRARY_DIRECTORY is required when Audiobookshelf backup is enabled}"
  required_directories+=("$AUDIOBOOKSHELF_CONFIGURATION_DIRECTORY" "$AUDIOBOOKSHELF_LIBRARY_DIRECTORY")
fi

for required_directory in "${required_directories[@]}"; do
  [[ -d "$required_directory" ]] || { echo "Configured backup directory does not exist: $required_directory" >&2; exit 2; }
done

compose_args=(compose -f "$compose_file")
if [[ -n "$project_directory" ]]; then
  compose_args+=(--project-directory "$project_directory")
fi

timestamp=$(date -u +%Y%m%dT%H%M%SZ)
backup_id="family-librarian-${timestamp}"
umask 077
mkdir -p "$BACKUP_OUTPUT_DIRECTORY"
staging_directory=$(mktemp -d "${BACKUP_OUTPUT_DIRECTORY}/.${backup_id}.XXXXXX")
backup_directory="${BACKUP_OUTPUT_DIRECTORY}/${backup_id}"
trap 'rm -rf "$staging_directory"' EXIT
mkdir -p "$staging_directory/components"

archive_directory() {
  local component_name=$1
  local source_directory=$2
  tar -C "$source_directory" -czf "$staging_directory/components/${component_name}.tar.gz" .
}

docker "${compose_args[@]}" exec -T postgres \
  pg_dump -U family_librarian -d family_librarian -Fc > "$staging_directory/postgres.dump"

if [[ "$CWA_BACKUP_MODE" == directories ]]; then
  archive_directory cwa-configuration "$CWA_CONFIGURATION_DIRECTORY"
  archive_directory cwa-library "$CWA_LIBRARY_DIRECTORY"
fi

if [[ "$AUDIOBOOKSHELF_BACKUP_MODE" == directories ]]; then
  archive_directory audiobookshelf-configuration "$AUDIOBOOKSHELF_CONFIGURATION_DIRECTORY"
  archive_directory audiobookshelf-library "$AUDIOBOOKSHELF_LIBRARY_DIRECTORY"
fi

checksum() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1"
  else
    shasum -a 256 "$1"
  fi
}

(
  cd "$staging_directory"
  find postgres.dump components -type f -print | LC_ALL=C sort | while IFS= read -r file; do
    checksum "$file"
  done
) > "$staging_directory/checksums.sha256"

created_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)
cat > "$staging_directory/manifest.json" <<EOF
{
  "format": "family-librarian-full-backup",
  "formatVersion": 1,
  "backupId": "${backup_id}",
  "createdAtUtc": "${created_at}",
  "postgres": { "file": "postgres.dump", "format": "pg_dump-custom" },
  "cwa": { "mode": "${CWA_BACKUP_MODE}" },
  "audiobookshelf": { "mode": "${AUDIOBOOKSHELF_BACKUP_MODE}" },
  "deploymentSecretRecoveryReference": "${DEPLOYMENT_SECRET_RECOVERY_REFERENCE}",
  "checksums": "checksums.sha256"
}
EOF

mv "$staging_directory" "$backup_directory"
trap - EXIT
printf '%s\n' "$backup_directory"
