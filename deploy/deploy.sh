#!/bin/bash
# Deploys Dare.Arenas to the Foundation zone (subspacefoundation.com). Safe to re-run.
#
#   deploy/deploy.sh --by '<session or branch label>' [--force-rollback] [--sysop <name>] [--no-restart] [--force]
#
#   --by              who deploys; published in /DEPLOYED.json (required)
#   --force-rollback  deploy even if the live site/plugin has commits this one lacks (lists them first)
#   --force           restart the zone even with players online (default: refuse)
#
# Deploys only a clean checkout whose commit contains the live one (/var/www/foundation/DEPLOYED.json)
# and records the new one there. See deploy/README.md, "Deploying safely".
#
#   OSS_BOT_LINUX  optional Linux x86-64 oss-bot executable to bundle (arena bots). Without it the
#                  server keeps the oss-bot it has (or the newest backed-up one), so bots stay on.
#   DEPLOY_HOST   ssh target (default root@5.161.238.245)
#
# Builds the plugin (Release), copies it with deploy/ and site/ to
# /root/dare-arenas-deploy-<utc timestamp>/ on the server, and runs deploy/remote-install.sh there,
# which backs up every file it changes (/root/backup-arena-ownership-<timestamp>/), installs the
# module, groups, template, site pages and Caddy route, and restarts the zone (see that script).
#
# Foundation accounts: the install swaps the AuthFile login module for FoundationAuth (the plugin
# imports passwd.conf [Users] once on first load), removes cmd_passwd and adds the /account/* Caddy
# route. After the first deploy, run deploy/set-resend-key.sh from your Mac so codes go out by email.
#
# Accounts (shared recovery emails): from this build one email may serve up to 10 pilots, so
# the old one-account-per-email index is dropped at start. An OLDER build recreates that unique
# index at start and fails once two accounts share an email. Before rolling the plugin back past
# this change, make every recovery email unique again (the first pilot on each email keeps it;
# the others lose their recovery email until they add one), with subspace stopped:
#   sqlite3 /opt/subspace/zone/data/dare-arenas.db "UPDATE accounts SET recovery_email=NULL, email_verified=0
#     WHERE recovery_email IS NOT NULL AND id NOT IN (SELECT MIN(id) FROM accounts
#     WHERE recovery_email IS NOT NULL GROUP BY recovery_email COLLATE NOCASE);"
# Requests withdrawn by their pilot have status 'Withdrawn', which builds before ?withdrawrequest
# can't read (their request lists fail). Before rolling back past it, with subspace stopped:
#   sqlite3 /opt/subspace/zone/data/dare-arenas.db "UPDATE requests SET status='Declined', reason='withdrawn' WHERE status='Withdrawn';"
#
# Rollback: remote-install.sh rolls back by itself when the new build does not come up (previous
# plugin folder on a re-deploy; Modules.config and groupdef.dir/default from the backup, so AuthFile is
# the login module again; Caddyfile and site too). By hand, on the server,
# with B=$(cat /root/backup-arena-ownership-latest):
#   cat $B/conf/Modules.config > /opt/subspace/zone/conf/Modules.config
#   cat $B/conf/groupdef.conf > /opt/subspace/zone/conf/groupdef.conf
#   cp -a $B/conf/groupdef.dir/. /opt/subspace/zone/conf/groupdef.dir/
#   cat $B/conf/staff.conf > /opt/subspace/zone/conf/staff.conf
#   passwd.conf: NEVER restore it wholesale (copy back single lines from $B/conf/passwd.conf).
#     Restoring Modules.config + groupdef.dir/default (above) puts AuthFile and cmd_passwd back; passwords
#     set on the site while FoundationAuth ran are not in passwd.conf and do not carry over.
#   data/dare-arenas.db: a consistent copy is in $B/data/ (restore only with subspace stopped).
#   if [ -d $B/zone-bin-modules/Dare.Arenas ]; then          # previous plugin build, if any
#     mv /opt/subspace/zone/bin/modules/Dare.Arenas $B/failed-Dare.Arenas
#     cp -a $B/zone-bin-modules/Dare.Arenas /opt/subspace/zone/bin/modules/
#   fi
#   cat $B/Caddyfile > /etc/caddy/Caddyfile && caddy validate --config /etc/caddy/Caddyfile && systemctl reload caddy
#   cp -a $B/site/. /var/www/foundation/
#   systemctl restart subspace
# Owned arenas, data/dare-arenas.db and _archive/ can stay; they do nothing with the module unloaded.
set -euo pipefail

