#!/usr/bin/env bash
# Publishes only the static site (site/) of one pushed commit; the plugin and zone are not touched.
#
#   deploy/publish-site-from-mac.sh --by '<session or branch label>' [--force-rollback] <full commit sha>
#
# The commit must be on origin's integration branch (main) and contain the
# live commit (/var/www/foundation/DEPLOYED.json); one deploy at a time (server lock).
# See deploy/README.md, "Deploying safely".
set -euo pipefail
REPO=$(cd "$(dirname "$0")/.." && pwd)
# shellcheck source=deploy/live-guard.sh
. "$REPO/deploy/live-guard.sh"
GUARD_BRANCHES=${SITE_BRANCHES:-main}
SOURCE_URL=${SITE_SOURCE_URL:-https://github.com/pioneermushrooms/dare-zone.git}
revision=""
while [ $# -gt 0 ]; do
  case "$1" in
    --by) [ $# -ge 2 ] || { echo "--by needs a value" >&2; exit 2; }; GUARD_BY=$2; shift 2 ;;
    --force-rollback) GUARD_FORCE_ROLLBACK=1; shift ;;
    -*) echo "unknown option $1" >&2; exit 2 ;;
    *) revision=$1; shift ;;
  esac
done
[[ "$revision" =~ ^[a-f0-9]{40}$ ]] || { echo "usage: publish-site-from-mac.sh --by LABEL [--force-rollback] FULL_COMMIT_SHA" >&2; exit 2; }
guard_need_by
RECORD=$GUARD_SITE_DIR/DEPLOYED.json
scratch="$(mktemp -d)"
trap 'rm -rf -- "$scratch"' EXIT
git init -q "$scratch"
git -C "$scratch" fetch -q --depth=1 "$SOURCE_URL" "$revision"
git -C "$scratch" archive -o "$scratch/site.tar.gz" --format=tar.gz FETCH_HEAD site deploy/install-public-site.sh

guard_use_commit "$REPO" origin "$revision"
guard_lock "site only (publish-site-from-mac.sh)"
guard_check_ancestry "$RECORD"

# shellcheck disable=SC2016  # runs on the server
remote="$(guard_remote bash -c 'mktemp -d "${TMPDIR:-/tmp}/foundation-site.XXXXXXXX"')"
[[ "$remote" =~ /foundation-site\.[a-zA-Z0-9]+$ ]] || { echo "Unexpected staging directory." >&2; exit 1; }
# shellcheck disable=SC2016
guard_remote bash -c 'cat > "$1/site.tar.gz"' _ "$remote" < "$scratch/site.tar.gz"
# shellcheck disable=SC2016
guard_remote bash -c 'cd "$1" && tar -xzf site.tar.gz && bash deploy/install-public-site.sh site' _ "$remote"
guard_remote rm -rf -- "$remote"
guard_write_record "$RECORD" site
guard_unlock
