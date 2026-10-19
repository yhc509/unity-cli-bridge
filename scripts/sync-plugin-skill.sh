#!/usr/bin/env bash
# Regenerate or verify the plugin skill. See scripts/sync-plugin-skill.py.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"

if command -v python3 >/dev/null 2>&1; then
  py=(python3)
elif command -v python >/dev/null 2>&1; then
  py=(python)
elif command -v py >/dev/null 2>&1; then
  py=(py -3)
else
  echo "python3 is required to sync the plugin skill" >&2
  exit 1
fi

exec "${py[@]}" "$root/scripts/sync-plugin-skill.py" "$@"
