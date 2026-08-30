#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "$0")/../.." && pwd)
temporary_directory=$(mktemp -d)
trap 'rm -rf "$temporary_directory"' EXIT

mkdir -p "$temporary_directory/bin" "$temporary_directory/cwa-config" "$temporary_directory/cwa-library" \
  "$temporary_directory/abs-config" "$temporary_directory/abs-library"
printf 'cwa-config' > "$temporary_directory/cwa-config/value"
printf 'cwa-library' > "$temporary_directory/cwa-library/value"
printf 'abs-config' > "$temporary_directory/abs-config/value"
printf 'abs-library' > "$temporary_directory/abs-library/value"

printf '%s\n' '#!/usr/bin/env bash' 'set -euo pipefail' \
  'if [[ "$*" == *"pg_dump"* ]]; then printf "fake-postgres-dump"; fi' \
  > "$temporary_directory/bin/docker"
chmod +x "$temporary_directory/bin/docker"

printf '%s\n' \
  "BACKUP_OUTPUT_DIRECTORY=$temporary_directory/backups" \
  'CWA_BACKUP_MODE=directories' \
  "CWA_CONFIGURATION_DIRECTORY=$temporary_directory/cwa-config" \
  "CWA_LIBRARY_DIRECTORY=$temporary_directory/cwa-library" \
  'AUDIOBOOKSHELF_BACKUP_MODE=directories' \
  "AUDIOBOOKSHELF_CONFIGURATION_DIRECTORY=$temporary_directory/abs-config" \
  "AUDIOBOOKSHELF_LIBRARY_DIRECTORY=$temporary_directory/abs-library" \
  'DEPLOYMENT_SECRET_RECOVERY_REFERENCE=test-runbook' \
  > "$temporary_directory/backup.env"

PATH="$temporary_directory/bin:$PATH" "$repository_root/scripts/backups/create-backup.sh" \
  --config "$temporary_directory/backup.env" --project-directory "$repository_root" > "$temporary_directory/created-path"
backup_directory=$(<"$temporary_directory/created-path")
"$repository_root/scripts/backups/verify-backup.sh" --backup "$backup_directory"

PATH="$temporary_directory/bin:$PATH" "$repository_root/scripts/backups/restore-backup.sh" \
  --backup "$backup_directory" --confirm-replace-postgres \
  --cwa-configuration-target "$temporary_directory/restore/cwa-config" \
  --cwa-library-target "$temporary_directory/restore/cwa-library" \
  --audiobookshelf-configuration-target "$temporary_directory/restore/abs-config" \
  --audiobookshelf-library-target "$temporary_directory/restore/abs-library"

[[ $(<"$temporary_directory/restore/cwa-config/value") == cwa-config ]]
[[ $(<"$temporary_directory/restore/cwa-library/value") == cwa-library ]]
[[ $(<"$temporary_directory/restore/abs-config/value") == abs-config ]]
[[ $(<"$temporary_directory/restore/abs-library/value") == abs-library ]]
