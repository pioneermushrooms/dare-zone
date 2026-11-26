#!/bin/bash
# Installs Dare.Arenas on the Foundation server. Runs ON the server as root; deploy/deploy.sh
# copies a staging folder there and calls this script. Safe to re-run.
#
#   remote-install.sh <stageDir> [--sysop <name>] [--no-restart] [--force]
#
# <stageDir> holds deploy/ (this folder from the repo), plugin/ (the Release build of
# Dare.Arenas) and site/ (index.html, arena-owners.html).
#
# What it does, in order:
#  0. preflight: the zone's server must be SubspaceServer v4.0.0 with the Microsoft.Data.Sqlite /
#     SQLitePCLRaw files the plugin uses in bin/ (the plugin ships no copy); a --sysop name must be
#     a plain player name; unless --no-restart, nobody may be online (override: --force)
#  1. backs up every file it may change to /root/backup-arena-ownership-<utc timestamp>/
#     (incl. the current bin/modules/Dare.Arenas, if any) and a consistent copy of the module's
#     store data/dare-arenas.db (sqlite3 .backup, or Python's sqlite3 backup API without sqlite3)
#  2. copies static site files to /var/www/foundation
#  3. adds deploy/Caddyfile.snippet (/arena/* and /account/*) to the subspacefoundation.com block of
#     /etc/caddy/Caddyfile (each part once), validates and reloads Caddy; a config that does not validate is not installed
#     (steps 2-3 come first so a failure there leaves the zone untouched)
#  4. runs deploy/apply-local.sh on the zone: module line, AuthFile commented out and the
#     FoundationAuth line in its place (before AuthBan; the plugin imports passwd.conf [Users] once
#     on first load), cmd_passwd removed from groupdef.dir/default, groupdef sections and files,
#     default caps, arenas/_template (existing files kept), the plugin folder bin/modules/Dare.Arenas;
#     --sysop <name>: adds "<name> = sysop" to staff.conf [(global)] unless already listed there
#  5. unless --no-restart: checks players online again, restarts subspace, and requires the module
#     to load, 8088 to listen on loopback only and the local API to answer 200. Otherwise it rolls
#     back: the previous bin/modules/Dare.Arenas from the backup (re-deploy), Modules.config and
#     groupdef.dir/default from the backup (so AuthFile is the login module again: the zone must never
#     run without one), the Caddyfile and the static site; then restarts and checks the zone is healthy.
#  Any error after step 4 starts prints the backup folder and the restore commands.
#  NEVER restore passwd.conf wholesale (existing rule; it is only the import source now, but an old
#  copy would still undo password changes made while AuthFile was active). Copy single lines.
#
# Never deletes arenas, maps, data or staff entries. Keeps the 3 newest staging folders
# (dare-arenas-deploy-*) beside <stageDir> and moves nothing else.
#
# Test seams (production defaults): DEPLOY_LOCK_CHECK, ZONE, SITE, CADDYFILE, BACKUP_ROOT, ZONE_PING_CMD,
# HEALTH_TRIES, HEALTH_SLEEP.
set -Eeuo pipefail

usage() { echo "usage: $0 <stageDir> [--sysop <name>] [--no-restart] [--force]" >&2; exit 2; }
[ $# -ge 1 ] || usage
STAGE=$(cd "$1" && pwd); shift
SYSOP=""
SYSOP_SET=0
RESTART=1
FORCE=0
while [ $# -gt 0 ]; do
  case "$1" in
    --sysop) [ $# -ge 2 ] || usage; SYSOP=$2; SYSOP_SET=1; shift 2 ;;
    --no-restart) RESTART=0; shift ;;
    --force) FORCE=1; shift ;;
    *) echo "unknown option $1" >&2; usage ;;
  esac
done

# Only a guarded deploy may install (it holds the deploy lock: deploy/live-guard.sh).
DEPLOY_LOCK_FILE=${DEPLOY_LOCK_FILE:-/var/lock/foundation-deploy.lock}
if [ "${DEPLOY_LOCK_CHECK:-1}" = 1 ] && flock -n "$DEPLOY_LOCK_FILE" true; then
  echo "Refused: no deploy holds $DEPLOY_LOCK_FILE. Deploy from your Mac with deploy/deploy.sh (see deploy/README.md)." >&2
  exit 1
