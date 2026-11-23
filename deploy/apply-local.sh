#!/bin/bash
# Applies the Dare.Arenas deploy files to a SubspaceServer zone directory. Safe to re-run.
#
#   deploy/apply-local.sh <zoneDir> [pluginDir]
#
# - copies groupdef.dir/{arenaowner,arenamod,arenacaps,arenastaff,arenaadmin} into <zone>/conf/groupdef.dir/
#   (overwrites: module-owned files)
# - adds "#include groupdef.dir/arenastaff" to the [mod], [smod] and [sysop] sections of
#   conf/groupdef.conf (once each), so only real staff can approve/decline requests
# - adds "#include groupdef.dir/arenaadmin" to [smod] and [sysop] (staff overrides, ?arenaadmin)
# - replaces the Dare.Arenas [arenaowner]/[arenamod] block in conf/groupdef.conf with
#   groupdef-sections.conf (also upgrades the old block that included groupdef.dir/mod)
# - removes the old staff caps that earlier versions appended to groupdef.dir/mod
# - appends default-caps.txt to groupdef.dir/default (missing lines only)
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
      echo "  + $line -> ${2#"$ZONE"/}"
    fi
  done < "$1"
}

for f in arenaowner arenamod arenacaps arenastaff arenaadmin; do
  cp "$HERE/groupdef.dir/$f" "$CONF/groupdef.dir/"
done
echo "groupdef.dir/arenaowner, arenamod, arenacaps, arenastaff, arenaadmin copied"

GD="$CONF/groupdef.conf"
tmp=$(mktemp)

# 1. drop any existing Dare.Arenas block (new format ends with the end marker; the old one,
#    written by the first version of this script, ended with "#include groupdef.dir/arenamod")
awk '
  $0 == "; ---- Dare.Arenas: player-owned arenas (granted arena-scoped by the module) ----" { skip=1; next }
  skip && $0 == "; ---- end Dare.Arenas ----" { skip=0; next }
  skip && $0 == "#include groupdef.dir/arenamod" { skip=2; next }
  skip == 2 && $0 == "; ---- end Dare.Arenas ----" { skip=0; next }
  skip == 2 { skip=0 }
  skip { next }
  { print }
' "$GD" > "$tmp"
# trim trailing blank lines left behind
awk '{ lines[NR]=$0 } END { n=NR; while (n>0 && lines[n] ~ /^[ \t]*$/) n--; for (i=1;i<=n;i++) print lines[i] }' "$tmp" > "$tmp.2"

# 2. make sure [mod], [smod], [sysop] include arenastaff and [smod], [sysop] include arenaadmin
#    (added at the end of each section; blank lines at the end of a section are held back so the
#    includes stay inside it)
awk '
  function flush() {
    if (want && !seen) print "#include groupdef.dir/arenastaff"
    if (wantadm && !seenadm) print "#include groupdef.dir/arenaadmin"
    printf "%s", blanks; blanks=""
  }
  /^[ \t]*$/ { blanks = blanks $0 "\n"; next }
  /^\[.*\][ \t]*$/ { flush(); sec=tolower($0); want=(sec=="[mod]" || sec=="[smod]" || sec=="[sysop]"); wantadm=(sec=="[smod]" || sec=="[sysop]"); seen=0; seenadm=0 }
  want && $0 == "#include groupdef.dir/arenastaff" { seen=1 }
  wantadm && $0 == "#include groupdef.dir/arenaadmin" { seenadm=1 }
  { printf "%s", blanks; blanks=""; print }
  END { flush() }
' "$tmp.2" > "$tmp"

# 3. append the current Dare.Arenas block
printf '\n' >> "$tmp"
cat "$HERE/groupdef-sections.conf" >> "$tmp"
if cmp -s "$tmp" "$GD"; then
  echo "groupdef.conf already up to date"
else
  cat "$tmp" > "$GD"
  echo "groupdef.conf: arenastaff/arenaadmin includes + [arenaowner]/[arenamod] block written"
fi
rm -f "$tmp" "$tmp.2"

if ! grep -qx '; Dare.Arenas' "$CONF/groupdef.dir/default"; then
  ensure_nl "$CONF/groupdef.dir/default"; printf '\n; Dare.Arenas\n' >> "$CONF/groupdef.dir/default"
fi
append_missing "$HERE/default-caps.txt" "$CONF/groupdef.dir/default"

# undo what earlier versions appended to groupdef.dir/mod (staff caps now live in arenastaff)
if grep -qxE 'cmd_approvearena|cmd_declinearena|; Dare.Arenas' "$CONF/groupdef.dir/mod"; then
  grep -vxE 'cmd_approvearena|cmd_declinearena|; Dare.Arenas' "$CONF/groupdef.dir/mod" \
    | awk '{ l[NR]=$0 } END { n=NR; while (n>0 && l[n] ~ /^[ \t]*$/) n--; for (i=1;i<=n;i++) print l[i] }' > "$tmp" || true
  cat "$tmp" > "$CONF/groupdef.dir/mod"; rm -f "$tmp"
  echo "groupdef.dir/mod: old Dare.Arenas staff caps removed"
fi

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
