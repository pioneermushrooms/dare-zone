#!/bin/bash
# Applies the Dare.Arenas deploy files to a SubspaceServer zone directory. Safe to re-run.
#
#   deploy/apply-local.sh <zoneDir> [pluginDir]
#   deploy/apply-local.sh --restore-authfile <zoneDir>   (undo the game-login switch below)
#
# - copies groupdef.dir/{arenaowner,arenamod,arenacaps,arenastaff,arenaadmin,arenabot} into <zone>/conf/groupdef.dir/
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
# - game logins from Foundation accounts: comments out the SS.Core.Modules.AuthFile line in
#   conf/Modules.config and puts the Dare.Arenas.FoundationAuth line in its place (it must load
#   before AuthBan, which wraps whichever IAuth is registered when it loads); removes cmd_passwd
#   from groupdef.dir/default (players change passwords on the site). --restore-authfile undoes
#   exactly this (AuthFile line back, FoundationAuth line out, cmd_passwd back).
# - if pluginDir is given (e.g. out/modules/Dare.Arenas), replaces <zone>/bin/modules/Dare.Arenas with it
set -euo pipefail

RESTORE_AUTHFILE=0
if [ "${1:-}" = "--restore-authfile" ]; then RESTORE_AUTHFILE=1; shift; fi

