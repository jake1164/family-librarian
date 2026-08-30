#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: verify-backup.sh --backup PATH"
}

backup_directory=""
while (($#)); do
  case "$1" in
    --backup) backup_directory=${2:?}; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
  esac
done

[[ -n "$backup_directory" && -d "$backup_directory" ]] || { usage >&2; exit 2; }
[[ -f "$backup_directory/manifest.json" ]] || { echo "Manifest is missing." >&2; exit 1; }
[[ -f "$backup_directory/checksums.sha256" ]] || { echo "Checksum list is missing." >&2; exit 1; }
[[ -f "$backup_directory/postgres.dump" ]] || { echo "PostgreSQL dump is missing." >&2; exit 1; }

if command -v sha256sum >/dev/null 2>&1; then
  (cd "$backup_directory" && sha256sum --check checksums.sha256)
else
  (cd "$backup_directory" && shasum -a 256 --check checksums.sha256)
fi
