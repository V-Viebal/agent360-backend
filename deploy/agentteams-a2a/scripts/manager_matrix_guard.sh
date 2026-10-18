#!/bin/bash
# Manager Matrix token guard (run from registry-sync.sh every 5 min on the AgentTeams node).
# The controller injects AGENTTEAMS_MANAGER_MATRIX_TOKEN into the manager container once; if Tuwunel revokes it the
# manager's Matrix channel silently stops (seen 2026-10-11 .. 2026-10-17, M_UNKNOWN_TOKEN). This guard:
#   1. calls /whoami with the token the manager actually uses (openclaw.json in the manager workspace);
#   2. on 401 logs ALERT and (MANAGER_MATRIX_AUTOHEAL=1, default) writes a valid token into openclaw.json: the
#      controller-injected env token when it is still valid, else one minted through the appservice login.
#      The manager's openclaw.json watcher re-bridges the change without a container restart.
# No token value is ever printed.
set -euo pipefail
CTRL=${AGENTTEAMS_CONTROLLER:-$(docker ps --format '{{.Names}}' | grep -m1 '^agentteams-controller-')}
MGR=${AGENTTEAMS_MANAGER_CONTAINER:-agentteams-manager}
CFG=${MANAGER_OPENCLAW_JSON:-/data/agentteams/agentteams-core/workspace/openclaw.json}
STATE=/opt/agent360-a2a/state; mkdir -p "$STATE"
LOG="$STATE/manager-matrix.status"
whoami() { # $1 = token; prints HTTP status from the homeserver inside the controller
  docker exec -i "$CTRL" python3 -c '
import sys,urllib.request
t=sys.stdin.read().strip()
r=urllib.request.Request("http://127.0.0.1:6167/_matrix/client/v3/account/whoami",headers={"Authorization":"Bearer "+t})
try: print(urllib.request.urlopen(r,timeout=10).status)
except urllib.error.HTTPError as e: print(e.code)
except Exception: print(0)' <<<"$1"
}
cfg_token() { python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["channels"]["matrix"]["accessToken"])' "$CFG"; }
write_cfg_token() { # stdin = token. The manager watcher re-bridges openclaw.json into QwenPaw on change.
  cp -p "$CFG" "$CFG.bak-guard"
  python3 -c '
import json,sys
p=sys.argv[1]; t=sys.stdin.read().strip(); d=json.load(open(p))
d["channels"]["matrix"]["accessToken"]=t; json.dump(d,open(p,"w"),indent=2)' "$CFG"
}
code=$(whoami "$(cfg_token)")
echo "$(date -u +%FT%TZ) manager_matrix_whoami=$code" > "$LOG"
[ "$code" = 200 ] && exit 0
echo "$(date -u +%FT%TZ) ALERT manager Matrix token in openclaw.json rejected (whoami=$code)"
[ "${MANAGER_MATRIX_AUTOHEAL:-1}" = 1 ] || exit 2
[ "$code" = 401 ] || exit 2   # only heal a revoked token, not a homeserver outage
# 1) The controller-injected env token is often still valid when the file was rolled back: reuse it.
envtok=$(docker exec "$MGR" printenv AGENTTEAMS_MANAGER_MATRIX_TOKEN || true)
if [ -n "$envtok" ] && [ "$(whoami "$envtok")" = 200 ]; then
  write_cfg_token <<<"$envtok"
  echo "$(date -u +%FT%TZ) HEALED openclaw.json token replaced with the valid controller-injected token (watcher re-bridges)"
  echo "$(date -u +%FT%TZ) manager_matrix_whoami=healed-env" > "$LOG"; exit 0
fi
# 2) Otherwise mint a new token through the controller's appservice login.
docker exec "$CTRL" python3 -c '
import os,json,urllib.request
r=urllib.request.Request("http://127.0.0.1:6167/_matrix/client/v3/login",
  data=json.dumps({"type":"m.login.application_service","identifier":{"type":"m.id.user","user":"manager"},
                   "initial_device_display_name":"agentteams-manager (guard)"}).encode(),
  headers={"Content-Type":"application/json","Authorization":"Bearer "+os.environ["AGENTTEAMS_MATRIX_APPSERVICE_AS_TOKEN"]})
print(json.load(urllib.request.urlopen(r,timeout=15))["access_token"])' | write_cfg_token
echo "$(date -u +%FT%TZ) HEALED openclaw.json token re-issued via appservice login (watcher re-bridges)"
echo "$(date -u +%FT%TZ) manager_matrix_whoami=healed-minted" > "$LOG"
