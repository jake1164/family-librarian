#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: restore-backup.sh --backup PATH --confirm-replace-postgres \
  [--compose-file PATH] [--project-directory PATH] \
  [--cwa-configuration-target PATH --cwa-library-target PATH] \
  [--audiobookshelf-configuration-target PATH --audiobookshelf-library-target PATH]

The destination directories for library components must be new or empty, and
their respective services must already be stopped. This script intentionally
does not delete existing library data or control external CWA/Audiobookshelf
containers.
EOF
}

backup_directory=""
compose_file="compose.yaml"
project_directory=""
cwa_configuration_target=""
cwa_library_target=""
audiobookshelf_configuration_target=""
audiobookshelf_library_target=""
replace_postgres=false

while (($#)); do
  case "$1" in
    --backup) backup_directory=${2:?}; shift 2 ;;
    --compose-file) compose_file=${2:?}; shift 2 ;;
    --project-directory) project_directory=${2:?}; shift 2 ;;
    --cwa-configuration-target) cwa_configuration_target=${2:?}; shift 2 ;;
    --cwa-library-target) cwa_library_target=${2:?}; shift 2 ;;
    --audiobookshelf-configuration-target) audiobookshelf_configuration_target=${2:?}; shift 2 ;;
    --audiobookshelf-library-target) audiobookshelf_library_target=${2:?}; shift 2 ;;
    --confirm-replace-postgres) replace_postgres=true; shift ;;
    --help|-h) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
  esac
done

[[ "$replace_postgres" == true ]] || { echo "--confirm-replace-postgres is required." >&2; exit 2; }
[[ -n "$backup_directory" && -d "$backup_directory" ]] || { usage >&2; exit 2; }

"$(dirname "$0")/verify-backup.sh" --backup "$backup_directory"

manifest=$(<"$backup_directory/manifest.json")
component_is_backed_up() {
  local component=$1
  [[ "$manifest" =~ \"${component}\"[[:space:]]*:[[:space:]]*\{[[:space:]]*\"mode\"[[:space:]]*:[[:space:]]*\"directories\" ]]
}

restore_component() {
  local component_name=$1
  local target_directory=$2
  [[ -n "$target_directory" ]] || { echo "A target directory is required for ${component_name}." >&2; exit 2; }
  if [[ -e "$target_directory" ]]; then
    [[ -d "$target_directory" ]] || { echo "Target is not a directory: $target_directory" >&2; exit 2; }
    [[ -z "$(find "$target_directory" -mindepth 1 -maxdepth 1 -print -quit)" ]] || {
      echo "Target must be empty; refusing to overwrite: $target_directory" >&2; exit 2;
    }
  else
    mkdir -p "$target_directory"
  fi
  tar -C "$target_directory" -xzf "$backup_directory/components/${component_name}.tar.gz"
}

if component_is_backed_up cwa; then
  restore_component cwa-configuration "$cwa_configuration_target"
  restore_component cwa-library "$cwa_library_target"
fi

if component_is_backed_up audiobookshelf; then
  restore_component audiobookshelf-configuration "$audiobookshelf_configuration_target"
  restore_component audiobookshelf-library "$audiobookshelf_library_target"
fi

compose_args=(compose -f "$compose_file")
if [[ -n "$project_directory" ]]; then
  compose_args+=(--project-directory "$project_directory")
fi

docker "${compose_args[@]}" stop family-librarian
docker "${compose_args[@]}" exec -T postgres \
  pg_restore -U family_librarian -d family_librarian \
  --clean --if-exists --no-owner --no-privileges < "$backup_directory/postgres.dump"
docker "${compose_args[@]}" up -d migrate family-librarian

echo "Restore completed. Verify migration logs, both health endpoints, CWA OPDS, and Audiobookshelf before opening the deployment."