if [ $# -lt 1 ]; then
  echo "usage: $0 <zoneDir> [pluginDir]  |  $0 --restore-authfile <zoneDir>" >&2
  exit 2
fi

HERE=$(cd "$(dirname "$0")" && pwd)
ZONE=$(cd "$1" && pwd)
CONF="$ZONE/conf"
MODULE_LINE='	<module type="Dare.Arenas.ArenaOwnership" path="bin/modules/Dare.Arenas/Dare.Arenas.dll"/>'

AUTH_TYPE='Dare.Arenas.FoundationAuth'
AUTH_LINE='	<module type="Dare.Arenas.FoundationAuth" path="bin/modules/Dare.Arenas/Dare.Arenas.dll"/>'
AUTHFILE_LINE='	<module type="SS.Core.Modules.AuthFile, SS.Core" />'
AUTHFILE_OFF='	<!--<module type="SS.Core.Modules.AuthFile, SS.Core" />--><!-- Dare.Arenas: game logins come from FoundationAuth -->'
# the live AuthFile module line (any spacing); the commented-out one this script writes
AUTHFILE_RE='^[[:space:]]*<module[[:space:]]+type="SS\.Core\.Modules\.AuthFile,[[:space:]]*SS\.Core"[[:space:]]*/>[[:space:]]*$'
AUTHFILE_OFF_RE='^[[:space:]]*<!--<module type="SS\.Core\.Modules\.AuthFile, SS\.Core" />--><!-- Dare\.Arenas: game logins come from FoundationAuth -->[[:space:]]*$'

for f in "$CONF/groupdef.conf" "$CONF/groupdef.dir/default" "$CONF/groupdef.dir/mod" "$CONF/Modules.config"; do
  [ -f "$f" ] || { echo "missing $f - is $ZONE a zone directory?" >&2; exit 1; }
done

# ensure a file ends with a newline before appending to it
ensure_nl() { [ -s "$1" ] && [ "$(tail -c1 "$1")" != "" ] && printf '\n' >> "$1" || true; }

if [ "$RESTORE_AUTHFILE" = 1 ]; then
  tmp=$(mktemp)
  RE="$AUTHFILE_OFF_RE" awk -v live="$AUTHFILE_LINE" -v auth="$AUTH_TYPE" '
    BEGIN { re = ENVIRON["RE"] }
    $0 ~ re { print live; next }
    index($0, "type=\"" auth "\"") { next }
    { print }
  ' "$CONF/Modules.config" > "$tmp"
  if ! grep -Eq "$AUTHFILE_RE" "$tmp"; then
    # no commented line to bring back: put AuthFile before AuthBan
    awk -v live="$AUTHFILE_LINE" '/SS\.Core\.Modules\.AuthBan/ && !done { print live; done=1 } { print }' "$tmp" > "$tmp.2"
    cat "$tmp.2" > "$tmp"; rm -f "$tmp.2"
  fi
  cat "$tmp" > "$CONF/Modules.config"; rm -f "$tmp"
  echo "Modules.config: AuthFile restored, FoundationAuth line removed"
  if ! grep -qxF 'cmd_passwd' "$CONF/groupdef.dir/default"; then
    ensure_nl "$CONF/groupdef.dir/default"; printf 'cmd_passwd\n' >> "$CONF/groupdef.dir/default"
    echo "groupdef.dir/default: cmd_passwd restored"
  fi
  exit 0
fi

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

for f in arenaowner arenamod arenacaps arenastaff arenaadmin arenabot; do
  cp "$HERE/groupdef.dir/$f" "$CONF/groupdef.dir/"
done
echo "groupdef.dir/arenaowner, arenamod, arenacaps, arenastaff, arenaadmin, arenabot copied"

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

# game logins: FoundationAuth replaces AuthFile, at AuthFile's place (before AuthBan)
tmp=$(mktemp)
RE="$AUTHFILE_RE" awk -v off="$AUTHFILE_OFF" -v line="$AUTH_LINE" -v auth="$AUTH_TYPE" '
  BEGIN { has = 0; re = ENVIRON["RE"] }
  { lines[NR] = $0; if (index($0, "type=\"" auth "\"")) has = 1 }
  END {
    for (i = 1; i <= NR; i++) {
      if (lines[i] ~ re) { print off; if (!has) { print line; has = 1 }; continue }
      if (!has && lines[i] ~ /SS\.Core\.Modules\.AuthBan/) { print line; has = 1 }
      if (!has && lines[i] ~ /Dare\.Arenas\.ArenaOwnership/) { print line; has = 1 }
      print lines[i]
    }
  }
' "$CONF/Modules.config" > "$tmp"
fa=$(grep -nF "type=\"$AUTH_TYPE\"" "$tmp" | head -1 | cut -d: -f1)
ab=$(grep -nE '^[[:space:]]*<module[[:space:]]+type="SS\.Core\.Modules\.AuthBan' "$tmp" | head -1 | cut -d: -f1)
[ -n "$fa" ] || { rm -f "$tmp"; echo "could not place the FoundationAuth line in Modules.config" >&2; exit 1; }
if [ -n "$ab" ] && [ "$fa" -gt "$ab" ]; then
  rm -f "$tmp"; echo "Modules.config: the FoundationAuth line must come before AuthBan - fix it by hand" >&2; exit 1
fi
if grep -Eq "$AUTHFILE_RE" "$tmp"; then
  rm -f "$tmp"; echo "Modules.config still loads AuthFile - fix it by hand" >&2; exit 1
fi
if cmp -s "$tmp" "$CONF/Modules.config"; then
  echo "Modules.config already uses FoundationAuth (AuthFile off)"
else
  cat "$tmp" > "$CONF/Modules.config"
  echo "Modules.config: AuthFile commented out, FoundationAuth line in its place"
fi
rm -f "$tmp"

if grep -qxF 'cmd_passwd' "$CONF/groupdef.dir/default"; then
  tmp=$(mktemp)
  grep -vxF 'cmd_passwd' "$CONF/groupdef.dir/default" > "$tmp" || true
  cat "$tmp" > "$CONF/groupdef.dir/default"; rm -f "$tmp"
  echo "groupdef.dir/default: cmd_passwd removed (passwords change on the site)"
fi

if [ $# -ge 2 ]; then
  SRC=$(cd "$2" && pwd)
  [ -f "$SRC/Dare.Arenas.dll" ] || { echo "$SRC has no Dare.Arenas.dll" >&2; exit 1; }
  rm -rf "$ZONE/bin/modules/Dare.Arenas"
  mkdir -p "$ZONE/bin/modules/Dare.Arenas"
  cp -R "$SRC/." "$ZONE/bin/modules/Dare.Arenas/"
  echo "plugin copied to bin/modules/Dare.Arenas"
fi
