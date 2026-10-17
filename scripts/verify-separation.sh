#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
qwenpaw_dir="${QWENPAW_REPO_DIR:-$repo_root/../qwenpaw}"
fail() { printf 'separation contract: FAIL: %s\n' "$*" >&2; exit 1; }
pass() { printf 'separation contract: %s\n' "$*"; }

cd "$repo_root"
test -f agent360-contract.json || fail "agent360-contract.json is missing"
test -f contracts/bot-definition.v1.json || fail "Bot contract is missing"
test -f adapters/qwenpaw/qwenpaw.lock.json || fail "QwenPaw dependency lock is missing"
python3 -m json.tool agent360-contract.json >/dev/null || fail "invalid Agent360 contract JSON"
for f in contracts/*.json; do python3 -m json.tool "$f" >/dev/null || fail "invalid JSON: $f"; done

# The overlay repo must not vendor the QwenPaw application tree or runtime state.
for forbidden in src/qwenpaw console plugins website /app/working /app/working.secret /app/working.backups; do
  test ! -e "$forbidden" || fail "forbidden vendored/runtime path exists: $forbidden"
done
if git ls-files | grep -Eq '(^|/)(\.env|.*secret.*|working(\.secret|\.backups)?/)' ; then
  fail "secret/runtime-state path is tracked"
fi

# Validate the separate runtime source when a checkout is available.
test -d "$qwenpaw_dir/.git" || fail "clean QwenPaw checkout not found at $qwenpaw_dir"
test -d "$qwenpaw_dir/src/qwenpaw" || fail "QwenPaw source tree missing from separate checkout"
test -z "$(git -C "$qwenpaw_dir" status --untracked-files=no --porcelain)" || fail "QwenPaw checkout is dirty"

# Accept either upstream direct or Viebal fork with verified upstream remote
upstream_remote="$(git -C "$qwenpaw_dir" remote get-url upstream 2>/dev/null || git -C "$qwenpaw_dir" remote get-url origin 2>/dev/null || true)"
case "$upstream_remote" in
  https://github.com/agentscope-ai/QwenPaw.git|git@github.com:agentscope-ai/QwenPaw.git) ;;
  *) fail "QwenPaw upstream remote is not the public upstream: ${upstream_remote:-missing}" ;;
esac

# Check locked commit against raw branch or current rev
locked_sha="$(python3 -c 'import json; print(json.load(open("adapters/qwenpaw/qwenpaw.lock.json"))["commit"])')"
raw_sha="$(git -C "$qwenpaw_dir" rev-parse raw 2>/dev/null || git -C "$qwenpaw_dir" rev-parse HEAD)"
test "$locked_sha" = "$raw_sha" || fail "QwenPaw raw baseline does not match the lock: $raw_sha vs $locked_sha"

qwenpaw_version="$(sed -n 's/^__version__ = "\([^"]*\)"/\1/p' "$qwenpaw_dir/src/qwenpaw/__version__.py")"
test -n "$qwenpaw_version" || fail "QwenPaw version is missing"
pass "Agent360 owns contracts only"
pass "QwenPaw base is cleanly separated (upstream: ${upstream_remote}, raw: ${raw_sha}, version: ${qwenpaw_version})"
