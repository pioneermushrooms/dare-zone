#!/bin/bash
# Test harness for deploy/remote-install.sh (runs on macOS/Linux, no root, no server): fake zone + stubbed systemctl/journalctl/caddy/ss/curl.
#   deploy/test-remote-install.sh <scenario>
# scenarios: redeploy-fail first-fail auth-rollback auth-install caddy-account-only public-8088 rollback-fails caddy-fail err-trap players players-bots sysop-bad sysop-ok store-backup store-backup-nocli oss-bot-host
# Needs a Release build in out/modules/Dare.Arenas (deploy.sh or dotnet build -c Release).
set -uo pipefail
REPO=$(cd "$(dirname "$0")/.." && pwd)
SC=$1
H=${TMPDIR:-/tmp}/dare-remote-install-test/$SC; rm -rf "$H"; mkdir -p "$H"/{stubs,root,www,zone/bin/modules,zone/arenas,zone/data,stage/dare-arenas-deploy-new}
export FAKE=$H
# fake zone from the pristine v4.0.0 sample conf
cp -R $REPO/vendor/SubspaceServer/src/SubspaceServer/Zone/conf "$H/zone/conf"
printf '{ "libraries": { "SS.Core/4.0.0": {} } }\n' > "$H/zone/bin/SubspaceServer.deps.json"
for f in Microsoft.Data.Sqlite.dll SQLitePCLRaw.core.dll SQLitePCLRaw.batteries_v2.dll SQLitePCLRaw.provider.e_sqlite3.dll libe_sqlite3.so; do touch "$H/zone/bin/$f"; done
printf 'subspacefoundation.com {\n\troot * /var/www/foundation\n\tfile_server\n}\n' > "$H/Caddyfile"
# staging folder like deploy.sh makes
S=$H/stage/dare-arenas-deploy-new; mkdir -p "$S/plugin" "$S/site"; cp -R $REPO/deploy "$S/deploy"
cp $REPO/out/modules/Dare.Arenas/* "$S/plugin/"; cp $REPO/site/* "$S/site/"
for o in 1 2 3 4; do mkdir -p "$H/stage/dare-arenas-deploy-2026010$o"; done
echo up > "$H/state"; echo "${PLAYERS:-0}" > "$H/players"; : > "$H/journal"
# stubs
cat > "$H/stubs/systemctl" <<'S'
#!/bin/bash
echo "systemctl $*" >> "$FAKE/calls"
case "$1" in
  is-active) [ "$2" = subspace ] && { [ "$(cat $FAKE/state)" = up ] && echo active || echo failed; } || echo active ;;
  reload) ;;
  restart)
    : > "$FAKE/journal"
    M="$FAKE/zone/conf/Modules.config"
    if grep -qF Dare.Arenas.ArenaOwnership "$M"; then
      if [ -e "$FAKE/zone/bin/modules/Dare.Arenas/BROKEN" ]; then
        echo 'E <ModuleManager> Failed to load module [Dare.Arenas.ArenaOwnership]' >> "$FAKE/journal"; echo down > "$FAKE/state"
      else
        echo 'I <Dare.Arenas> Web pages listening on http://127.0.0.1:8088/ (fake)' >> "$FAKE/journal"; echo up > "$FAKE/state"
      fi
    else echo 'I <Core> zone started without Dare.Arenas (fake)' >> "$FAKE/journal"; echo up > "$FAKE/state"; fi ;;
esac
S
cat > "$H/stubs/journalctl" <<'S'
#!/bin/bash
cat "$FAKE/journal"
S
cat > "$H/stubs/caddy" <<'S'
#!/bin/bash
echo "Valid configuration"
S
cat > "$H/stubs/ss" <<'S'
#!/bin/bash
echo "State Recv-Q Send-Q Local Address:Port Peer"
if grep -q Web "$FAKE/journal"; then [ -e "$FAKE/public" ] && echo "LISTEN 0 500 0.0.0.0:8088 0.0.0.0:*" || echo "LISTEN 0 500 127.0.0.1:8088 0.0.0.0:*"; fi
S
cat > "$H/stubs/curl" <<'S'
#!/bin/bash
printf 200
S
chmod +x "$H"/stubs/*
export DEPLOY_LOCK_CHECK=0 PATH="$H/stubs:$PATH" ZONE=$H/zone SITE=$H/www CADDYFILE=$H/Caddyfile BACKUP_ROOT=$H/root HEALTH_TRIES=3 HEALTH_SLEEP=0
export ZONE_PING_CMD='[ "$(cat $FAKE/state)" = up ] && cat $FAKE/players || echo unknown'
RI="$S/deploy/remote-install.sh"
case $SC in
  redeploy-fail)
    echo "### install 1 (good build)"; bash "$RI" "$S" >/dev/null 2>&1; echo "exit $?"
    echo "good build" > "$H/zone/bin/modules/Dare.Arenas/MARK-previous"
    touch "$S/plugin/BROKEN"
    echo "### install 2 (broken build)"; bash "$RI" "$S" 2>&1 | grep -vE '^  ' ; echo "exit ${PIPESTATUS[0]}"
    echo "### after: live plugin folder"; ls "$H/zone/bin/modules/Dare.Arenas"
    echo "### module line still in Modules.config: $(grep -c Dare.Arenas.ArenaOwnership $H/zone/conf/Modules.config)"
    echo "### failed build kept: $(ls $H/root/backup-arena-ownership-*/failed-Dare.Arenas 2>/dev/null | grep -c BROKEN)"
    echo "### zone state: $(cat $H/state); journal: $(cat $H/journal)" ;;
  first-fail)
    touch "$S/plugin/BROKEN"
    bash "$RI" "$S" 2>&1 | grep -vE '^  ' ; echo "exit ${PIPESTATUS[0]}"
    echo "### module line in Modules.config: $(grep -c Dare.Arenas.ArenaOwnership $H/zone/conf/Modules.config)"
    echo "### zone state: $(cat $H/state); journal: $(cat $H/journal)" ;;
  auth-install)
    bash "$RI" "$S" >/dev/null 2>&1; echo "exit $?"
    M=$H/zone/conf/Modules.config
    echo "### live AuthFile lines: $(grep -cE '^[[:space:]]*<module[[:space:]]+type="SS.Core.Modules.AuthFile' $M)"
    echo "### FoundationAuth line before AuthBan: $([ "$(grep -n 'Dare.Arenas.FoundationAuth' $M | head -1 | cut -d: -f1)" -lt "$(grep -n 'SS.Core.Modules.AuthBan' $M | head -1 | cut -d: -f1)" ] && echo yes || echo NO)"
    echo "### cmd_passwd in default: $(grep -cx cmd_passwd $H/zone/conf/groupdef.dir/default)"
    echo "### passwd.conf backed up: $(ls $H/root/backup-arena-ownership-*/conf/passwd.conf 2>/dev/null | wc -l | tr -d ' ')"
    echo "### Caddyfile: arena $(grep -c 'handle_path /arena/\*' $H/Caddyfile), account $(grep -c 'handle /account/\*' $H/Caddyfile), account redir $(grep -c 'redir /account ' $H/Caddyfile)"
    echo "### re-run is idempotent:"; cp $H/Caddyfile $H/Caddyfile.1; cp $M $H/Modules.1
    bash "$RI" "$S" >/dev/null 2>&1; echo "exit $?"
    cmp $H/Caddyfile $H/Caddyfile.1 && cmp $M $H/Modules.1 && echo "unchanged" ;;
  auth-rollback)
    M=$H/zone/conf/Modules.config
    cp $M $H/Modules.orig; cp $H/zone/conf/groupdef.dir/default $H/default.orig; cp $H/Caddyfile $H/Caddyfile.orig
    touch "$S/plugin/BROKEN"
    bash "$RI" "$S" 2>&1 | grep -vE '^  ' ; echo "exit ${PIPESTATUS[0]}"
    echo "### live AuthFile lines: $(grep -cE '^[[:space:]]*<module[[:space:]]+type="SS.Core.Modules.AuthFile' $M)"
    echo "### FoundationAuth lines: $(grep -c Dare.Arenas.FoundationAuth $M); ArenaOwnership lines: $(grep -c Dare.Arenas.ArenaOwnership $M); AuthBan lines: $(grep -c 'SS.Core.Modules.AuthBan' $M)"
    cmp $M $H/Modules.orig && echo "### Modules.config identical to the original"
    cmp $H/zone/conf/groupdef.dir/default $H/default.orig && echo "### groupdef.dir/default identical to the original (cmd_passwd back: $(grep -cx cmd_passwd $H/zone/conf/groupdef.dir/default))"
    cmp $H/Caddyfile $H/Caddyfile.orig && echo "### Caddyfile identical to the original"
    echo "### zone state: $(cat $H/state); journal: $(cat $H/journal)" ;;
  caddy-account-only)
    printf 'subspacefoundation.com {\n\troot * /var/www/foundation\n\thandle_path /arena/* {\n\t\treverse_proxy 127.0.0.1:8088\n\t}\n\tfile_server\n}\n' > "$H/Caddyfile"
    bash "$RI" "$S" --no-restart 2>&1 | grep -E 'Caddyfile|snippet|ERROR'
    echo "### arena blocks: $(grep -c 'handle_path /arena/\*' $H/Caddyfile), account blocks: $(grep -c 'handle /account/\*' $H/Caddyfile)" ;;
  public-8088)
    bash "$RI" "$S" >/dev/null 2>&1; echo "good build" > "$H/zone/bin/modules/Dare.Arenas/MARK-previous"; touch "$H/public"
    bash "$RI" "$S" 2>&1 | grep -vE '^  /' | sed -n '/restart subspace/,$p'; echo "exit ${PIPESTATUS[0]}"
    echo "### previous plugin back: $(ls $H/zone/bin/modules/Dare.Arenas | grep -c MARK-previous)" ;;
  rollback-fails)
    touch "$S/plugin/BROKEN"; bash "$RI" "$S" >/dev/null 2>&1   # first install fails -> module line stripped
    echo "### re-add module line by hand and deploy the broken build again (previous build also broken)"
    bash "$S/deploy/apply-local.sh" "$H/zone" >/dev/null
    bash "$RI" "$S" 2>&1 | grep -vE '^  /' | sed -n '/restart subspace/,$p'; echo "exit ${PIPESTATUS[0]}" ;;
  caddy-fail)
    printf '#!/bin/bash\necho "Error: adapting config using caddyfile: fake failure" >&2; exit 1\n' > "$H/stubs/caddy"
    bash "$RI" "$S" 2>&1 | grep -vE '^  /' | sed -n '/== caddy/,$p'; echo "exit ${PIPESTATUS[0]}"
    echo "### module line: $(grep -c Dare.Arenas.ArenaOwnership $H/zone/conf/Modules.config); plugin dir: $(ls -d $H/zone/bin/modules/Dare.Arenas 2>/dev/null || echo none); restarts: $(cat "$H/calls" 2>/dev/null | grep -c restart)" ;;
  err-trap)
    mv "$H/zone/conf/groupdef.dir/mod" "$H/zone/conf/groupdef.dir/mod.away"
    bash "$RI" "$S" 2>&1 | grep -vE '^  /' | sed -n '/== zone files/,$p' | head -8; echo "exit ${PIPESTATUS[0]}"
    echo "### restarts: $(cat "$H/calls" 2>/dev/null | grep -c restart)" ;;
  players)
    echo 2 > "$H/players"
    bash "$RI" "$S" 2>&1; echo "exit $?"
    echo "### backups made: $(ls -d $H/root/backup-* 2>/dev/null | wc -l | tr -d ' '); restarts: $(cat "$H/calls" 2>/dev/null | grep -c restart); module line: $(grep -c Dare.Arenas.ArenaOwnership $H/zone/conf/Modules.config)"
    echo "### with --force:"; bash "$RI" "$S" --force 2>&1 | grep -E 'players|restart|done'; echo "exit ${PIPESTATUS[0]}" ;;
  players-bots)
    # the zone's count already leaves arena bots out ([arenabot] excludepopulation): running
    # oss-bot processes are not subtracted, so 1 pilot refuses even with 3 bot processes about
    printf '#!/bin/bash\necho 3\n' > "$H/stubs/pgrep"; chmod +x "$H/stubs/pgrep"
    echo 1 > "$H/players"
    bash "$RI" "$S" 2>&1 | grep -E 'players online|ERROR'; echo "exit ${PIPESTATUS[0]}"
    echo 0 > "$H/players"
    bash "$RI" "$S" 2>&1 | grep -E 'players online|ERROR|^done'; echo "exit ${PIPESTATUS[0]}" ;;
  sysop-bad)
    for n in 'evil]' 'a = sysop' 'x;y' 'arena:bob' $'two\nlines' ' lead' 'abcdefghijklmnopqrstuvwx' ''; do
      out=$(bash "$RI" "$S" --no-restart --sysop "$n" 2>&1 | grep -E 'ERROR|added'); printf '%-28q -> %s\n' "$n" "$out"
    done
    echo "### staff.conf touched: $(grep -c ' = sysop' $H/zone/conf/staff.conf)" ;;
  sysop-ok)
    bash "$RI" "$S" --no-restart --sysop 'Dare Dev' 2>&1 | grep -E 'sysop|ERROR'; echo "exit ${PIPESTATUS[0]}"
    bash "$RI" "$S" --no-restart --sysop 'dare dev' 2>&1 | grep -E 'sysop|ERROR'
    sed -n '/^\[(global)\]/,/^\[/p' $H/zone/conf/staff.conf | grep -v '^;' ;;
  oss-bot-host)
    # the oss-bot it will run must have --host (one host per arena); an old one would leave every bot off
    fake_bot() { printf '#!/bin/bash\necho "Usage: oss-bot [options]"\n%s\n' "$2" > "$1"; chmod +x "$1"; }
    mkdir -p "$H/zone/bin/modules/Dare.Arenas"; echo "old plugin" > "$H/zone/bin/modules/Dare.Arenas/MARK-previous"
    echo "### kept oss-bot without --host:"
    fake_bot "$H/zone/bin/modules/Dare.Arenas/oss-bot" 'echo "  --script <file>"'
    bash "$RI" "$S" 2>&1 | grep -E '^oss-bot|ERROR|WARNING|^done'; echo "exit ${PIPESTATUS[0]}"
    echo "### nothing touched: backups $(ls -d $H/root/backup-* 2>/dev/null | wc -l | tr -d ' '), restarts $(cat "$H/calls" 2>/dev/null | grep -c restart), old plugin $(ls $H/zone/bin/modules/Dare.Arenas | grep -c MARK-previous)"
    echo "### bundled oss-bot without --host (kept one has it):"
    fake_bot "$H/zone/bin/modules/Dare.Arenas/oss-bot" 'echo "  --host   Run an arena'"'"'s bots"'
    fake_bot "$S/plugin/oss-bot" 'exit 2'
    bash "$RI" "$S" 2>&1 | grep -E '^oss-bot|ERROR|WARNING|^done'; echo "exit ${PIPESTATUS[0]}"
    echo "### kept oss-bot with --host:"
    rm "$S/plugin/oss-bot"
    bash "$RI" "$S" 2>&1 | grep -E '^oss-bot|ERROR|WARNING|^done'; echo "exit ${PIPESTATUS[0]}"
    echo "### installed oss-bot has --host: $("$H/zone/bin/modules/Dare.Arenas/oss-bot" --help | grep -c -- --host)"
    echo "### bundled oss-bot with --host:"
    fake_bot "$S/plugin/oss-bot" 'echo "  --host   new"'
    bash "$RI" "$S" 2>&1 | grep -E '^oss-bot|ERROR|WARNING|^done'; echo "exit ${PIPESTATUS[0]}"
    echo "### no oss-bot anywhere (first install): warns, installs"
    rm -f "$S/plugin/oss-bot"; rm -rf "$H/zone/bin/modules/Dare.Arenas" "$H"/root/backup-*
    bash "$RI" "$S" 2>&1 | grep -E '^oss-bot|ERROR|WARNING|^done'; echo "exit ${PIPESTATUS[0]}" ;;
  store-backup|store-backup-nocli)
    sqlite3 "$H/zone/data/dare-arenas.db" "CREATE TABLE arenas(name TEXT); INSERT INTO arenas VALUES('skyrace');"
    if [ "$SC" = store-backup-nocli ]; then
      # hide the sqlite3 CLI: a PATH without /usr/bin's sqlite3, but with python3 and core tools
      mkdir -p "$H/nocli"; for t in /usr/bin/* /bin/* /usr/sbin/* /sbin/*; do [ "$(basename "$t")" = sqlite3 ] || ln -sf "$t" "$H/nocli/" 2>/dev/null; done
      [ -x "$(command -v python3)" ] && ln -sf "$(command -v python3)" "$H/nocli/python3"
      PATH="$H/stubs:$H/nocli" bash "$RI" "$S" --no-restart 2>&1 | grep -E 'store backup|ERROR|done'
    else
      bash "$RI" "$S" --no-restart 2>&1 | grep -E 'store backup|ERROR'
    fi
    echo "### backed-up rows: $(sqlite3 $H/root/backup-arena-ownership-*/data/dare-arenas.db 'SELECT name FROM arenas' 2>&1)"
    printf '### restore text warns about passwd.conf: '; grep -c 'NEVER restore it wholesale' "$S/deploy/remote-install.sh" ;;
esac
echo "### staging folders left: $(ls $H/stage | tr '\n' ' ')"
