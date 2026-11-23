#!/bin/bash
# Applies the Dare.Arenas deploy files to a SubspaceServer zone directory. Safe to re-run.
#
#   deploy/apply-local.sh <zoneDir> [pluginDir]
#
# - copies groupdef.dir/arenaowner + arenamod into <zone>/conf/groupdef.dir/ (overwrites: module-owned files)
# - appends the [arenaowner]/[arenamod] sections to conf/groupdef.conf (once)
# - appends default-caps.txt to groupdef.dir/default and mod-caps.txt to groupdef.dir/mod (missing lines only)
# - copies arenas/_template/ into <zone>/arenas/_template/ (existing files are kept, so local edits survive)
# - adds the Dare.Arenas <module> line to conf/Modules.config before </modules> (once)
# - if pluginDir is given (e.g. out/modules/Dare.Arenas), replaces <zone>/bin/modules/Dare.Arenas with it
set -euo pipefail

if [ $# -lt 1 ]; then
  echo "usage: $0 <zoneDir> [pluginDir]" >&2
  exit 2
fi

HERE=$(cd "$(dirname "$0")" && pwd)
ZONE=$(cd "$1" && pwd)
CONF="$ZONE/conf"
MODULE_LINE='	<module type="Dare.Arenas.ArenaOwnership" path="bin/modules/Dare.Arenas/Dare.Arenas.dll"/>'

for f in "$CONF/groupdef.conf" "$CONF/groupdef.dir/default" "$CONF/groupdef.dir/mod" "$CONF/Modules.config"; do
  [ -f "$f" ] || { echo "missing $f - is $ZONE a zone directory?" >&2; exit 1; }
done

# ensure a file ends with a newline before appending to it
ensure_nl() { [ -s "$1" ] && [ "$(tail -c1 "$1")" != "" ] && printf '\n' >> "$1" || true; }

append_missing() { # <capsFile> <target>
  local line
  while IFS= read -r line || [ -n "$line" ]; do
    [ -z "$line" ] && continue
    if ! grep -qxF "$line" "$2"; then
      ensure_nl "$2"
      printf '%s\n' "$line" >> "$2"
      echo "  + $line -> ${2#$ZONE/}"
    fi
  done < "$1"
}

cp "$HERE/groupdef.dir/arenaowner" "$HERE/groupdef.dir/arenamod" "$CONF/groupdef.dir/"
echo "groupdef.dir/arenaowner, arenamod copied"

if grep -qxF '[arenaowner]' "$CONF/groupdef.conf"; then
  echo "groupdef.conf already has [arenaowner]"
else
  ensure_nl "$CONF/groupdef.conf"
  cat "$HERE/groupdef-sections.conf" >> "$CONF/groupdef.conf"
  echo "groupdef.conf: [arenaowner] and [arenamod] appended"
fi

if ! grep -qx '; Dare.Arenas' "$CONF/groupdef.dir/default"; then
  ensure_nl "$CONF/groupdef.dir/default"; printf '\n; Dare.Arenas\n' >> "$CONF/groupdef.dir/default"
fi
append_missing "$HERE/default-caps.txt" "$CONF/groupdef.dir/default"
if ! grep -qx '; Dare.Arenas' "$CONF/groupdef.dir/mod"; then
  ensure_nl "$CONF/groupdef.dir/mod"; printf '\n; Dare.Arenas\n' >> "$CONF/groupdef.dir/mod"
fi
append_missing "$HERE/mod-caps.txt" "$CONF/groupdef.dir/mod"

mkdir -p "$ZONE/arenas/_template"
cp -Rn "$HERE/arenas/_template/." "$ZONE/arenas/_template/" 2>/dev/null || true
echo "arenas/_template present"

if grep -qF 'Dare.Arenas.ArenaOwnership' "$CONF/Modules.config"; then
  echo "Modules.config already loads Dare.Arenas"
else
  tmp=$(mktemp)
  awk -v line="$MODULE_LINE" '/<\/modules>/ && !done { print line; done=1 } { print }' "$CONF/Modules.config" > "$tmp"
  grep -qF 'Dare.Arenas.ArenaOwnership' "$tmp" || { rm -f "$tmp"; echo "no </modules> in Modules.config" >&2; exit 1; }
  cat "$tmp" > "$CONF/Modules.config"; rm -f "$tmp"
  echo "Modules.config: Dare.Arenas module line added"
fi

if [ $# -ge 2 ]; then
  SRC=$(cd "$2" && pwd)
  [ -f "$SRC/Dare.Arenas.dll" ] || { echo "$SRC has no Dare.Arenas.dll" >&2; exit 1; }
  rm -rf "$ZONE/bin/modules/Dare.Arenas"
  mkdir -p "$ZONE/bin/modules/Dare.Arenas"
  cp -R "$SRC/." "$ZONE/bin/modules/Dare.Arenas/"
  echo "plugin copied to bin/modules/Dare.Arenas"
fi
