#!/bin/bash
# Deploys Dare.Arenas to the Foundation zone (subspacefoundation.com). Safe to re-run.
#
#   deploy/deploy.sh [--sysop <name>] [--no-restart] [--force]
#
#   --force       restart the zone even with players online (default: refuse)
#
#   DEPLOY_HOST   ssh target (default root@5.161.238.245)
#
# Builds the plugin (Release), copies it with deploy/ and site/ to
# /root/dare-arenas-deploy-<utc timestamp>/ on the server, and runs deploy/remote-install.sh there,
# which backs up every file it changes (/root/backup-arena-ownership-<timestamp>/), installs the
# module, groups, template, site pages and Caddy route, and restarts the zone (see that script).
#
# Rollback: remote-install.sh rolls back by itself when the new build does not come up (previous
# plugin folder on a re-deploy, module line removed on a first install). By hand, on the server,
# with B=$(cat /root/backup-arena-ownership-latest):
#   cat $B/conf/Modules.config > /opt/subspace/zone/conf/Modules.config
#   cat $B/conf/groupdef.conf > /opt/subspace/zone/conf/groupdef.conf
#   cp -a $B/conf/groupdef.dir/. /opt/subspace/zone/conf/groupdef.dir/
#   cat $B/conf/staff.conf > /opt/subspace/zone/conf/staff.conf
#   passwd.conf: NEVER restore it wholesale - players claim their names there with ?passwd, so an
#     old copy deletes every password set since. Copy back single lines from $B/conf/passwd.conf.
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
cp "$REPO/site/index.html" "$REPO/site/arena-owners.html" "$STAGE/site/"

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

echo
echo "== public check"
for u in https://subspacefoundation.com/ https://subspacefoundation.com/arena-owners.html \
         https://subspacefoundation.com/arena/ https://subspacefoundation.com/arena/api/public \
         https://subspacefoundation.com/arena; do
  printf '%-50s %s\n' "$u" "$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' "$u")"
done