fi

ZONE=${ZONE:-/opt/subspace/zone}
SITE=${SITE:-/var/www/foundation}
CADDYFILE=${CADDYFILE:-/etc/caddy/Caddyfile}
SITE_HOST=${SITE_HOST:-subspacefoundation.com}
BACKUP_ROOT=${BACKUP_ROOT:-/root}
ZONE_PING_PORT=${ZONE_PING_PORT:-5001}
HEALTH_TRIES=${HEALTH_TRIES:-60}
HEALTH_SLEEP=${HEALTH_SLEEP:-2}
MODULE_TYPE='Dare.Arenas.ArenaOwnership'
TS=$(date -u +%Y%m%d-%H%M%S)
BACKUP=$BACKUP_ROOT/backup-arena-ownership-$TS
n=2; while [ -e "$BACKUP" ]; do BACKUP=$BACKUP_ROOT/backup-arena-ownership-$TS-$n; n=$((n+1)); done
OWNER=$(stat -c '%U:%G' "$ZONE/conf" 2>/dev/null || stat -f '%Su:%Sg' "$ZONE/conf")

die() { echo "ERROR: $*" >&2; exit 1; }
step() { echo; echo "== $*"; }

# ---- helpers ------------------------------------------------------------------------------
zone_ping() { # the zone's own count (arena bots left out), or "unknown" when it does not answer
  if [ -n "${ZONE_PING_CMD:-}" ]; then bash -c "$ZONE_PING_CMD"; return; fi
  python3 - "$ZONE_PING_PORT" <<'EOF' 2>/dev/null || echo "unknown"
import socket, struct, sys
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.settimeout(3)
s.sendto(struct.pack("<I", 4242), ("127.0.0.1", int(sys.argv[1])))
print(struct.unpack("<I", s.recv(64)[:4])[0])
EOF
}

# People online, or "unknown" when the zone does not answer. Arena bots are already left out of
# the zone's count: they are in the [arenabot] group, whose excludepopulation leaves them out.
players_online() { zone_ping; }

zone_log_since() { journalctl -u subspace --since "$1" --no-pager -o cat 2>/dev/null || true; }

# wait_healthy <since> <expectModule 0|1>: the zone answers pings (and, if expected, the module
# says its web pages listen); fails early on a module load error or a failed unit
wait_healthy() {
  local since=$1 want=$2 i log
  for ((i = 0; i < HEALTH_TRIES; i++)); do
    log=$(zone_log_since "$since")
    if grep -qE 'E <ModuleManager>|exited: error loading modules' <<<"$log"; then return 1; fi
    if [ "$(systemctl is-active subspace || true)" = "failed" ]; then return 1; fi
    if [ "$(players_online)" != "unknown" ]; then
      if [ "$want" = 0 ] || grep -q '<Dare.Arenas> Web pages listening' <<<"$log"; then return 0; fi
    fi
    sleep "$HEALTH_SLEEP"
  done
  return 1
}

# backup_db <src> <dest>: an online-safe copy of a SQLite database (the zone may be running)
backup_db() {
  if command -v sqlite3 >/dev/null 2>&1; then
    sqlite3 "$1" ".backup '$2'"
  else
    python3 -c 'import sqlite3, sys
s = sqlite3.connect(sys.argv[1]); d = sqlite3.connect(sys.argv[2])
s.backup(d); d.close(); s.close()' "$1" "$2"
  fi
}

restore_commands() {
  cat >&2 <<EOF
  Backup: $BACKUP
  Restore (on this server):
    cat $BACKUP/conf/Modules.config > $ZONE/conf/Modules.config
    cat $BACKUP/conf/groupdef.conf > $ZONE/conf/groupdef.conf
    # (Modules.config and groupdef.dir/default also bring AuthFile and cmd_passwd back)
    cp -a $BACKUP/conf/groupdef.dir/. $ZONE/conf/groupdef.dir/
    cat $BACKUP/conf/staff.conf > $ZONE/conf/staff.conf
    # passwd.conf: NEVER restore it wholesale;
    #   copy back only the lines you changed by hand from $BACKUP/conf/passwd.conf
    # data/dare-arenas.db (only if the store itself is broken; stop subspace first):
    #   cp $BACKUP/data/dare-arenas.db $ZONE/data/dare-arenas.db
    [ -d $BACKUP/zone-bin-modules/Dare.Arenas ] && { mv $ZONE/bin/modules/Dare.Arenas $BACKUP/failed-Dare.Arenas-\$(date +%s); cp -a $BACKUP/zone-bin-modules/Dare.Arenas $ZONE/bin/modules/; }
    cat $BACKUP/Caddyfile > $CADDYFILE && caddy validate --config $CADDYFILE --adapter caddyfile && systemctl reload caddy
    cp -a $BACKUP/site/. $SITE/
    systemctl restart subspace
EOF
}

