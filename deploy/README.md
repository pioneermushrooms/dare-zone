# Deploying to the Foundation zone

## Deploying safely

Many sessions work at once; whoever deploys last must never roll back anyone else. So:

1. **Work on your own branch** in your own worktree. Never commit into another session's branch or
   worktree; message that session instead.
2. **Ship by merging.** Merge the integration branch into yours, test, then merge yours into the
   integration branch and push. Integration branch: `main`, in this repo and in the browser
   client (dare-client). `custom-graphics` is retired: merge into `main` instead.
3. **Deploy only from the pushed tip of the integration branch**, from a deploy checkout:
   ```bash
   git -C ~/dare-zone/.claude/worktrees/deploy fetch origin && git -C ~/dare-zone/.claude/worktrees/deploy switch --detach origin/main
   git -C ~/oss-src/deploy fetch dare && git -C ~/oss-src/deploy switch --detach dare/main
   ```
   then run one of:
   - `deploy/deploy.sh --by <label>`: plugin + site + zone config.
   - `deploy/publish-site-from-mac.sh --by <label> <full sha>`: static site only.
   - `deploy/deploy-client.sh --by <label>`: the browser game at `/play/` (builds `~/oss-src/deploy`).
   - `deploy/live-record.sh`: shows what is live and who holds the deploy lock.
4. **Never copy files to the server by hand**, and ask Chris before every deploy.

What the scripts enforce:

- the checkout is clean and is exactly the pushed tip of the integration branch;
- the commit contains what is live, read from `/var/www/foundation/DEPLOYED.json` (site) or
  `/var/www/foundation/play/DEPLOYED.json` (client). If refused, merge the live commit and push.
  `--force-rollback` overrides after listing the commits it would drop; only when Chris says so;
- one deploy at a time (`flock` on `/var/lock/foundation-deploy.lock`; a refused deploy is told
  who holds it). The server-side installers refuse to run outside a deploy;
- each deploy writes the new record: repo, branch, commit, UTC time and `--by` (public: use a
  session or branch label).

Tests: `deploy/test-live-guard.sh`.
