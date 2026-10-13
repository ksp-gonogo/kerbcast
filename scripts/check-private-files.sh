#!/usr/bin/env bash
# Fails if git tracks agent config files that must stay private.
# `--self-test` plants a positive in a throwaway repo to prove the check bites.
set -euo pipefail

pattern='(^|/)(CLAUDE\.md|AGENTS\.md|GEMINI\.md|\.mcp\.json)$|(^|/)\.claude/'

check() {
  local hits
  hits=$(git -C "$1" ls-files | grep -E "$pattern" || true)
  if [ -n "$hits" ]; then
    echo "private agent files are tracked:" >&2
    echo "$hits" >&2
    return 1
  fi
}

if [ "${1:-}" = "--self-test" ]; then
  tmp=$(mktemp -d)
  trap 'rm -rf "$tmp"' EXIT
  git -C "$tmp" init -q
  touch "$tmp/CLAUDE.md"
  git -C "$tmp" add CLAUDE.md
  if check "$tmp" 2>/dev/null; then
    echo "self-test failed: planted CLAUDE.md was not detected" >&2
    exit 1
  fi
  echo "self-test ok"
  exit 0
fi

check "$(git rev-parse --show-toplevel)"
