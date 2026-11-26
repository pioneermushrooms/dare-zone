#!/bin/bash
# Installs the ship service (ships/: the ship generator behind a loopback HTTP API) on the
# Foundation server, as systemd unit dare-ships. Run from your Mac. Safe to re-run.
#
#   deploy/deploy-ships.sh [--set-key]
#
#   --set-key     also asks (without echo) for the PixelLab API key and stores it on the server in
#                 /etc/dare-ships.env (root, mode 600). Without a key, Random ships work and Prompt
#                 answers "not set up on this server yet".
#   DEPLOY_HOST   ssh target (default root@5.161.238.245)
#
# On the server: /opt/dare-ships (code + venv with Pillow and requests), service user dare-ships,
# listening on 127.0.0.1:8089 only. The zone's web server (Dare.Arenas) calls it for
# /account/api/shipyard/generate; set DARE_SHIPS_URL in the zone's environment to move it.
# The key goes over ssh's stdin, never on a command line, and is never printed or logged.
set -euo pipefail

HOST=${DEPLOY_HOST:-root@5.161.238.245}
SSH=${SSH:-ssh}
SET_KEY=0
[ "${1:-}" = "--set-key" ] && SET_KEY=1
cd "$(dirname "$0")/.."

KEY=""
if [ $SET_KEY = 1 ]; then
  [ -t 0 ] || { echo "run this in a terminal: it asks for the key without echoing it" >&2; exit 2; }
  read -rsp "PixelLab API key (input hidden): " KEY; echo >&2
  if [ -z "$KEY" ] || [[ $KEY == *[[:space:]]* ]] || [[ $KEY == *[^[:print:]]* ]]; then
    echo "empty key, or whitespace/control characters in it; nothing was changed" >&2; exit 1
  fi
fi

echo "== copying ships/ to $HOST:/opt/dare-ships"
tar -C ships -czf - shipgen.py service.py requirements.txt | $SSH "$HOST" 'set -e
  id -u dare-ships >/dev/null 2>&1 || useradd --system --home /opt/dare-ships --shell /usr/sbin/nologin dare-ships
  mkdir -p /opt/dare-ships && tar -C /opt/dare-ships -xzf - && chown -R root:root /opt/dare-ships'

echo "== venv, unit, start"
# shellcheck disable=SC2016  # the $-expressions are meant for the remote shell
printf '%s\n' "$KEY" | $SSH "$HOST" 'set -euo pipefail
IFS= read -r key || true
if [ -n "$key" ]; then
  umask 077
  printf "PIXEL_LAB_API_KEY=%s\n" "$key" > /etc/dare-ships.env.tmp && mv /etc/dare-ships.env.tmp /etc/dare-ships.env
  echo "PixelLab key stored in /etc/dare-ships.env"
fi
[ -f /etc/dare-ships.env ] || { umask 077; : > /etc/dare-ships.env; }
command -v python3 >/dev/null || { echo "python3 is missing" >&2; exit 1; }
[ -x /opt/dare-ships/venv/bin/python ] || python3 -m venv /opt/dare-ships/venv \
  || { echo "python3 -m venv failed: apt-get install python3-venv, then re-run" >&2; exit 1; }
/opt/dare-ships/venv/bin/pip install -q --upgrade -r /opt/dare-ships/requirements.txt
cat > /etc/systemd/system/dare-ships.service <<UNIT
[Unit]
Description=Foundation ship generator (loopback only)
After=network.target

[Service]
User=dare-ships
WorkingDirectory=/opt/dare-ships
EnvironmentFile=/etc/dare-ships.env
ExecStart=/opt/dare-ships/venv/bin/python /opt/dare-ships/service.py --host 127.0.0.1 --port 8089
Restart=on-failure
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable --now dare-ships >/dev/null
systemctl restart dare-ships
for i in $(seq 1 20); do
  if out=$(curl -fsS http://127.0.0.1:8089/health 2>/dev/null); then echo "dare-ships healthy: $out"; exit 0; fi
  sleep 1
done
echo "dare-ships did not answer on 127.0.0.1:8089; journalctl -u dare-ships -n 50" >&2; exit 1'
