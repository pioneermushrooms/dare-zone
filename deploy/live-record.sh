#!/bin/bash
# Shows or seeds the live records on the Foundation server (see deploy/live-guard.sh).
#
#   deploy/live-record.sh [show]
#   deploy/live-record.sh seed site   --commit <sha> --branch <b> --by <label> [--replace]
#   deploy/live-record.sh seed client --commit <sha> --branch <b> --by <label> --compare <build dir> [--replace]
#
# seed writes a record for what is live now, without deploying, after proving it by checksum:
#   site    every file of the commit's site/ must equal the server's /var/www/foundation/<file>
#   client  every file of <build dir> (web/build-client.sh output of that commit) must equal
#           /var/www/foundation/play/<file>
# It refuses to overwrite an existing record unless --replace.
#   --repo <dir>  the repository holding the commit (site: this repo; client: required)
#   --remote <r>  its remote for the record's repo URL (site: origin; client: dare)
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
# shellcheck source=deploy/live-guard.sh
. "$HERE/live-guard.sh"

cmd=${1:-show}; [ $# -eq 0 ] || shift
if [ "$cmd" = show ]; then
  for r in "$GUARD_SITE_DIR/DEPLOYED.json" "$GUARD_PLAY_DIR/DEPLOYED.json"; do
    echo "== $GUARD_HOST:$r"
    guard_read_record "$r"; echo "${GUARD_LIVE_JSON:-(no record)}"
  done
  exit 0
fi
[ "$cmd" = seed ] || { sed -n '3,6p' "$0" >&2; exit 2; }
what=${1:-}; [ $# -eq 0 ] || shift
commit="" branch="" compare="" replace=0 repo="" remote=""
while [ $# -gt 0 ]; do
  case "$1" in
    --commit) commit=$2; shift 2 ;;
    --branch) branch=$2; shift 2 ;;
    --by) GUARD_BY=$2; shift 2 ;;
    --compare) compare=$2; shift 2 ;;
    --repo) repo=$2; shift 2 ;;
    --remote) remote=$2; shift 2 ;;
    --replace) replace=1; shift ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
case "$what" in
  site) repo=${repo:-$(cd "$HERE/.." && pwd)} remote=${remote:-origin} dir=$GUARD_SITE_DIR ;;
  client) remote=${remote:-dare} dir=$GUARD_PLAY_DIR
          [ -n "$repo" ] || guard_die "seed client needs --repo <client checkout>"
          [ -n "$compare" ] || guard_die "seed client needs --compare <web/build-client.sh output of $commit>" ;;
  *) guard_die "seed what? site or client" ;;
esac
guard_need_by
[ -n "$branch" ] || guard_die "--branch is required"
GUARD_REPO_DIR=$repo GUARD_REMOTE=$remote GUARD_BRANCH=$branch
GUARD_COMMIT=$(git -C "$repo" rev-parse --verify -q "$commit^{commit}") || guard_die "$commit is not a commit in $repo"
GUARD_REPO_URL=$(git -C "$repo" remote get-url "$remote")
RECORD=$dir/DEPLOYED.json

TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
if [ "$what" = site ]; then
  mkdir "$TMP/cmp"; git -C "$repo" archive "$GUARD_COMMIT" site | tar -x -C "$TMP/cmp"; compare=$TMP/cmp/site
fi
(cd "$compare" && find . -type f | sort | xargs shasum -a 256) > "$TMP/SUMS"

guard_read_record "$RECORD"
[ -z "$GUARD_LIVE_JSON" ] || [ "$replace" = 1 ] || guard_die "$RECORD exists (use --replace):
$GUARD_LIVE_JSON"
echo "== checksum $(wc -l < "$TMP/SUMS" | tr -d ' ') files of $GUARD_COMMIT against $GUARD_HOST:$dir"
# shellcheck disable=SC2016
guard_remote bash -c 'cd "$1" && sha256sum -c - 2>&1 | grep -v ": OK$"; exit "${PIPESTATUS[0]}"' _ "$dir" < "$TMP/SUMS" ||
  guard_die "what is live in $dir is not $GUARD_COMMIT (the files above differ); record not written"
echo "all files match"
guard_write_record "$RECORD" "$what"
