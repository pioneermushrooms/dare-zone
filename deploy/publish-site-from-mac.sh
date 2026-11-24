#!/usr/bin/env bash
set -euo pipefail
revision="${1:?usage: publish-site-from-mac.sh VERIFIED_COMMIT}"
[[ "$revision" =~ ^[a-f0-9]{40}$ ]] || { echo "Expected a full commit SHA." >&2; exit 2; }
host="${DEPLOY_HOST:-root@5.161.238.245}"
scratch="$(mktemp -d)"
trap 'rm -rf -- "$scratch"' EXIT
git init -q "$scratch"
git -C "$scratch" fetch -q --depth=1 https://github.com/pioneermushrooms/dare-zone.git "$revision"
git -C "$scratch" archive -o "$scratch/site.tar.gz" --format=tar.gz FETCH_HEAD site deploy/install-public-site.sh
remote="$(ssh "$host" 'mktemp -d /tmp/foundation-site.XXXXXXXX')"
[[ "$remote" =~ ^/tmp/foundation-site\.[a-zA-Z0-9]+$ ]] || { echo "Unexpected staging directory." >&2; exit 1; }
scp "$scratch/site.tar.gz" "$host:$remote/site.tar.gz"
ssh "$host" "cd '$remote' && tar -xzf site.tar.gz && bash deploy/install-public-site.sh site"
ssh "$host" "rm -rf -- '$remote'"