on_err() {
  local code=$? line=$1
  trap - ERR
  echo >&2
  echo "!!!! remote-install.sh failed (exit $code, line $line) after zone files started changing." >&2
  echo "!!!! subspace was NOT restarted by this run; the running zone still uses what it loaded." >&2
  restore_commands
  exit "$code"
}

rollback() {
  echo >&2
  echo "!!!! NEW BUILD DID NOT COME UP - rolling back" >&2
  local expect since
  if [ -d "$BACKUP/zone-bin-modules/Dare.Arenas" ]; then
    mv "$ZONE/bin/modules/Dare.Arenas" "$BACKUP/failed-Dare.Arenas"
    cp -a "$BACKUP/zone-bin-modules/Dare.Arenas" "$ZONE/bin/modules/Dare.Arenas"
    echo "restored the previous plugin folder from $BACKUP (failed build kept in $BACKUP/failed-Dare.Arenas)" >&2
    expect=1
  else
    expect=0
  fi
  if [ -f "$BACKUP/zone-bin-libe_sqlite3.so" ] && ! cmp -s "$BACKUP/zone-bin-libe_sqlite3.so" "$ZONE/bin/libe_sqlite3.so"; then
    cp -a "$BACKUP/zone-bin-libe_sqlite3.so" "$ZONE/bin/libe_sqlite3.so"
    echo "restored the previous native SQLite from $BACKUP" >&2
  fi
  # the login module: put Modules.config and groupdef.dir/default back as they were, so AuthFile
  # (or whatever was active before this run) is the login module again and cmd_passwd works again
  cat "$BACKUP/conf/Modules.config" > "$ZONE/conf/Modules.config"
  cat "$BACKUP/conf/groupdef.dir/default" > "$ZONE/conf/groupdef.dir/default"
  echo "restored Modules.config and groupdef.dir/default from $BACKUP (login module as before this run)" >&2
  if [ "$expect" = 0 ]; then echo "first install: the zone runs without $MODULE_TYPE" >&2; fi
  # caddy and the static site back to what they were (the /account route has no backend any more)
  cat "$BACKUP/Caddyfile" > "$CADDYFILE"
  if caddy validate --config "$CADDYFILE" --adapter caddyfile >/dev/null 2>&1 && systemctl reload caddy; then
    echo "restored the Caddyfile and reloaded caddy" >&2
  else
    echo "!!!! could not validate/reload the restored Caddyfile - check $CADDYFILE by hand" >&2
  fi
  if [ -d "$BACKUP/site" ]; then
    for path in "$STAGE/site/"*; do
      [ -f "$path" ] && [ ! -L "$path" ] || continue
      f=${path##*/}
      if [ ! -e "$BACKUP/site/$f" ]; then rm -f -- "$SITE/$f"; fi
    done
    cp -a "$BACKUP/site/." "$SITE/"
    echo "restored the static site" >&2
  fi
  since=$(date '+%Y-%m-%d %H:%M:%S')
  systemctl restart subspace
  if wait_healthy "$since" "$expect"; then
    echo "rollback OK: subspace $(systemctl is-active subspace || true), zone answers ($(players_online) players)" >&2
  else
    echo "" >&2
    echo "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!" >&2
    echo "!!!! ROLLBACK DID NOT BRING THE ZONE BACK. subspace: $(systemctl is-active subspace || true). FIX BY HAND NOW." >&2
    echo "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!" >&2
    zone_log_since "$since" | tail -40 >&2
    restore_commands
  fi
  exit 1
}

# ---- 0. preflight -------------------------------------------------------------------------
step "preflight"
grep -q '"SS.Core/4.0.0"' "$ZONE/bin/SubspaceServer.deps.json" \
  || die "$ZONE does not run SubspaceServer v4.0.0 (SS.Core/4.0.0 not in bin/SubspaceServer.deps.json); the plugin is built against v4.0.0"
for f in Microsoft.Data.Sqlite.dll SQLitePCLRaw.core.dll SQLitePCLRaw.batteries_v2.dll SQLitePCLRaw.provider.e_sqlite3.dll libe_sqlite3.so; do
  [ -f "$ZONE/bin/$f" ] || die "zone bin/ has no $f (the plugin uses the server's copy)"
done
[ -f "$STAGE/plugin/Dare.Arenas.dll" ] || die "$STAGE/plugin has no Dare.Arenas.dll"
if compgen -G "$STAGE/plugin/*[sS][qQ][lL][iI][tT][eE]*" >/dev/null; then die "plugin folder carries its own sqlite files; it must use the server's"; fi
[ -f "$STAGE/site/index.html" ] && [ -f "$STAGE/site/arena-owners.html" ] || die "$STAGE/site is incomplete"
if [ -f "$ZONE/data/dare-arenas.db" ]; then
  if command -v sqlite3 >/dev/null 2>&1; then echo "store backup: sqlite3"
  elif python3 -c 'import sqlite3' 2>/dev/null; then echo "store backup: python3 sqlite3 (no sqlite3 CLI)"
  else die "neither sqlite3 nor python3's sqlite3 module is available to back up data/dare-arenas.db"; fi
fi
if [ "$SYSOP_SET" = 1 ]; then
  # a plain player name: 1-23 printable characters, no conf syntax ([ ] = ; : #), no edge spaces
  # (glob, not regex: a bracket expression starting with ']' is not portable across regex engines)
  if [ -z "$SYSOP" ] || [ ${#SYSOP} -gt 23 ] || [[ $SYSOP == *[\[\]=\;:#]* ]] \
     || [[ $SYSOP == *[^[:print:]]* ]] || [[ $SYSOP == [[:space:]]* ]] || [[ $SYSOP == *[[:space:]] ]]; then
    die "--sysop name '$SYSOP' is not a plain player name (1-23 printable characters, none of [ ] = ; : #, no leading/trailing spaces)"
  fi
fi
if [ "$RESTART" = 1 ]; then
  online=$(players_online)
  echo "players online: $online"
  if [ "$online" != 0 ] && [ "$FORCE" != 1 ]; then
    die "players online: $online - not restarting the zone; re-run later, with --no-restart, or with --force"
  fi
fi
echo "SubspaceServer v4.0.0, server sqlite present, plugin OK"

# ---- 1. backup ----------------------------------------------------------------------------
step "backup to $BACKUP"
mkdir -p "$BACKUP/conf" "$BACKUP/zone-bin-modules" "$BACKUP/zone-arenas"
cp -a "$ZONE/conf/Modules.config" "$ZONE/conf/groupdef.conf" "$ZONE/conf/groupdef.dir" \
      "$ZONE/conf/staff.conf" "$ZONE/conf/passwd.conf" "$BACKUP/conf/"
cp -a "$CADDYFILE" "$BACKUP/Caddyfile"
if [ -d "$SITE" ]; then cp -a "$SITE" "$BACKUP/site"; fi
if [ -d "$ZONE/bin/modules/Dare.Arenas" ]; then cp -a "$ZONE/bin/modules/Dare.Arenas" "$BACKUP/zone-bin-modules/"; fi
if [ -f "$ZONE/bin/libe_sqlite3.so" ]; then cp -a "$ZONE/bin/libe_sqlite3.so" "$BACKUP/zone-bin-libe_sqlite3.so"; fi
if [ -d "$ZONE/arenas/_template" ]; then cp -a "$ZONE/arenas/_template" "$BACKUP/zone-arenas/"; fi
if [ -f "$ZONE/data/dare-arenas.db" ]; then
  mkdir -p "$BACKUP/data"
  backup_db "$ZONE/data/dare-arenas.db" "$BACKUP/data/dare-arenas.db" || die "backing up data/dare-arenas.db failed"
fi
echo "$BACKUP" > "$BACKUP_ROOT/backup-arena-ownership-latest"
find "$BACKUP" | sed 's/^/  /'

# ---- 2. static site -----------------------------------------------------------------------
step "site ($SITE)"
mkdir -p "$SITE"
for path in "$STAGE/site/"*; do
  [ -f "$path" ] && [ ! -L "$path" ] || continue
  f=${path##*/}
  if cmp -s "$path" "$SITE/$f"; then echo "$f unchanged"; else install -m 644 "$path" "$SITE/$f"; echo "$f installed"; fi
done

# ---- 3. caddy -----------------------------------------------------------------------------
step "caddy ($CADDYFILE)"
want_arena=1; want_account=1
if grep -qF 'handle_path /arena/*' "$CADDYFILE"; then want_arena=0; echo "Caddyfile already proxies /arena/*"; fi
if grep -qF 'handle /account/*' "$CADDYFILE"; then want_account=0; echo "Caddyfile already proxies /account/*"; fi
if [ "$want_arena" = 1 ] || [ "$want_account" = 1 ]; then
  tmp=$(mktemp)
  # insert the wanted parts of the snippet (comments dropped) right after the "<SITE_HOST> {" line;
  # the snippet's arena part is everything before its "redir /account" line, the account part the rest
  awk -v snip="$STAGE/deploy/Caddyfile.snippet" -v host="$SITE_HOST" -v wa="$want_arena" -v wc="$want_account" '
    { print }
    !done && NF == 2 && $1 == host && $2 == "{" {
      print "\t# Dare.Arenas web pages (dare-zone deploy/Caddyfile.snippet); header_up Host is required"
      part = "arena"
      while ((getline l < snip) > 0) {
        if (l ~ /^[ \t]*#/ || l ~ /^[ \t]*$/) continue
        if (l ~ /^redir \/account /) part = "account"
        if ((part == "arena" && wa == 1) || (part == "account" && wc == 1)) print "\t" l
      }
      print ""
      done = 1
    }
    END { if (!done) exit 3 }
  ' "$CADDYFILE" > "$tmp" || { rm -f "$tmp"; die "no '$SITE_HOST {' line in $CADDYFILE"; }
  caddy validate --config "$tmp" --adapter caddyfile >/dev/null 2>&1 \
    || { caddy validate --config "$tmp" --adapter caddyfile || true; rm -f "$tmp"; die "new Caddyfile does not validate; not installed"; }
  cat "$tmp" > "$CADDYFILE"; rm -f "$tmp"
  echo "snippet added inside '$SITE_HOST {' (arena: $want_arena, account: $want_account)"
fi
caddy validate --config "$CADDYFILE" --adapter caddyfile 2>&1 | tail -1
systemctl reload caddy
echo "caddy reloaded: $(systemctl is-active caddy || true)"

# ---- 4. zone files ------------------------------------------------------------------------
trap 'on_err $LINENO' ERR
step "zone files ($ZONE)"
# Arena bots need the oss-bot program beside the plugin. It is built from the client repo and only
# bundled when deploy.sh gets OSS_BOT_LINUX, so otherwise keep the one installed (or, if a deploy
# already dropped it, the newest backed-up copy): a plugin deploy must never switch the bots off.
if [ ! -f "$STAGE/plugin/oss-bot" ]; then
  keep=$(ls -t "$ZONE/bin/modules/Dare.Arenas/oss-bot" "$BACKUP_ROOT"/backup-arena-ownership-*/zone-bin-modules/Dare.Arenas/oss-bot 2>/dev/null | head -1 || true)
  if [ -n "$keep" ]; then
    install -m 0755 "$keep" "$STAGE/plugin/oss-bot"
    echo "oss-bot kept from $keep"
  else
    echo "WARNING: no oss-bot to install: arena bots stay off until a deploy with OSS_BOT_LINUX" >&2
  fi
fi
bash "$STAGE/deploy/apply-local.sh" "$ZONE" "$STAGE/plugin"
chown -R "$OWNER" "$ZONE/bin/modules/Dare.Arenas" "$ZONE/arenas/_template"
# the server's native SQLite (the version the plugin builds against; same C API as before)
if [ -f "$STAGE/native/libe_sqlite3.so" ] && ! cmp -s "$STAGE/native/libe_sqlite3.so" "$ZONE/bin/libe_sqlite3.so"; then
  install -m 0755 "$STAGE/native/libe_sqlite3.so" "$ZONE/bin/libe_sqlite3.so.new"
  chown "$OWNER" "$ZONE/bin/libe_sqlite3.so.new"
  mv -f "$ZONE/bin/libe_sqlite3.so.new" "$ZONE/bin/libe_sqlite3.so"
  echo "native SQLite updated in bin/ (previous one in $BACKUP)"
fi
for f in arenaowner arenamod arenacaps arenastaff arenaadmin arenabot; do chown "$OWNER" "$ZONE/conf/groupdef.dir/$f"; done

if [ "$SYSOP_SET" = 1 ]; then
  step "staff.conf: $SYSOP = sysop"
  STAFF="$ZONE/conf/staff.conf"
  existing=$(awk -v n="$SYSOP" '
    /^[ \t]*\[/ { g = (tolower($0) ~ /^[ \t]*\[\(global\)\][ \t]*$/); next }
    g && /=/ && $0 !~ /^[ \t]*;/ { k=$0; sub(/[ \t]*=.*/, "", k); sub(/^[ \t]+/, "", k); if (tolower(k) == tolower(n)) print }
  ' "$STAFF")
  if [ -n "$existing" ]; then
    echo "already listed in [(global)]: $existing (left unchanged)"
  else
    tmp=$(mktemp)
    if grep -qiE '^[ \t]*\[\(global\)\][ \t]*$' "$STAFF"; then
      awk -v line="$SYSOP = sysop" '{ print } !done && tolower($0) ~ /^[ \t]*\[\(global\)\][ \t]*$/ { print line; done=1 }' "$STAFF" > "$tmp"
    else
      cat "$STAFF" > "$tmp"; printf '\n[(global)]\n%s = sysop\n' "$SYSOP" >> "$tmp"
    fi
    cat "$tmp" > "$STAFF"; rm -f "$tmp"
    echo "added '$SYSOP = sysop' to [(global)]"
  fi
fi
trap - ERR

# ---- 5. restart ---------------------------------------------------------------------------
if [ "$RESTART" = 1 ]; then
  step "restart subspace"
  online=$(players_online)
  echo "players online right before restart: $online"
  if [ "$online" != 0 ] && [ "$FORCE" != 1 ]; then
    echo "players online: $online - NOT restarting. The new files are in place and load on the next restart." >&2
    echo "Backup: $BACKUP" >&2
    exit 1
  fi
  since=$(date '+%Y-%m-%d %H:%M:%S')
  systemctl restart subspace
  wait_healthy "$since" 1 || rollback
  zone_log_since "$since" | grep -E 'Dare\.Arenas|^E <' | sed 's/^/  /' || true
  listen=$(ss -ltn | awk '{print $4}' | grep -E ':8088$' || true)
  echo "  8088 listening on: ${listen:-nothing}"
  if [ "$listen" != "127.0.0.1:8088" ]; then
    echo "8088 must listen on 127.0.0.1 only (got: ${listen:-nothing})" >&2
    rollback
  fi
  code=$(curl -s -o /dev/null -w '%{http_code}' -H 'Host: 127.0.0.1:8088' http://127.0.0.1:8088/api/public || true)
  echo "local /api/public: $code"
  [ "$code" = 200 ] || { echo "local API answered '$code', not 200" >&2; rollback; }
  echo "subspace: $(systemctl is-active subspace || true); players online now: $(players_online)"
else
  echo; echo "(--no-restart: restart subspace yourself to load the module)"
fi

# ---- staging cleanup: keep the 3 newest dare-arenas-deploy-* beside this stage -----------
parent=$(dirname "$STAGE")
case "$(basename "$STAGE")" in
  dare-arenas-deploy-*)
    stages=("$parent"/dare-arenas-deploy-*)   # glob order is sorted, and the names sort by time
    for ((i = 0; i + 3 < ${#stages[@]}; i++)); do
      d=${stages[$i]}
      if [ "$d" != "$STAGE" ]; then rm -rf -- "$d"; echo "removed old staging folder $d"; fi
    done
    ;;
esac

echo; echo "done. backup: $BACKUP"
