#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: print-cron-entry.sh --frequency daily|weekly --repository PATH --config PATH --log PATH"
}

frequency=""
repository=""
config_path=""
log_path=""
while (($#)); do
  case "$1" in
    --frequency) frequency=${2:?}; shift 2 ;;
    --repository) repository=${2:?}; shift 2 ;;
    --config) config_path=${2:?}; shift 2 ;;
    --log) log_path=${2:?}; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
  esac
done

[[ -n "$frequency" && -n "$repository" && -n "$config_path" && -n "$log_path" ]] || { usage >&2; exit 2; }
case "$frequency" in
  daily) schedule='15 2 * * *' ;;
  weekly) schedule='15 2 * * 0' ;;
  *) echo "--frequency must be daily or weekly." >&2; exit 2 ;;
esac

printf '%s cd %q && %q --config %q >> %q 2>&1\n' \
  "$schedule" "$repository" "$repository/scripts/backups/create-backup.sh" "$config_path" "$log_path"
