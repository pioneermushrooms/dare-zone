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
- nothing was changed by hand: each record lists the sha256 of every file its deploy shipped, and
  the next deploy is refused when any of them differs on the server (someone copied files by hand,
  so the record no longer says what is live). Get that work merged into main and deployed;
- one deploy at a time (`flock` on `/var/lock/foundation-deploy.lock`; a refused deploy is told
  who holds it). The server-side installers refuse to run outside a deploy;
- each deploy writes the new record: repo, branch, commit, UTC time and `--by` (public: use a
  session or branch label).

Tests: `deploy/test-live-guard.sh`.

## Shipping the in-game tutorial

The tutorial spans the zone (plugin and `arenas/#training`), the static site (`tutorial.js`,
`tutorial.css`, `tutorial-mentor.png`, `index.html`) and the browser client (its `?tutorialclient`
hints). Ship **the zone first, or together with the client and site**: a client or page that
arrives before the zone sends `?tutorialclient` to a zone that doesn't know it, and the page's
mentor box waits for hints that never come. `site/index.html` bumps the `start.mjs?v=` cache
buster with each client change, so browsers fetch the new client once it is deployed.

## Zone settings: the tutorial and bots

`[Tutorial]` in the zone's global config (`?tutorialstats` shows how many finished, left and are running):

| Setting | Default | Meaning |
| --- | --- | --- |
| `Enabled` | 1 | Newcomers are taken into the tutorial, and `?tutorial` works. |
| `FullCasts` | 10 | Tutorials running at once with the full bot cast (target, 5-bot swarm, teammate); each one beyond gets one bot that plays every part. |
| `MaxTutorials` | 30 | Most tutorials at once; beyond it a newcomer is only offered the tutorial. |

Each tutorial runs its own bot host (`oss-bot`). Measured 2026-11-28 on a local zone with a
release `oss-bot` and a full cast of 7 bots during the swarm: about **78 MB resident and 1% CPU
per full-cast host** (28 MB before the bots connect), so 10 full casts are about 780 MB on top of
the zone. Size `FullCasts` and `MaxTutorials` to the server's memory.

Tutorial bots are **separate from `[DareArenas] MaxBots`** (the zone-wide cap on arena owners'
bots, default 30): they are not counted against it, and `MaxBots = 0` does not stop them. Use
`[Tutorial] Enabled = 0` or `FullCasts`/`MaxTutorials` for those.
