# shellcheck shell=bash
# Live record and rollback guard for the Foundation server. Sourced (not run) by deploy/deploy.sh,
# deploy/publish-site-from-mac.sh, deploy/deploy-client.sh and deploy/live-record.sh.
#
# The live record is a JSON file on the server beside what it describes:
#   site + plugin   $DEPLOY_SITE_DIR/DEPLOYED.json        (/var/www/foundation/DEPLOYED.json)
#   browser client  $DEPLOY_PLAY_DIR/DEPLOYED.json        (/var/www/foundation/play/DEPLOYED.json)
# Both are public (Caddy serves the folder), so --by is public too: a session or branch label.
# A deploy is refused unless its commit contains the live commit
# (git merge-base --is-ancestor live new); --force-rollback overrides after listing what is dropped.
#
# Test seams (production defaults): DEPLOY_HOST, DEPLOY_SSH (command used as "$DEPLOY_SSH host cmd"),
# DEPLOY_SITE_DIR, DEPLOY_PLAY_DIR, DEPLOY_BACKUP_ROOT.

# shellcheck disable=SC2034  # used by the scripts that source this
GUARD_HOST=${DEPLOY_HOST:-root@5.161.238.245}
GUARD_SSH=${DEPLOY_SSH:-ssh}
GUARD_SITE_DIR=${DEPLOY_SITE_DIR:-/var/www/foundation}
GUARD_PLAY_DIR=${DEPLOY_PLAY_DIR:-$GUARD_SITE_DIR/play}
GUARD_BACKUP_ROOT=${DEPLOY_BACKUP_ROOT:-/root}
GUARD_BY=""
GUARD_FORCE_ROLLBACK=0

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

# guard_check_tree <repo> <remote> <shipped paths...>: the checkout must be clean (tracked files;
#   untracked files only under the shipped paths), since its HEAD is what the record names.
#   Sets GUARD_REPO_DIR GUARD_BRANCH GUARD_COMMIT GUARD_REMOTE GUARD_REPO_URL.
guard_check_tree() {
  local repo=$1 remote=$2 dirty; shift 2
  GUARD_REPO_DIR=$repo GUARD_REMOTE=$remote
  GUARD_BRANCH=$(git -C "$repo" symbolic-ref --short -q HEAD) || GUARD_BRANCH="(detached)"
  dirty=$(git -C "$repo" status --porcelain --untracked-files=no)
  [ -z "$dirty" ] || guard_die "uncommitted changes in $repo:
$dirty"
  dirty=$(git -C "$repo" ls-files --others --exclude-standard -- "$@")
  [ -z "$dirty" ] || guard_die "untracked files in shipped folders of $repo:
$dirty"
  GUARD_COMMIT=$(git -C "$repo" rev-parse HEAD)
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
  local record=$1 live repo=$GUARD_REPO_DIR desc
  guard_read_record "$record"
  if [ -z "$GUARD_LIVE_JSON" ]; then
    if [ "$GUARD_FORCE_ROLLBACK" = 1 ]; then
      echo "WARNING: no live record at $GUARD_HOST:$record; --force-rollback given, deploying without the check" >&2
      return 0
    fi
    guard_die "no live record at $GUARD_HOST:$record, so nothing proves this deploy keeps what is live.
  Seed it (deploy/live-record.sh seed ...) after checking what is live, or pass --force-rollback."
  fi
  live=$(guard_field commit)
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
  It was deployed from work you do not have. Get it pushed, merge it into your branch,
  then deploy. Do not deploy over it."
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
  guard_die "the live build has commits this one does not. Merge $live into your branch and
  deploy that, or pass --force-rollback if dropping them is intended."
}

# guard_write_record <record path> <parts, e.g. "site" or "plugin site" or "client">
#   Writes the new live record. A site-only deploy carries the plugin part of the old record over.
guard_write_record() {
  local record=$1 parts=$2 json
  json=$(GUARD_LIVE_JSON=$GUARD_LIVE_JSON python3 - "$GUARD_REPO_URL" "$GUARD_BRANCH" "$GUARD_COMMIT" \
      "$GUARD_BY" "$parts" "$(git -C "$GUARD_REPO_DIR" log -1 --format=%s "$GUARD_COMMIT")" <<'PY'
import json, os, sys, datetime
repo, branch, commit, by, parts, subject = sys.argv[1:7]
old = json.loads(os.environ["GUARD_LIVE_JSON"]) if os.environ.get("GUARD_LIVE_JSON") else {}
now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
rec = {"repo": repo, "branch": branch, "commit": commit, "subject": subject,
       "deployed_at": now, "by": by, "parts": parts.split(),
       "previous_commit": old.get("commit")}
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
