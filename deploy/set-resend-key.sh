#!/bin/bash
# Sets the Resend API key of the Foundation zone, so account codes go out by real email.
# Run by Chris from his Mac (not on the server):
#
#   deploy/set-resend-key.sh
#
#   DEPLOY_HOST   ssh target (default root@5.161.238.245)
#   ZONE_CONF     zone conf folder on the server (default /opt/subspace/zone/conf)
#
# Prompts for the key without echo, sends it to the server over ssh's stdin (never on a command
# line, so it is not visible in `ps`; never printed or logged), writes
#   $ZONE_CONF/dare-secrets.conf   ([Resend] / ApiKey = <key>)   owner subspace, mode 600
# then restarts the subspace service and prints only whether the plugin log says real email is on.
# The key must start with "re_". Restarting the zone drops everyone online for a moment.
set -euo pipefail

HOST=${DEPLOY_HOST:-root@5.161.238.245}
ZONE_CONF=${ZONE_CONF:-/opt/subspace/zone/conf}
SSH=${SSH:-ssh}

if [ ! -t 0 ] && [ -z "${ALLOW_PIPED_KEY:-}" ]; then
  echo "run this in a terminal: it asks for the key without echoing it" >&2; exit 2
fi
read -rsp "Resend API key (input hidden): " KEY; echo >&2
case "$KEY" in
  re_?*) ;;
  *) echo "that does not look like a Resend key (it must start with re_); nothing was changed" >&2; exit 1 ;;
esac
if [[ $KEY == *[[:space:]]* ]] || [[ $KEY == *[^[:print:]]* ]]; then
  echo "the key contains whitespace or control characters; nothing was changed" >&2; exit 1
fi

# The remote script is a fixed text (only ZONE_CONF is substituted, a path); the key arrives on stdin.
# shellcheck disable=SC2016  # the $-expressions are meant for the remote shell
REMOTE='set -euo pipefail
conf='"'$ZONE_CONF'"'
IFS= read -r key
case "$key" in re_?*) ;; *) echo "remote: no key received" >&2; exit 1 ;; esac
umask 077
tmp=$(mktemp "$conf/.dare-secrets.XXXXXX")
printf "[Resend]\nApiKey = %s\n" "$key" > "$tmp"
unset key
chown subspace:subspace "$tmp"
chmod 600 "$tmp"
mv "$tmp" "$conf/dare-secrets.conf"
echo "dare-secrets.conf written (owner subspace, mode 600)"
since=$(date "+%Y-%m-%d %H:%M:%S")
systemctl restart subspace
for i in $(seq 1 30); do
  log=$(journalctl -u subspace --since "$since" --no-pager -o cat 2>/dev/null || true)
  if grep -q "Account emails are sent with Resend" <<<"$log"; then echo "plugin log: real email is ON (Resend)"; exit 0; fi
  if grep -q "Real email is OFF" <<<"$log"; then echo "plugin log: real email is still OFF - check the key file" >&2; exit 1; fi
  sleep 2
done
echo "plugin log: no email line yet - check the zone (journalctl -u subspace)" >&2; exit 1'

printf '%s\n' "$KEY" | $SSH "$HOST" "$REMOTE"
