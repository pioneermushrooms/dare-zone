#!/bin/bash
# Installs (or updates) the browser game's log collector on Foundation. Safe to re-run.
#
#   deploy/install-clientlog.sh
#
#   DEPLOY_HOST   ssh target (default root@5.161.238.245)
#
# Copies deploy/clientlog/collector.py to /opt/dare-clientlog/, installs and (re)starts the
# dare-clientlog service on 127.0.0.1:8091, and adds a /clientlog route to the
# subspacefoundation.com block of /etc/caddy/Caddyfile if it has none. A Caddyfile that doesn't
# validate is not installed. Reports land in /var/lib/dare-clientlog/<date>.jsonl; read them with
# deploy/client-logs.sh. Neither the zone nor the game gateway is restarted.
#
# Remove: systemctl disable --now dare-clientlog; rm /etc/systemd/system/dare-clientlog.service;
# take the /clientlog block out of the Caddyfile and reload caddy; rm -r /opt/dare-clientlog.
set -euo pipefail

HOST=${DEPLOY_HOST:-root@5.161.238.245}
HERE=$(cd "$(dirname "$0")" && pwd)

ssh -o BatchMode=yes "$HOST" 'mkdir -p /opt/dare-clientlog'
scp -q "$HERE/clientlog/collector.py" "$HOST:/opt/dare-clientlog/collector.py.new"
scp -q "$HERE/clientlog/dare-clientlog.service" "$HOST:/etc/systemd/system/dare-clientlog.service.new"
ssh "$HOST" bash -s <<'REMOTE'
set -euo pipefail
python3 -I -m py_compile /opt/dare-clientlog/collector.py.new
mv /opt/dare-clientlog/collector.py.new /opt/dare-clientlog/collector.py
chmod 0644 /opt/dare-clientlog/collector.py
mv /etc/systemd/system/dare-clientlog.service.new /etc/systemd/system/dare-clientlog.service
systemctl daemon-reload
systemctl enable --quiet dare-clientlog
systemctl restart dare-clientlog
sleep 1
systemctl is-active dare-clientlog

CADDYFILE=/etc/caddy/Caddyfile
if grep -qF 'handle /clientlog' "$CADDYFILE"; then
  echo "Caddyfile already routes /clientlog"
else
  cp -a "$CADDYFILE" "/root/Caddyfile.before-clientlog.$(date -u +%Y%m%dT%H%M%SZ)"
  tmp=$(mktemp)
  awk '
    { print }
    /^subspacefoundation\.com \{/ && !done {
      print "\t# The browser game'"'"'s log reports (dare-zone deploy/install-clientlog.sh)"
      print "\thandle /clientlog {"
      print "\t\treverse_proxy 127.0.0.1:8091"
      print "\t}"
      print ""
      done = 1
    }
    END { if (!done) exit 1 }
  ' "$CADDYFILE" > "$tmp" || { rm -f "$tmp"; echo "no subspacefoundation.com block in $CADDYFILE" >&2; exit 1; }
  caddy validate --config "$tmp" --adapter caddyfile >/dev/null 2>&1 \
    || { caddy validate --config "$tmp" --adapter caddyfile || true; rm -f "$tmp"; echo "new Caddyfile does not validate; not installed" >&2; exit 1; }
  cat "$tmp" > "$CADDYFILE"
  rm -f "$tmp"
  systemctl reload caddy
  echo "added /clientlog to the Caddyfile; caddy $(systemctl is-active caddy)"
fi
REMOTE
