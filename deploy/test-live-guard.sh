#!/bin/bash
# Test harness for the rollback guard, deploy lock and live record (deploy/live-guard.sh and the
# scripts using it). No server, no network: throwaway git repos stand in for the site and client
# repositories, a temp dir for the server, and a fake ssh runs commands there locally.
#   deploy/test-live-guard.sh        (exit 0 = all cases passed)
set -uo pipefail
REPO=$(cd "$(dirname "$0")/.." && pwd)
H=$(mktemp -d "${TMPDIR:-/tmp}/live-guard-test.XXXXXX")
trap 'rm -rf "$H"' EXIT
SRV=$H/server
mkdir -p "$SRV/www/play" "$SRV/root" "$SRV/lock" "$H/stubs"
fail=0 n=0

# ---- stubs: ssh runs locally; flock/sha256sum/systemctl/curl for macOS or a box without them
cat > "$H/fakessh" <<EOF
#!/bin/bash
shift   # host
export PATH="$H/stubs:\$PATH" TMPDIR="$H/srvtmp" SITE="$SRV/www" BACKUP_ROOT="$SRV/root" DEPLOY_LOCK_FILE="$SRV/lock/deploy.lock"
mkdir -p "\$TMPDIR"
exec bash -c "\$1"
EOF
cat > "$H/stubs/flock" <<'EOF'
#!/bin/bash
# flock -n <fd> | flock -n <file> <cmd...>
[ "$1" = -n ] && shift
if [[ "$1" =~ ^[0-9]+$ ]]; then
  exec python3 -c 'import fcntl,sys; fcntl.flock(int(sys.argv[1]), fcntl.LOCK_EX|fcntl.LOCK_NB)' "$1" 2>/dev/null
fi
f=$1; shift
exec python3 -c '
import fcntl,os,subprocess,sys
fd=os.open(sys.argv[1], os.O_WRONLY|os.O_CREAT|os.O_APPEND, 0o644)
try: fcntl.flock(fd, fcntl.LOCK_EX|fcntl.LOCK_NB)
except OSError: sys.exit(1)
sys.exit(subprocess.call(sys.argv[2:]))' "$f" "$@"
EOF
if command -v sha256sum >/dev/null; then ln -s "$(command -v sha256sum)" "$H/stubs/sha256sum"
else printf '#!/bin/bash\nexec shasum -a 256 "$@"\n' > "$H/stubs/sha256sum"; fi
printf '#!/bin/bash\nexit 0\n' > "$H/stubs/systemctl"
printf '#!/bin/bash\nexit 0\n' > "$H/stubs/curl"
if ! mv --version >/dev/null 2>&1; then   # BSD mv: install-public-site.sh uses GNU "mv -fT"
  printf '#!/bin/bash\na=(); for x in "$@"; do [ "$x" = -fT ] && x=-f; a+=("$x"); done; exec /bin/mv "${a[@]}"\n' > "$H/stubs/mv"
fi
chmod +x "$H/fakessh" "$H/stubs/"*

export DEPLOY_SSH=$H/fakessh DEPLOY_HOST=fake DEPLOY_SITE_DIR=$SRV/www DEPLOY_PLAY_DIR=$SRV/www/play \
  DEPLOY_LOCK_FILE=$SRV/lock/deploy.lock DEPLOY_BACKUP_ROOT=$SRV/root DEPLOY_LOCK_WAIT=5 \
  DEPLOY_PUBLIC_CHECK=0 GIT_AUTHOR_NAME=t GIT_AUTHOR_EMAIL=t@example.invalid \
  GIT_COMMITTER_NAME=t GIT_COMMITTER_EMAIL=t@example.invalid
export PATH="$H/stubs:$PATH"

# expect <ok|refused> <grep pattern or ""> <name> -- command...
expect() {
  local want=$1 pat=$2 name=$3; shift 4
  n=$((n + 1))
  local out rc
  out=$("$@" 2>&1); rc=$?
  local got=ok; [ $rc -eq 0 ] || got=refused
  if [ "$got" = "$want" ] && { [ -z "$pat" ] || grep -q -- "$pat" <<<"$out"; }; then
    echo "ok   $n $name"
  else
    echo "FAIL $n $name (wanted $want${pat:+ + /$pat/}, got $got rc=$rc)"; sed 's/^/     | /' <<<"$out"; fail=1
  fi
}
check() { n=$((n + 1)); if eval "$2"; then echo "ok   $n $1"; else echo "FAIL $n $1 ($2)"; fail=1; fi; }
field() { python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))[sys.argv[2]])' "$1" "$2"; }

