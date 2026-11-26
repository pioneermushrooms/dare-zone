# Deploying to the Foundation zone

## Deploying safely

Deploy only through these scripts, never by copying files to the server by hand:

- `deploy/deploy.sh --by <label>`: plugin + site + zone config.
- `deploy/publish-site-from-mac.sh --by <label> <full sha>`: static site only.
- `deploy/deploy-client.sh --by <label> --client <client checkout>`: the browser game at `/play/`.
- `deploy/live-record.sh`: shows what is live.

The server records what is live in `/var/www/foundation/DEPLOYED.json` (site) and
`/var/www/foundation/play/DEPLOYED.json` (client): repo, branch, commit, UTC time and `--by`. Both
files are public, so `--by` is a plain session or branch label. A deploy is refused unless its commit
contains the live commit. If it is refused, merge the live commit into your branch and deploy again.
`--force-rollback` overrides the check, after listing the live commits it would drop. Use it only
when dropping them is the point.

Tests: `deploy/test-live-guard.sh`.