HOST=${DEPLOY_HOST:-root@5.161.238.245}
REPO=$(cd "$(dirname "$0")/.." && pwd)
[ -d /usr/local/share/dotnet ] && export PATH=/usr/local/share/dotnet:$PATH
# shellcheck source=deploy/live-guard.sh
. "$REPO/deploy/live-guard.sh"
PASS=()
while [ $# -gt 0 ]; do
  case "$1" in
    --by) [ $# -ge 2 ] || { echo "--by needs a value" >&2; exit 2; }; GUARD_BY=$2; shift 2 ;;
    --force-rollback) GUARD_FORCE_ROLLBACK=1; shift ;;
    *) PASS+=("$1"); shift ;;
  esac
done
set -- ${PASS[@]+"${PASS[@]}"}
guard_need_by
RECORD=$GUARD_SITE_DIR/DEPLOYED.json
echo "== checks"
guard_check_tree "$REPO" origin site deploy src
guard_check_ancestry "$RECORD"   # early, so a refused deploy does not wait for a build

echo "== build (Release)"
rm -rf "$REPO/out/modules/Dare.Arenas"
dotnet build "$REPO/src/Dare.Arenas" -c Release -nologo -v quiet
PLUGIN="$REPO/out/modules/Dare.Arenas"
[ -f "$PLUGIN/Dare.Arenas.dll" ] || { echo "build produced no $PLUGIN/Dare.Arenas.dll" >&2; exit 1; }
if compgen -G "$PLUGIN/*[sS][qQ][lL][iI][tT][eE]*" >/dev/null; then echo "plugin output carries sqlite files; refusing" >&2; exit 1; fi

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/plugin" "$STAGE/site"
cp -R "$REPO/deploy" "$STAGE/deploy"
cp "$PLUGIN"/* "$STAGE/plugin/"
if [ -n "${OSS_BOT_LINUX:-}" ]; then
  [ -f "$OSS_BOT_LINUX" ] && file -b "$OSS_BOT_LINUX" | grep -Eq '^ELF 64-bit.*executable.*x86-64' ||
    { echo "OSS_BOT_LINUX must point to a Linux x86-64 executable" >&2; exit 1; }
  install -m 0755 "$OSS_BOT_LINUX" "$STAGE/plugin/oss-bot"
fi
# The server's native SQLite comes from SQLitePCLRaw.lib.e_sqlite3 at the version the plugin builds
# against (2.1.10, which the server shipped with, has a known high-severity flaw); same C API.
SQLITE_VER=$(sed -n 's/.*SQLitePCLRaw.lib.e_sqlite3" Version="\([0-9.]*\)".*/\1/p' "$REPO/src/Dare.Arenas/Dare.Arenas.csproj" | head -1)
NATIVE="${NUGET_PACKAGES:-$HOME/.nuget/packages}/sqlitepclraw.lib.e_sqlite3/$SQLITE_VER/runtimes/linux-x64/native/libe_sqlite3.so"
[ -f "$NATIVE" ] || { echo "no $NATIVE (restore the solution first)" >&2; exit 1; }
mkdir -p "$STAGE/native" && cp "$NATIVE" "$STAGE/native/libe_sqlite3.so"
cp "$REPO/site/"* "$STAGE/site/"

echo "== re-check the live record"
guard_check_ancestry "$RECORD"

TS=$(date -u +%Y%m%d-%H%M%S)
REMOTE=/root/dare-arenas-deploy-$TS
echo "== copy to $HOST:$REMOTE"
# shellcheck disable=SC2029  # $REMOTE is expanded locally on purpose (a fixed timestamped path)
COPYFILE_DISABLE=1 tar --no-xattrs -C "$STAGE" -cz . | ssh "$HOST" "mkdir -p '$REMOTE' && tar -C '$REMOTE' -xz --no-same-owner"

echo "== install"
# quote every argument for the remote shell (it runs as root)
remote_cmd=$(printf '%q ' bash "$REMOTE/deploy/remote-install.sh" "$REMOTE" "$@")
# shellcheck disable=SC2029  # expanded locally on purpose, already quoted with %q
ssh "$HOST" "$remote_cmd"
guard_write_record "$RECORD" "plugin site"

echo
echo "== public check"
for u in https://subspacefoundation.com/ https://subspacefoundation.com/arena-owners.html \
         https://subspacefoundation.com/arena/ https://subspacefoundation.com/arena/api/public \
         https://subspacefoundation.com/arena https://subspacefoundation.com/account/signup \
         https://subspacefoundation.com/account; do
  printf '%-50s %s\n' "$u" "$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' "$u")"
done
