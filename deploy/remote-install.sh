#!/bin/bash
# Installs Dare.Arenas on the Foundation server. Runs ON the server as root; deploy/deploy.sh
# copies a staging folder there and calls this script. Safe to re-run.
#
#   remote-install.sh <stageDir> [--sysop <name>] [--no-restart]
#
# <stageDir> holds deploy/ (this folder from the repo), plugin/ (the Release build of
# Dare.Arenas) and site/ (index.html, arena-owners.html).
#
# What it does, in order:
#  0. refuses to run unless the zone's server is SubspaceServer v4.0.0 and the zone's bin/ has the
#     Microsoft.Data.Sqlite / SQLitePCLRaw files the plugin uses (the plugin ships no copy)
#  1. backs up every file it may change to /root/backup-arena-ownership-<utc timestamp>/
#  2. runs deploy/apply-local.sh on the zone: module line, groupdef sections and files, default
#     caps, arenas/_template (existing files kept), the plugin folder bin/modules/Dare.Arenas
#  3. --sysop <name>: adds "<name> = sysop" to staff.conf [(global)] unless the name is already
#     listed there (an existing entry is never changed)
#  4. copies site/*.html to /var/www/foundation
#  5. adds deploy/Caddyfile.snippet to the subspacefoundation.com block of /etc/caddy/Caddyfile
#     (once), validates and reloads Caddy; a config that does not validate is not installed
#  6. unless --no-restart: reports players online, restarts subspace, checks that the module
#     loaded and the web pages answer on 127.0.0.1:8088 only; on failure it restores
#     Modules.config from the backup (zone runs without the module) and restarts again
#
# Never deletes arenas, maps, data or staff entries.
set -euo pipefail

[ $# -ge 1 ] || { echo "usage: $0 <stageDir> [--sysop <name>] [--no-restart]" >&2; exit 2; }
STAGE=$(cd "$1" && pwd); shift
SYSOP=""
RESTART=1
while [ $# -gt 0 ]; do
  case "$1" in
    --sysop) SYSOP=${2:?--sysop needs a name}; shift 2 ;;
    --no-restart) RESTART=0; shift ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done

ZONE=${ZONE:-/opt/subspace/zone}
SITE=${SITE:-/var/www/foundation}
CADDYFILE=${CADDYFILE:-/etc/caddy/Caddyfile}
SITE_HOST=${SITE_HOST:-subspacefoundation.com}
ZONE_PING_PORT=${ZONE_PING_PORT:-5001}
TS=$(date -u +%Y%m%d-%H%M%S)
BACKUP=/root/backup-arena-ownership-$TS
n=2; while [ -e "$BACKUP" ]; do BACKUP=/root/backup-arena-ownership-$TS-$n; n=$((n+1)); done
OWNER=$(stat -c '%U:%G' "$ZONE/conf")

die() { echo "ERROR: $*" >&2; exit 1; }
step() { echo; echo "== $*"; }

# ---- 0. preflight -------------------------------------------------------------------------
step "preflight"
grep -q '"SS.Core/4.0.0"' "$ZONE/bin/SubspaceServer.deps.json" \
  || die "$ZONE does not run SubspaceServer v4.0.0 (SS.Core/4.0.0 not in bin/SubspaceServer.deps.json); the plugin is built against v4.0.0"
for f in Microsoft.Data.Sqlite.dll SQLitePCLRaw.core.dll SQLitePCLRaw.batteries_v2.dll SQLitePCLRaw.provider.e_sqlite3.dll libe_sqlite3.so; do
  [ -f "$ZONE/bin/$f" ] || die "zone bin/ has no $f (the plugin uses the server's copy)"
done
[ -f "$STAGE/plugin/Dare.Arenas.dll" ] || die "$STAGE/plugin has no Dare.Arenas.dll"
if ls "$STAGE/plugin" | grep -qi sqlite; then die "plugin folder carries its own sqlite files; it must use the server's"; fi
[ -f "$STAGE/site/index.html" ] && [ -f "$STAGE/site/arena-owners.html" ] || die "$STAGE/site is incomplete"
echo "SubspaceServer v4.0.0, server sqlite present, plugin OK"

# ---- 1. backup ----------------------------------------------------------------------------
step "backup to $BACKUP"
mkdir -p "$BACKUP/conf" "$BACKUP/zone-bin-modules" "$BACKUP/zone-arenas"
cp -a "$ZONE/conf/Modules.config" "$ZONE/conf/groupdef.conf" "$ZONE/conf/groupdef.dir" \
      "$ZONE/conf/staff.conf" "$ZONE/conf/passwd.conf" "$BACKUP/conf/"
cp -a "$CADDYFILE" "$BACKUP/Caddyfile"
[ -d "$SITE" ] && cp -a "$SITE" "$BACKUP/site"
[ -d "$ZONE/bin/modules/Dare.Arenas" ] && cp -a "$ZONE/bin/modules/Dare.Arenas" "$BACKUP/zone-bin-modules/"
[ -d "$ZONE/arenas/_template" ] && cp -a "$ZONE/arenas/_template" "$BACKUP/zone-arenas/"
echo "$BACKUP" > /root/backup-arena-ownership-latest
ls -R "$BACKUP" | sed 's/^/  /'

