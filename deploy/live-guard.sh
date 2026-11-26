# shellcheck shell=bash
# Rollback guard, deploy lock and live record for the Foundation server. Sourced (not run) by
# deploy/deploy.sh, deploy/publish-site-from-mac.sh, deploy/deploy-client.sh and
# deploy/live-record.sh, on the deploying machine. See deploy/README.md, "Deploying safely".
#
# The live record is a JSON file on the server beside what it describes:
#   site + plugin   $DEPLOY_SITE_DIR/DEPLOYED.json        (/var/www/foundation/DEPLOYED.json)
#   browser client  $DEPLOY_PLAY_DIR/DEPLOYED.json        (/var/www/foundation/play/DEPLOYED.json)
# Both are public (Caddy serves the folder), so --by is public too: a session or branch label.
#
# The guard refuses a deploy whose commit does not contain the live commit
# (git merge-base --is-ancestor live new); --force-rollback overrides and lists what is dropped.
# Each record also lists the sha256 of every file the deploy shipped ("files"); a deploy is refused
# when the server's files no longer match it, i.e. someone changed them by hand since (the record
# no longer says what is live). --force-rollback overrides that too, after listing the files.
# The lock is flock on $DEPLOY_LOCK_FILE, held by one ssh connection for the whole deploy and
# released when this script exits (or its connection drops). $DEPLOY_LOCK_FILE.who names the holder.
#
# Test seams (production defaults): DEPLOY_HOST, DEPLOY_SSH (command used as "$DEPLOY_SSH host cmd"),
# DEPLOY_SITE_DIR, DEPLOY_PLAY_DIR, DEPLOY_LOCK_FILE, DEPLOY_BACKUP_ROOT, DEPLOY_LOCK_WAIT.

# shellcheck disable=SC2034  # used by the scripts that source this
GUARD_HOST=${DEPLOY_HOST:-root@5.161.238.245}
GUARD_SSH=${DEPLOY_SSH:-ssh}
GUARD_SITE_DIR=${DEPLOY_SITE_DIR:-/var/www/foundation}
GUARD_PLAY_DIR=${DEPLOY_PLAY_DIR:-$GUARD_SITE_DIR/play}
GUARD_LOCK_FILE=${DEPLOY_LOCK_FILE:-/var/lock/foundation-deploy.lock}
GUARD_BACKUP_ROOT=${DEPLOY_BACKUP_ROOT:-/root}
GUARD_BY=""
GUARD_FORCE_ROLLBACK=0
GUARD_SHIPPED_DIR=""   # set by the deploy: a local folder whose files (relative paths) the deploy ships

guard_die() { echo "DEPLOY REFUSED: $*" >&2; exit 1; }

# guard_remote <program> [args...]: runs one program on the server, every argument quoted.
guard_remote() {
  local cmd
  cmd=$(printf '%q ' "$@")
  # shellcheck disable=SC2086,SC2029  # GUARD_SSH may carry options; cmd is quoted locally on purpose
  $GUARD_SSH "$GUARD_HOST" "$cmd"
}

# guard_need_by: --by is required on every deploy.
guard_need_by() {
  [ -n "$GUARD_BY" ] || guard_die "say who deploys: --by '<session or branch label>' (it is published in DEPLOYED.json)"
  case "$GUARD_BY" in *[[:cntrl:]]*) guard_die "--by must be one line of plain text" ;; esac
}

# guard_check_tree <repo> <remote> <shipped paths...>
#   The checkout must be clean (tracked files; untracked files only under the shipped paths) and
#   exactly the pushed tip of <remote>/<integration branch> ($GUARD_BRANCHES, space separated), so
#   every other session can fetch what goes live. It may be on that branch or detached at its tip
#   (git switch --detach <remote>/<branch>), which works while the branch is checked out elsewhere.
#   Sets GUARD_REPO_DIR GUARD_BRANCH GUARD_COMMIT GUARD_REMOTE GUARD_REPO_URL.
guard_check_tree() {
  local repo=$1 remote=$2; shift 2
  GUARD_REPO_DIR=$repo GUARD_REMOTE=$remote
  # GUARD_BRANCHES (integration branches, space separated) is set by the calling script
  # shellcheck disable=SC2153
  local b
  GUARD_BRANCH=$(git -C "$repo" symbolic-ref --short -q HEAD) || {
    for b in $GUARD_BRANCHES; do
      git -C "$repo" fetch -q "$remote" "$b" 2>/dev/null || continue
      if [ "$(git -C "$repo" rev-parse "refs/remotes/$remote/$b")" = "$(git -C "$repo" rev-parse HEAD)" ]; then
        GUARD_BRANCH=$b; break
      fi
    done
    [ -n "$GUARD_BRANCH" ] || guard_die "$repo is detached at a commit that is not the pushed tip of $GUARD_BRANCHES.
  Run: git -C $repo fetch $remote && git -C $repo switch --detach $remote/<branch>"
  }
  case " $GUARD_BRANCHES " in
    *" $GUARD_BRANCH "*) ;;
    *) guard_die "$repo is on '$GUARD_BRANCH'. Deploy only from an integration branch ($GUARD_BRANCHES):
  merge your branch into it, push it, and deploy from a checkout of it." ;;
  esac
  local dirty
  dirty=$(git -C "$repo" status --porcelain --untracked-files=no)
  [ -z "$dirty" ] || guard_die "uncommitted changes in $repo:
$dirty"
  dirty=$(git -C "$repo" ls-files --others --exclude-standard -- "$@")
  [ -z "$dirty" ] || guard_die "untracked files in shipped folders of $repo:
$dirty"
  git -C "$repo" fetch -q "$remote" "$GUARD_BRANCH" || guard_die "git fetch $remote $GUARD_BRANCH failed"
  GUARD_COMMIT=$(git -C "$repo" rev-parse HEAD)
  local pushed
  pushed=$(git -C "$repo" rev-parse "refs/remotes/$remote/$GUARD_BRANCH" 2>/dev/null) ||
    guard_die "$remote/$GUARD_BRANCH does not exist; push the integration branch first"
  [ "$pushed" = "$GUARD_COMMIT" ] || guard_die "HEAD ($GUARD_COMMIT) is not $remote/$GUARD_BRANCH ($pushed).
  Pull (merge) $remote/$GUARD_BRANCH and push, so what goes live is what everyone can fetch."
  GUARD_REPO_URL=$(git -C "$repo" remote get-url "$remote")
}

# guard_use_commit <repo> <remote> <full sha>: like guard_check_tree for a deploy that ships a
#   commit from the remote rather than the checkout: the commit must be on the pushed tip of an
#   integration branch. Sets the same variables.
guard_use_commit() {
  local repo=$1 remote=$2 sha=$3 b
  GUARD_REPO_DIR=$repo GUARD_REMOTE=$remote GUARD_COMMIT=$sha GUARD_BRANCH=""
  for b in $GUARD_BRANCHES; do
    git -C "$repo" fetch -q "$remote" "$b" 2>/dev/null || continue
    if git -C "$repo" merge-base --is-ancestor "$sha" "refs/remotes/$remote/$b" 2>/dev/null; then
      GUARD_BRANCH=$b; break
    fi
  done
  [ -n "$GUARD_BRANCH" ] || guard_die "$sha is not on $remote's integration branch ($GUARD_BRANCHES).
  Merge it into one, push, and deploy that."
  GUARD_REPO_URL=$(git -C "$repo" remote get-url "$remote")
}

# guard_read_record <record path on server>: sets GUARD_LIVE_JSON (empty when there is none).
guard_read_record() {
  local out
  # shellcheck disable=SC2016  # runs on the server
  out=$(guard_remote bash -c 'if [ -f "$1" ]; then cat "$1"; else echo NO-RECORD; fi' _ "$1") ||
    guard_die "could not read $1 on $GUARD_HOST"
  if [ "$out" = NO-RECORD ]; then GUARD_LIVE_JSON=""; else GUARD_LIVE_JSON=$out; fi
}

# guard_field <name>: one top-level field of GUARD_LIVE_JSON ("" when absent).
guard_field() {
  [ -n "$GUARD_LIVE_JSON" ] || return 0
  printf '%s' "$GUARD_LIVE_JSON" | python3 -c 'import json,sys; v=json.load(sys.stdin).get(sys.argv[1]); print("" if v is None else v)' "$1" ||
    guard_die "the live record is not valid JSON"
}

