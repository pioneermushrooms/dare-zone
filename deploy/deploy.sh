#!/bin/bash
# Deploys Dare.Arenas to the Foundation zone (subspacefoundation.com). Safe to re-run.
#
#   deploy/deploy.sh [--sysop <name>] [--no-restart]
#
#   DEPLOY_HOST   ssh target (default root@5.161.238.245)
#
# Builds the plugin (Release), copies it with deploy/ and site/ to
# /root/dare-arenas-deploy-<utc timestamp>/ on the server, and runs deploy/remote-install.sh there,
# which backs up every file it changes (/root/backup-arena-ownership-<timestamp>/), installs the
# module, groups, template, site pages and Caddy route, and restarts the zone (see that script).
#
# Rollback (on the server, B=$(cat /root/backup-arena-ownership-latest)):
#   cat $B/conf/Modules.config > /opt/subspace/zone/conf/Modules.config   # unloads the module
#   systemctl restart subspace
# and, to undo everything else: copy $B/conf/{groupdef.conf,groupdef.dir/*,staff.conf} back,
# cat $B/Caddyfile > /etc/caddy/Caddyfile && systemctl reload caddy, and copy $B/site/* back.
# Owned arenas, data/dare-arenas.db and bin/modules/Dare.Arenas can stay; they do nothing unloaded.
set -euo pipefail

HOST=${DEPLOY_HOST:-root@5.161.238.245}
REPO=$(cd "$(dirname "$0")/.." && pwd)
[ -d /usr/local/share/dotnet ] && export PATH=/usr/local/share/dotnet:$PATH

echo "== build (Release)"
rm -rf "$REPO/out/modules/Dare.Arenas"
dotnet build "$REPO/src/Dare.Arenas" -c Release -nologo -v quiet
PLUGIN="$REPO/out/modules/Dare.Arenas"
[ -f "$PLUGIN/Dare.Arenas.dll" ] || { echo "build produced no $PLUGIN/Dare.Arenas.dll" >&2; exit 1; }
if ls "$PLUGIN" | grep -qi sqlite; then echo "plugin output carries sqlite files; refusing" >&2; exit 1; fi

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/plugin" "$STAGE/site"
cp -R "$REPO/deploy" "$STAGE/deploy"
cp "$PLUGIN"/* "$STAGE/plugin/"
cp "$REPO/site/index.html" "$REPO/site/arena-owners.html" "$STAGE/site/"

TS=$(date -u +%Y%m%d-%H%M%S)
REMOTE=/root/dare-arenas-deploy-$TS
echo "== copy to $HOST:$REMOTE"
COPYFILE_DISABLE=1 tar --no-xattrs -C "$STAGE" -cz . | ssh "$HOST" "mkdir -p '$REMOTE' && tar -C '$REMOTE' -xz --no-same-owner"

echo "== install"
# shellcheck disable=SC2029  # args are expanded locally on purpose
ssh "$HOST" bash "$REMOTE/deploy/remote-install.sh" "$REMOTE" "$@"

echo
echo "== public check"
for u in https://subspacefoundation.com/ https://subspacefoundation.com/arena-owners.html \
         https://subspacefoundation.com/arena/ https://subspacefoundation.com/arena/api/public \
         https://subspacefoundation.com/arena; do
  printf '%-50s %s\n' "$u" "$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' "$u")"
done
