#!/usr/bin/env bash
set -euo pipefail
stage="${1:?usage: install-public-site.sh SITE_SOURCE}"
site=/var/www/foundation
host=subspacefoundation.com
files=(
  style.css home.css portal.js arenas.js jump.webp sector.png
  arena-owner-reference.html arena-owners.html pilot.html client-standard.html
  arenas.html index.html build.html build.js
)
for name in "${files[@]}"; do
  [[ -f "$stage/$name" && ! -L "$stage/$name" ]] || { echo "Missing site file: $name" >&2; exit 1; }
done
systemctl is-active --quiet caddy
test -f "$site/index.html"
backup="$(mktemp -d /root/foundation-site-backup.XXXXXXXX)"
work="$(mktemp -d "$site/.site-stage.XXXXXXXX")"
installed=()
cleanup() {
  result=$?
  trap - EXIT
  if (( result != 0 )); then
    for name in "${installed[@]}"; do
      if [[ -f "$backup/$name" ]]; then
        cp -p -- "$backup/$name" "$site/$name"
      else
        rm -f -- "$site/$name"
      fi
    done
    echo "Site update did not pass verification. Restored prior files from $backup." >&2
  fi
  rm -rf -- "$work"
  exit "$result"
}
trap cleanup EXIT
for name in "${files[@]}"; do
  [[ ! -e "$site/$name" ]] || cp -p -- "$site/$name" "$backup/$name"
  install -m 0644 "$stage/$name" "$work/$name"
done
for name in "${files[@]}"; do
  mv -fT -- "$work/$name" "$site/$name"
  installed+=("$name")
done
for route in / /build.html /build.js /arenas.html /arenas.js /jump.webp /arena-owners.html /pilot.html /client-standard.html /play/ /arena/ /account/signup /arena/api/public; do
  curl -fsS --max-time 10 --resolve "$host:443:127.0.0.1" "https://$host$route" -o /dev/null
done
echo "Published https://$host/ and https://$host/arenas.html"
echo "Previous static files: $backup"