# guard_check_ancestry <record path>: refuses unless GUARD_COMMIT contains the live commit.
guard_check_ancestry() {
  local record=$1 live repo=$GUARD_REPO_DIR
  guard_read_record "$record"
  if [ -z "$GUARD_LIVE_JSON" ]; then
    if [ "$GUARD_FORCE_ROLLBACK" = 1 ]; then
      echo "WARNING: no live record at $GUARD_HOST:$record; --force-rollback given, deploying without the check" >&2
      return 0
    fi
    guard_die "no live record at $GUARD_HOST:$record, so nothing proves this deploy keeps what is live.
  Seed it (deploy/live-record.sh seed ...) after checking what is live, or pass --force-rollback."
  fi
  guard_check_files "$record"
  live=$(guard_field commit)
  local desc
  desc="$live (branch $(guard_field branch), by $(guard_field by), $(guard_field deployed_at))"
  echo "live:      $desc"
  echo "deploying: $GUARD_COMMIT ($GUARD_BRANCH)"
  if ! git -C "$repo" cat-file -e "$live^{commit}" 2>/dev/null; then
    git -C "$repo" fetch -q "$GUARD_REMOTE" "$live" 2>/dev/null || true
  fi
  if ! git -C "$repo" cat-file -e "$live^{commit}" 2>/dev/null; then
    if [ "$GUARD_FORCE_ROLLBACK" = 1 ]; then
      echo "WARNING: live commit $live is not in this repository; --force-rollback drops whatever it has" >&2
      return 0
    fi
    guard_die "the live commit $live is not in this repository (nor on $GUARD_REMOTE).
  It was deployed from work you do not have. Ask its owner to push and merge it into
  $GUARD_BRANCH, then merge that here. Do not deploy over it."
  fi
  if git -C "$repo" merge-base --is-ancestor "$live" "$GUARD_COMMIT"; then
    echo "ok: keeps everything live, adds $(git -C "$repo" rev-list --count "$live..$GUARD_COMMIT") commit(s)"
    return 0
  fi
  echo "This deploy would DROP these live commits (in $live, not in $GUARD_COMMIT):" >&2
  git -C "$repo" log --oneline --no-decorate "$GUARD_COMMIT..$live" | sed 's/^/  /' >&2
  if [ "$GUARD_FORCE_ROLLBACK" = 1 ]; then
    echo "WARNING: --force-rollback given; rolling them back" >&2
    return 0
  fi
  guard_die "the live build has commits this one does not. Merge $live (or its branch) into
  $GUARD_BRANCH and deploy that, or pass --force-rollback if dropping them is intended."
}

