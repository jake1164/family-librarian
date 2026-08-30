#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "$0")/../.." && pwd)
# shellcheck source=lib/backup-test-helpers.sh
source "$repository_root/tests/scripts/lib/backup-test-helpers.sh"

print_cron_entry="$repository_root/scripts/backups/print-cron-entry.sh"

test_daily_schedule() {
  local output
  output=$("$print_cron_entry" --frequency daily --repository /srv/family-librarian \
    --config /etc/family-librarian/backup.env --log /var/log/family-librarian-backup.log)
  [[ "$output" == "15 2 * * * cd /srv/family-librarian && /srv/family-librarian/scripts/backups/create-backup.sh --config /etc/family-librarian/backup.env >> /var/log/family-librarian-backup.log 2>&1" ]]
}

test_weekly_schedule() {
  local output
  output=$("$print_cron_entry" --frequency weekly --repository /srv/family-librarian \
    --config /etc/family-librarian/backup.env --log /var/log/family-librarian-backup.log)
  [[ "$output" == "15 2 * * 0 cd /srv/family-librarian && /srv/family-librarian/scripts/backups/create-backup.sh --config /etc/family-librarian/backup.env >> /var/log/family-librarian-backup.log 2>&1" ]]
}

# %q-quotes each path, so a path containing a space must come through intact
# and safely quoted rather than splitting the printed cron line in two.
test_quotes_paths_containing_spaces() {
  local output
  output=$("$print_cron_entry" --frequency daily --repository "/srv/family librarian" \
    --config "/etc/family librarian/backup.env" --log "/var/log/family librarian-backup.log")
  [[ "$output" == *"cd /srv/family\\ librarian"* ]]
  [[ "$output" == *"--config /etc/family\\ librarian/backup.env"* ]]
}

test_rejects_invalid_frequency() {
  assert_command_fails "an invalid --frequency value" \
    "$print_cron_entry" --frequency monthly --repository /srv/family-librarian \
    --config /etc/family-librarian/backup.env --log /var/log/family-librarian-backup.log
}

test_rejects_missing_required_arguments() {
  assert_command_fails "print-cron-entry.sh with no arguments" "$print_cron_entry"
  assert_command_fails "print-cron-entry.sh missing --config" \
    "$print_cron_entry" --frequency daily --repository /srv/family-librarian --log /var/log/family-librarian-backup.log
}

echo "== daily schedule =="
test_daily_schedule
echo "== weekly schedule =="
test_weekly_schedule
echo "== quotes paths containing spaces =="
test_quotes_paths_containing_spaces
echo "== rejects invalid frequency =="
test_rejects_invalid_frequency
echo "== rejects missing required arguments =="
test_rejects_missing_required_arguments
