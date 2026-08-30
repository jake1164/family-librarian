#!/usr/bin/env bash
# Shared helpers for the backup/restore shell test suite. Sourced, not
# executed, so it deliberately has no shebang side effects beyond `set`.
set -euo pipefail

# Runs "$@" and fails the test (message + exit 1) unless it exits non-zero.
# Centralizes the "this must be rejected" shape so every such assertion reads
# the same way and none of them can accidentally forget the negation.
assert_command_fails() {
  local description=$1
  shift
  if "$@" >/dev/null 2>&1; then
    echo "FAIL: expected to fail but succeeded: $description" >&2
    exit 1
  fi
}

# Creates a fake `docker` on PATH at $1/docker. Any invocation whose
# arguments contain "pg_dump" prints $2 (default: a fixed payload) to
# stdout; every other invocation — compose up/stop/exec pg_restore — exits 0
# having done nothing, since restore-backup.sh's own docker calls are not
# what these fast, stub-backed tests are verifying (the real Postgres
# round trip in backup-restore-postgres.test.sh covers that). Set $3 to make
# the pg_dump invocation itself fail, to exercise create-backup.sh's error
# handling.
make_fake_docker() {
  local bin_directory=$1
  local dump_payload=${2:-fake-postgres-dump}
  local pg_dump_exit_code=${3:-0}
  mkdir -p "$bin_directory"
  cat > "$bin_directory/docker" <<EOF
#!/usr/bin/env bash
set -euo pipefail
if [[ "\$*" == *"pg_dump"* ]]; then
  if [[ "${pg_dump_exit_code}" != 0 ]]; then
    echo "fake pg_dump failure" >&2
    exit ${pg_dump_exit_code}
  fi
  printf '%s' "${dump_payload}"
fi
EOF
  chmod +x "$bin_directory/docker"
}

# Prints a file's permission bits as three octal digits, trying GNU stat
# (Linux, incl. CI) then falling back to BSD stat (macOS) — the same
# fallback shape create-backup.sh itself uses for sha256sum/shasum.
file_permission_bits() {
  stat -c '%a' "$1" 2>/dev/null || stat -f '%Lp' "$1"
}