# ---- client repo: bare "dare" remote + checkout on `main`; the "build" writes the commit into the files
git init -q --bare "$H/client.git"
git init -q -b main "$H/client"; C=$H/client
git -C "$C" remote add dare "$H/client.git"
mkdir -p "$C/web" "$C/crates"
printf '#!/bin/bash\necho real build not used\n' > "$C/web/build-client.sh"
ccommit() { echo "$1" > "$C/crates/f.txt"; git -C "$C" add -A; git -C "$C" commit -qm "$1"; git -C "$C" rev-parse HEAD; }
export CLIENT_BUILD_CMD='o=$1; mkdir -p "$o/pkg"; c=$(git rev-parse HEAD)
  for f in index.html start.mjs transport.mjs build.json pkg/oss_client.js pkg/oss_client_bg.wasm; do echo "$c $f" > "$o/$f"; done'
A=$(ccommit A); git -C "$C" push -q dare main
# what is "live" on the fake server: a build of A, plus a file the build does not make
(cd "$C" && bash -c "$CLIENT_BUILD_CMD" _ "$SRV/www/play")
echo keep > "$SRV/www/play/extra.txt"
mkdir -p "$H/buildA" && (cd "$C" && bash -c "$CLIENT_BUILD_CMD" _ "$H/buildA")
DC=("$REPO/deploy/deploy-client.sh" --client "$C")
LR=("$REPO/deploy/live-record.sh")

echo "## client"
expect refused "say who deploys" "no --by"                    -- "${DC[@]}"
expect refused "no live record"  "no live record yet"         -- "${DC[@]}" --by t1
mkdir -p "$H/buildX"; (cd "$C" && bash -c "$CLIENT_BUILD_CMD" _ "$H/buildX"); echo other > "$H/buildX/pkg/oss_client_bg.wasm"
expect refused "not $A"          "seed: checksum mismatch"    -- "${LR[@]}" seed client --repo "$C" --commit "$A" --branch voice-menu --by seed --compare "$H/buildX"
check "mismatch wrote no record" '[ ! -e "$SRV/www/play/DEPLOYED.json" ]'
expect ok "all files match"      "seed: matching build"       -- "${LR[@]}" seed client --repo "$C" --commit "$A" --branch voice-menu --by seed --compare "$H/buildA"
check "record names A" '[ "$(field "$SRV/www/play/DEPLOYED.json" commit)" = "$A" ]'
expect refused "exists"          "seed again refuses"         -- "${LR[@]}" seed client --repo "$C" --commit "$A" --branch voice-menu --by seed --compare "$H/buildA"

B=$(ccommit B)
expect refused "is not dare/main" "unpushed HEAD"             -- "${DC[@]}" --by t1
git -C "$C" push -q dare main
echo dirty >> "$C/crates/f.txt"
expect refused "uncommitted"     "dirty tree"                 -- "${DC[@]}" --by t1
git -C "$C" checkout -q -- crates/f.txt
touch "$C/web/new.js"
expect refused "untracked"       "untracked shipped file"     -- "${DC[@]}" --by t1
rm "$C/web/new.js"
git -C "$C" checkout -q -b my-feature
expect refused "integration branch" "non-integration branch"  -- "${DC[@]}" --by t1
git -C "$C" checkout -q main
git -C "$C" checkout -q --detach HEAD~1
expect refused "not the pushed tip" "detached at an old commit" -- "${DC[@]}" --by t1
git -C "$C" checkout -q --detach dare/main
expect ok "adds 1 commit"        "deploy detached at dare/main" -- "${DC[@]}" --by "detached"
python3 - "$SRV/www/play/DEPLOYED.json" "$A" <<'PY2'
import json,sys; p=sys.argv[1]; r=json.load(open(p)); r["commit"]=sys.argv[2]; json.dump(r,open(p,"w"))
PY2
git -C "$C" checkout -q main
expect ok "adds 1 commit"        "deploy B over A"            -- "${DC[@]}" --by "session one"
check "record = B, previous = A, by" '[ "$(field "$SRV/www/play/DEPLOYED.json" commit)" = "$B" ] && [ "$(field "$SRV/www/play/DEPLOYED.json" previous_commit)" = "$A" ] && [ "$(field "$SRV/www/play/DEPLOYED.json" by)" = "session one" ]'
check "files are B's build" 'grep -q "$B" "$SRV/www/play/pkg/oss_client_bg.wasm"'
check "extra server file kept" '[ -f "$SRV/www/play/extra.txt" ]'
check "backup holds A's build" 'grep -q "$A" "$SRV"/root/play-backup-*/pkg/oss_client_bg.wasm'
check "lock released" '[ ! -s "$SRV/lock/deploy.lock.who" ]'