# ---- 2. zone files ------------------------------------------------------------------------
step "zone files ($ZONE)"
bash "$STAGE/deploy/apply-local.sh" "$ZONE" "$STAGE/plugin"
chown -R "$OWNER" "$ZONE/bin/modules/Dare.Arenas" "$ZONE/arenas/_template"
for f in arenaowner arenamod arenacaps arenastaff arenaadmin; do chown "$OWNER" "$ZONE/conf/groupdef.dir/$f"; done

# ---- 3. sysop -----------------------------------------------------------------------------
if [ -n "$SYSOP" ]; then
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

# ---- 4. static site -----------------------------------------------------------------------
step "site ($SITE)"
mkdir -p "$SITE"
for f in index.html arena-owners.html; do
  if cmp -s "$STAGE/site/$f" "$SITE/$f"; then echo "$f unchanged"; else install -m 644 "$STAGE/site/$f" "$SITE/$f"; echo "$f installed"; fi
done

# ---- 5. caddy -----------------------------------------------------------------------------
step "caddy ($CADDYFILE)"
if grep -qF 'handle_path /arena/*' "$CADDYFILE"; then
  echo "Caddyfile already proxies /arena/*"
else
  tmp=$(mktemp --suffix=.Caddyfile)
  # insert the snippet (comments dropped) right after the "<SITE_HOST> {" line
  awk -v snip="$STAGE/deploy/Caddyfile.snippet" -v host="$SITE_HOST" '
    { print }
    !done && NF == 2 && $1 == host && $2 == "{" {
      print "\t# Dare.Arenas web pages (dare-zone deploy/Caddyfile.snippet); header_up Host is required"
      while ((getline l < snip) > 0) { if (l ~ /^[ \t]*#/ || l ~ /^[ \t]*$/) continue; print "\t" l }
      print ""
      done = 1
    }
    END { if (!done) exit 3 }
  ' "$CADDYFILE" > "$tmp" || { rm -f "$tmp"; die "no '$SITE_HOST {' line in $CADDYFILE"; }
  caddy validate --config "$tmp" --adapter caddyfile >/dev/null 2>&1 \
    || { caddy validate --config "$tmp" --adapter caddyfile || true; rm -f "$tmp"; die "new Caddyfile does not validate; not installed"; }
  cat "$tmp" > "$CADDYFILE"; rm -f "$tmp"
  echo "snippet added inside '$SITE_HOST {'"
fi
caddy validate --config "$CADDYFILE" --adapter caddyfile 2>&1 | tail -1
systemctl reload caddy
echo "caddy reloaded: $(systemctl is-active caddy)"

# ---- 6. restart ---------------------------------------------------------------------------
players_online() {
  python3 - "$ZONE_PING_PORT" <<'EOF' 2>/dev/null || echo "unknown"
import socket, struct, sys
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.settimeout(3)
s.sendto(struct.pack("<I", 4242), ("127.0.0.1", int(sys.argv[1])))
print(struct.unpack("<I", s.recv(64)[:4])[0])
EOF
}

module_check() { # <since> -> 0 when the module is up
  local since=$1 i
  for i in $(seq 1 60); do
    local log; log=$(journalctl -u subspace --since "$since" --no-pager -o cat 2>/dev/null || true)
    if grep -q '<Dare.Arenas> Web pages listening' <<<"$log"; then return 0; fi
    if grep -qE 'E <ModuleManager>|exited: error loading modules' <<<"$log"; then return 1; fi
    if [ "$(systemctl is-active subspace)" = "failed" ]; then return 1; fi
    sleep 2
  done
  return 1
}

if [ "$RESTART" = 1 ]; then
  step "restart subspace"
  echo "players online right before restart: $(players_online)"
  since=$(date '+%Y-%m-%d %H:%M:%S')
  systemctl restart subspace
  if module_check "$since"; then
    journalctl -u subspace --since "$since" --no-pager -o cat | grep -E 'Dare\.Arenas|^E <' | sed 's/^/  /'
    sleep 1
    ss -ltn | grep -E ':8088\b' | sed 's/^/  /'
    if ss -ltn | awk '{print $4}' | grep -E ':8088$' | grep -vq '^127\.0\.0\.1:8088$'; then
      echo "WARNING: something listens on 8088 on a non-loopback address" >&2
    fi
    code=$(curl -s -o /dev/null -w '%{http_code}' -H 'Host: 127.0.0.1:8088' http://127.0.0.1:8088/api/public)
    echo "local /api/public: $code"
    echo "subspace: $(systemctl is-active subspace); players online now: $(players_online)"
  else
    echo "MODULE DID NOT COME UP - rolling back Modules.config from $BACKUP" >&2
    journalctl -u subspace --since "$since" --no-pager -o cat | tail -60 >&2
    cat "$BACKUP/conf/Modules.config" > "$ZONE/conf/Modules.config"
    systemctl restart subspace
    sleep 10
    echo "subspace after rollback: $(systemctl is-active subspace)" >&2
    exit 1
  fi
else
  echo; echo "(--no-restart: restart subspace yourself to load the module)"
fi

echo; echo "done. backup: $BACKUP"
