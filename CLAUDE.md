# dare-zone

Deploy to Foundation only as `deploy/README.md` ("Deploying safely") says: merge into the integration branch, push, and deploy its pushed tip with `deploy/deploy.sh`, `deploy/publish-site-from-mac.sh` or `deploy/deploy-client.sh`, never by copying files to the server by hand. If a deploy is refused, merge the live commit (`deploy/live-record.sh`) and try again.