# another session's branch went live (C2 on top of B); this checkout's live is reset to before it
C2=$(ccommit C2); git -C "$C" push -q dare main
expect ok "" "deploy C2"                                      -- "${DC[@]}" --by "session two"
git -C "$C" reset -q --hard "$B"; D=$(ccommit D); git -C "$C" push -q -f dare main
expect refused "DROP"            "rollback refused"           -- "${DC[@]}" --by "session one"
expect refused "C2"              "refusal lists dropped commit" -- "${DC[@]}" --by "session one"
check "still C2 live" '[ "$(field "$SRV/www/play/DEPLOYED.json" commit)" = "$C2" ]'
expect ok "rolling them back"    "--force-rollback overrides" -- "${DC[@]}" --by "session one" --force-rollback
check "record = D" '[ "$(field "$SRV/www/play/DEPLOYED.json" commit)" = "$D" ]'

# live commit the repo has never seen
python3 - "$SRV/www/play/DEPLOYED.json" <<'PY'
import json,sys; p=sys.argv[1]; r=json.load(open(p)); r["commit"]="1"*40; json.dump(r,open(p,"w"))
PY
expect refused "not in this repository" "unknown live commit" -- "${DC[@]}" --by t1

# lock: a deploy holds it; another one is refused and told who
(
  # shellcheck source=deploy/live-guard.sh
  . "$REPO/deploy/live-guard.sh"; GUARD_BY="long deploy" GUARD_BRANCH=main GUARD_COMMIT=$D
  guard_lock "client test hold" >/dev/null; touch "$H/held"; sleep 4
) &
holder=$!
for _ in $(seq 50); do [ -e "$H/held" ] && break; sleep 0.1; done
expect refused "long deploy"     "busy lock names holder"     -- "${DC[@]}" --by t1 --force-rollback
expect refused "no deploy holds" "install-public-site needs the lock" -- \
  env SITE="$SRV/www" DEPLOY_LOCK_FILE="$H/nobody.lock" bash "$REPO/deploy/install-public-site.sh" "$REPO/site"
wait $holder
expect ok "" "lock free again after holder exits"            -- "${DC[@]}" --by t1 --force-rollback

# ---- site repo: origin bare + checkout on main, with this repo's site/ and deploy/
echo "## site"
git init -q --bare "$H/site.git"
git -C "$H/site.git" config uploadpack.allowAnySHA1InWant true
git init -q -b main "$H/site"; S=$H/site
git -C "$S" remote add origin "$H/site.git"
cp -R "$REPO/site" "$REPO/deploy" "$S/"
scommit() { echo "/* $1 */" >> "$S/site/style.css"; git -C "$S" add -A; git -C "$S" commit -qm "$1"; git -C "$S" rev-parse HEAD; }
git -C "$S" add -A; git -C "$S" commit -qm base
S1=$(scommit S1); git -C "$S" push -q origin main
(cd "$S" && git archive "$S1" site | tar -x -C "$SRV/www" --strip-components=1)
PUB=(env SITE_SOURCE_URL="$H/site.git" "$S/deploy/publish-site-from-mac.sh")
expect ok "all files match"      "seed site"                  -- "$S/deploy/live-record.sh" seed site --commit "$S1" --branch main --by seed
S2=$(scommit S2); git -C "$S" push -q origin main
expect ok "adds 1 commit"        "publish site S2"            -- "${PUB[@]}" --by "site one" "$S2"
check "site record = S2, parts site" '[ "$(field "$SRV/www/DEPLOYED.json" commit)" = "$S2" ] && grep -q S2 "$SRV/www/style.css"'
expect refused "DROP"            "publish older S1 refused"   -- "${PUB[@]}" --by "site one" "$S1"
git -C "$S" checkout -q -b side; S3=$(scommit S3); git -C "$S" push -q origin side
expect refused "not on origin's integration branch" "commit off the integration branch" -- "${PUB[@]}" --by x "$S3"
git -C "$S" checkout -q main
# deploy.sh refuses before it builds anything
git -C "$S" checkout -q side
expect refused "integration branch" "deploy.sh on a side branch" -- "$S/deploy/deploy.sh" --by x
git -C "$S" checkout -q main; git -C "$S" reset -q --hard "$S1"; git -C "$S" push -q -f origin main
expect refused "DROP"            "deploy.sh rollback refused before build" -- "$S/deploy/deploy.sh" --by x --no-restart
expect refused "say who deploys" "deploy.sh needs --by"       -- "$S/deploy/deploy.sh" --no-restart
expect ok "lock free"            "live-record show"           -- "$S/deploy/live-record.sh" show

echo
if [ $fail = 0 ]; then echo "all $n checks passed"; else echo "SOME CHECKS FAILED"; fi
exit $fail