# guard_check_files <record path>: refuses when a file the record lists differs on the server.
guard_check_files() {
  local record=$1 dir sums out
  dir=$(dirname "$record")
  sums=$(printf '%s' "$GUARD_LIVE_JSON" | python3 -c '
import json, sys
for name, sha in sorted((json.load(sys.stdin).get("files") or {}).items()):
    print(f"{sha}  {name}")') || guard_die "the live record is not valid JSON"
  if [ -z "$sums" ]; then
    echo "note: the live record has no file list (older record), so a hand change cannot be detected"
    return 0
  fi
  # shellcheck disable=SC2016  # runs on the server
  if out=$(printf '%s\n' "$sums" | guard_remote bash -c 'cd "$1" && sha256sum -c - 2>&1 | grep -v ": OK$"; exit "${PIPESTATUS[0]}"' _ "$dir"); then
    echo "files: all $(printf '%s\n' "$sums" | wc -l | tr -d ' ') recorded files are as deployed"
    return 0
  fi
  echo "These files in $GUARD_HOST:$dir changed since the last recorded deploy (by hand?):" >&2
  printf '%s\n' "$out" | sed 's/^/  /' >&2
  if [ "$GUARD_FORCE_ROLLBACK" = 1 ]; then
    echo "WARNING: --force-rollback given; overwriting them" >&2
    return 0
  fi
  guard_die "what is live is not what the record says, so the rollback check cannot be trusted.
  Find who changed it (deploy/live-record.sh shows the record), get that work merged into main and
  deployed through these scripts, or pass --force-rollback if overwriting it is intended."
}

# guard_lock <what>: takes the server's deploy lock for the rest of this script's life.
guard_lock() {
  local what=$1 dir wait=${DEPLOY_LOCK_WAIT:-30} i=0 who
  dir=$(mktemp -d)
  GUARD_LOCK_DIR=$dir
  mkfifo "$dir/hold"
  who="$GUARD_BY | $what | $GUARD_BRANCH $GUARD_COMMIT | since $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  # The remote side holds flock while its stdin stays open; this script keeps fd 9 (the fifo's write
  # end) open until it exits, so the lock is released on exit, ^C, or a dropped connection.
  # shellcheck disable=SC2016  # runs on the server
  guard_remote bash -c '
    exec 8>>"$1" || exit 3
    if ! flock -n 8; then echo "BUSY $(cat "$1.who" 2>/dev/null || echo "(holder unknown)")"; exit 75; fi
    printf "%s\n" "$2" > "$1.who"
    echo LOCKED
    cat > /dev/null
    rm -f "$1.who"' _ "$GUARD_LOCK_FILE" "$who" < "$dir/hold" > "$dir/out" 2>&1 &
  GUARD_LOCK_PID=$!
  exec 9> "$dir/hold"
  while :; do
    if grep -q '^LOCKED' "$dir/out" 2>/dev/null; then
      echo "lock: holding $GUARD_HOST:$GUARD_LOCK_FILE"
      return 0
    fi
    if grep -q '^BUSY' "$dir/out" 2>/dev/null; then
      exec 9>&-
      guard_die "another deploy is running on $GUARD_HOST:
  $(sed -n 's/^BUSY //p' "$dir/out")
  Wait for it to finish (or ask that session), then run this again."
    fi
    if ! kill -0 "$GUARD_LOCK_PID" 2>/dev/null && ! grep -q '^LOCKED' "$dir/out"; then
      exec 9>&-
      guard_die "could not take the deploy lock on $GUARD_HOST: $(cat "$dir/out")"
    fi
    i=$((i + 1))
    [ "$i" -le $((wait * 5)) ] || { exec 9>&-; guard_die "no answer from the lock on $GUARD_HOST after ${wait}s"; }
    sleep 0.2
  done
}

# guard_unlock: releases the lock now (it is also released when the script exits).
guard_unlock() {
  [ -n "${GUARD_LOCK_PID:-}" ] || return 0
  exec 9>&-
  wait "$GUARD_LOCK_PID" 2>/dev/null || true
  rm -rf "$GUARD_LOCK_DIR"
  GUARD_LOCK_PID=""
}

# guard_write_record <record path> <parts, e.g. "site" or "plugin site" or "client">
#   Writes the new live record. A site-only deploy carries the plugin part of the old record over.
guard_write_record() {
  local record=$1 parts=$2 json sums=""
  if [ -n "$GUARD_SHIPPED_DIR" ]; then
    # shellcheck disable=SC2016  # runs on the server
    sums=$( (cd "$GUARD_SHIPPED_DIR" && find . -type f | sort) |
      guard_remote bash -c 'cd "$1" && while IFS= read -r f; do if [ -f "$f" ]; then sha256sum "$f"; fi; done' _ "$(dirname "$record")") ||
      guard_die "the deploy went out but reading its files back failed; write the record by hand (deploy/live-record.sh seed)"
  fi
  json=$(GUARD_LIVE_JSON=$GUARD_LIVE_JSON GUARD_SUMS=$sums python3 - "$GUARD_REPO_URL" "$GUARD_BRANCH" "$GUARD_COMMIT" \
      "$GUARD_BY" "$parts" "$(git -C "$GUARD_REPO_DIR" log -1 --format=%s "$GUARD_COMMIT")" <<'PY'
import json, os, sys, datetime
repo, branch, commit, by, parts, subject = sys.argv[1:7]
old = json.loads(os.environ["GUARD_LIVE_JSON"]) if os.environ.get("GUARD_LIVE_JSON") else {}
now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
rec = {"repo": repo, "branch": branch, "commit": commit, "subject": subject,
       "deployed_at": now, "by": by, "parts": parts.split(),
       "previous_commit": old.get("commit")}
files = {}
for line in os.environ.get("GUARD_SUMS", "").splitlines():
    sha, _, name = line.partition("  ")
    if sha and name:
        files[name] = sha
if files:
    rec["files"] = files
if "plugin" in rec["parts"]:
    rec["plugin"] = {"commit": commit, "deployed_at": now, "by": by}
elif "plugin" in old:
    rec["plugin"] = old["plugin"]
print(json.dumps(rec, indent=2))
PY
  ) || guard_die "could not compose the live record"
  # shellcheck disable=SC2016  # runs on the server
  printf '%s\n' "$json" | guard_remote bash -c 'cat > "$1.tmp" && chmod 644 "$1.tmp" && mv -f "$1.tmp" "$1"' _ "$record" ||
    guard_die "the deploy went out but writing $record failed; write it by hand (deploy/live-record.sh seed)"
  echo "live record: $GUARD_HOST:$record = $GUARD_COMMIT ($GUARD_BRANCH, by $GUARD_BY)"
}
