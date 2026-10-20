#!/usr/bin/env bash
# Build a .unitypackage from the current tree and validate it.
# Pass --output to keep the archive; otherwise the check uses a temp file.
# See scripts/make_unitypackage.py.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"

pick_python() {
  local candidate
  for candidate in python3 python "py -3"; do
    # shellcheck disable=SC2086
    if $candidate -c "import sys" >/dev/null 2>&1; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done
  return 1
}

if ! chosen="$(pick_python)"; then
  echo "python3 is required to build the .unitypackage" >&2
  exit 1
fi

# shellcheck disable=SC2086
exec $chosen "$root/scripts/make_unitypackage.py" --check "$@"
