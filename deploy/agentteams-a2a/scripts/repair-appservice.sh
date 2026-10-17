#!/bin/bash
# Repair AgentTeams v1.2.3 Matrix appservice ("Matrix AppService token not active yet").
# Root cause seen 2026-10-17: Tuwunel @admin password drifted from AGENTTEAMS_ADMIN_PASSWORD, so the
# controller could not run `appservices register` -> every worker/human stayed matrixUserID="".
# Run on the AgentTeams node. Requires: scripts/patch_start_tuwunel.py already applied (idempotent).
set -euo pipefail
C=${AGENTTEAMS_CONTAINER:?set AGENTTEAMS_CONTAINER}
HERE=$(cd "$(dirname "$0")" && pwd)
docker cp "$HERE/patch_start_tuwunel.py" "$C:/tmp/patch_start_tuwunel.py"
docker exec "$C" python3 /tmp/patch_start_tuwunel.py >/dev/null
docker exec "$C" bash -n /opt/agentteams/scripts/init/start-tuwunel.sh
# 1) one-shot admin password reconcile: flag + SIGKILL (SIGTERM does not release the RocksDB LOCK in time);
#    supervisord restarts tuwunel which executes the admin command once and deletes the flag.
docker exec "$C" sh -c 'touch /data/.a2a-reset-admin; kill -9 $(pgrep -x tuwunel | head -1)
  for i in $(seq 1 60); do sleep 1; curl -s -m2 http://127.0.0.1:6167/_matrix/client/versions >/dev/null && break; done
  test ! -f /data/.a2a-reset-admin && echo "tuwunel restarted, flag consumed"'
# 2) re-register the appservice with the controller's current AS token
docker exec "$C" sh -c 'T=$(cat /var/run/agentteams/cli-token); curl -s -m120 -H "Authorization: Bearer $T" -H Content-Type:application/json \
  -X POST http://127.0.0.1:8090/api/v1/appservice/rotate-token \
  -d "{\"as_token\":\"$AGENTTEAMS_MATRIX_APPSERVICE_AS_TOKEN\",\"force\":true}" | sed -E "s/[A-Za-z0-9_-]{32,}/<redacted>/g"; echo'
# 3) verify: workers/humans get matrixUserID within ~30s
sleep 30
docker exec "$C" sh -c 'T=$(cat /var/run/agentteams/cli-token); for p in workers humans; do curl -s -m10 -H "Authorization: Bearer $T" http://127.0.0.1:8090/api/v1/$p; echo; done' \
 | python3 -c 'import sys,json
for l in sys.stdin:
  l=l.strip()
  if not l: continue
  d=json.loads(l)
  for x in d.get("workers",d.get("humans",[])): print(x["name"], x.get("phase"), x.get("matrixUserID") or "MISSING")'
