#!/bin/bash
# The browser game's log reports, from your Mac (collected by deploy/install-clientlog.sh).
#
#   deploy/client-logs.sh                 one line per visit in the last 2 days: pilot, platform,
#                                         browser, screen, updates/draws per second, ping, stalls, loss
#   deploy/client-logs.sh NAME            everything those visits sent (NAME: pilot, guest name or id)
#   deploy/client-logs.sh --days 7 [NAME] look further back (kept 14 days)
#
#   DEPLOY_HOST   ssh target (default root@5.161.238.245)
set -euo pipefail
HOST=${DEPLOY_HOST:-root@5.161.238.245}
HERE=$(cd "$(dirname "$0")" && pwd)
args=$(printf ' %q' "$@")
ssh -o BatchMode=yes "$HOST" "python3 -I - $args" < "$HERE/clientlog/show.py"
