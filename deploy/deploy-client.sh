#!/bin/bash
# Builds the browser game client and puts it live at https://subspacefoundation.com/play/.
# Run from your Mac. Deploys only the pushed tip of the client's main branch.
#
#   deploy/deploy-client.sh --by '<session or branch label>' [--client <checkout>] [--force-rollback]
#
#   --by              who deploys; published in /play/DEPLOYED.json (required)
#   --client DIR      a client checkout on main or detached at dare/main
#                     (default: $CLIENT_DIR or ~/oss-src/deploy)
#   --force-rollback  deploy even if the live client has commits this one lacks (lists them first)
#
#   CLIENT_BRANCH  integration branch (default main); CLIENT_REMOTE  its remote (default dare)
#   DEPLOY_HOST    ssh target (default root@5.161.238.245)
#
# Refuses: a checkout that is not the pushed tip of main, a dirty tree, a build that does not contain
# the live commit (live record), or a deploy while another one runs (server lock).
# Builds with web/build-client.sh, backs up /var/www/foundation/play to /root/play-backup-<utc ts>,
# copies the build over it, checks every file by checksum (restores the backup if one differs) and
# writes the live record. Neither the zone nor the site is touched.
# Rollback by hand: cp -a /root/play-backup-<ts>/. /var/www/foundation/play/  (and fix DEPLOYED.json).
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
# shellcheck source=deploy/live-guard.sh
. "$HERE/live-guard.sh"

CLIENT=${CLIENT_DIR:-$HOME/oss-src/deploy}
GUARD_BRANCHES=${CLIENT_BRANCH:-main}
REMOTE=${CLIENT_REMOTE:-dare}
BUILD_CMD=${CLIENT_BUILD_CMD:-}   # test seam: replaces "bash web/build-client.sh <out>"
usage() { sed -n '5,9p' "$0" >&2; exit 2; }
while [ $# -gt 0 ]; do
  case "$1" in
    --by) [ $# -ge 2 ] || usage; GUARD_BY=$2; shift 2 ;;
    --client) [ $# -ge 2 ] || usage; CLIENT=$2; shift 2 ;;
    --force-rollback) GUARD_FORCE_ROLLBACK=1; shift ;;
    *) echo "unknown option $1" >&2; usage ;;
  esac
done
guard_need_by
[ -f "$CLIENT/web/build-client.sh" ] || guard_die "$CLIENT is not a client checkout (no web/build-client.sh)"
CLIENT=$(cd "$CLIENT" && pwd)
RECORD=$GUARD_PLAY_DIR/DEPLOYED.json

echo "== checks ($CLIENT)"
guard_check_tree "$CLIENT" "$REMOTE" crates web Cargo.toml Cargo.lock
guard_check_ancestry "$RECORD"   # early, so a refused deploy does not wait for a build

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
echo "== build $GUARD_COMMIT"
if [ -n "$BUILD_CMD" ]; then (cd "$CLIENT" && bash -c "$BUILD_CMD" _ "$STAGE/play"); else
  (cd "$CLIENT" && PATH=$HOME/.cargo/bin:$PATH bash web/build-client.sh "$STAGE/play"); fi
for f in index.html start.mjs transport.mjs build.json pkg/oss_client.js pkg/oss_client_bg.wasm; do
  [ -s "$STAGE/play/$f" ] || guard_die "the build has no $f"
done
[ "$(git -C "$CLIENT" rev-parse HEAD)" = "$GUARD_COMMIT" ] && [ -z "$(git -C "$CLIENT" status --porcelain --untracked-files=no)" ] ||
  guard_die "$CLIENT changed during the build"
(cd "$STAGE/play" && find . -type f | sort | xargs shasum -a 256) > "$STAGE/SUMS"

echo "== lock and re-check"
guard_lock "client $GUARD_PLAY_DIR"
guard_check_ancestry "$RECORD"

TS=$(date -u +%Y%m%d-%H%M%S)
echo "== copy to $GUARD_HOST:$GUARD_PLAY_DIR (backup $GUARD_BACKUP_ROOT/play-backup-$TS)"
# shellcheck disable=SC2016  # runs on the server
COPYFILE_DISABLE=1 tar --no-xattrs -C "$STAGE/play" -cz . | guard_remote bash -c '
  set -e
  play=$1 backup=$2/play-backup-$3
  [ -f "$play/index.html" ] || { echo "no $play/index.html on the server" >&2; exit 1; }
  n=2; while [ -e "$backup" ]; do backup=$2/play-backup-$3-$n; n=$((n + 1)); done
  cp -a "$play" "$backup"
  tar -C "$play" -xz --no-same-owner
  echo "$backup" > "$2/play-backup-latest"
  echo "copied; previous client in $backup"' _ "$GUARD_PLAY_DIR" "$GUARD_BACKUP_ROOT" "$TS"
BACKUP=$(guard_remote cat "$GUARD_BACKUP_ROOT/play-backup-latest")

echo "== verify"
# shellcheck disable=SC2016  # runs on the server
if ! guard_remote bash -c 'cd "$1" && sha256sum --quiet -c -' _ "$GUARD_PLAY_DIR" < "$STAGE/SUMS"; then
  # shellcheck disable=SC2016
  guard_remote bash -c 'cp -a "$2/." "$1/"' _ "$GUARD_PLAY_DIR" "$BACKUP" || true
  guard_die "the copied client does not match the build; restored $BACKUP"
fi
echo "all $(wc -l < "$STAGE/SUMS" | tr -d ' ') files match the build"
GUARD_SHIPPED_DIR=$STAGE/play
guard_write_record "$RECORD" client
guard_unlock

if [ "${DEPLOY_PUBLIC_CHECK:-1}" = 1 ]; then
  echo "== public check"
  printf '%-50s %s\n' https://subspacefoundation.com/play/build.json \
    "$(curl -s https://subspacefoundation.com/play/build.json)"
fi
