#!/bin/bash
# Provision an AgentTeams Matrix identity for an EXTERNAL (edge) QwenPaw agent or the Agent360 control plane.
# AgentTeams reserves the whole user namespace for its appservice, so identities MUST be created through the
# controller (POST /api/v1/humans) — direct /register returns M_EXCLUSIVE.
# Usage: AGENTTEAMS_CONTAINER=... provision-edge-identity.sh <name> [out_dir]
#   name: lowercase localpart, e.g. gm-default (agent://gm/default) or agent360-a2a (control plane)
# Output: <out_dir>/<name>.matrix.json (0600) {user_id, access_token, device_id, homeserver}. Never printed.
set -euo pipefail
C=${AGENTTEAMS_CONTAINER:?}; N=${1:?name}; OUT=${2:-/opt/agent360-a2a}
HS=${A2A_HOMESERVER:-https://agentteams-gateway.hmz.one}
mkdir -p "$OUT"; chmod 700 "$OUT"; umask 077
docker exec -e N="$N" "$C" sh -c 'T=$(cat /var/run/agentteams/cli-token); curl -s -m15 -H "Authorization: Bearer $T" -H Content-Type:application/json \
  -X POST http://127.0.0.1:8090/api/v1/humans -d "{\"name\":\"$N\",\"displayName\":\"$N\",\"permissionLevel\":1,\"note\":\"agent360 external A2A identity\"}" >/dev/null || true'
for i in $(seq 1 20); do
  docker exec "$C" sh -c 'T=$(cat /var/run/agentteams/cli-token); curl -s -m10 -H "Authorization: Bearer $T" http://127.0.0.1:8090/api/v1/humans' > "$OUT/.humans.json"
  python3 -c "import json,sys;h={x['name']:x for x in json.load(open('$OUT/.humans.json'))['humans']};sys.exit(0 if h.get('$N',{}).get('matrixUserID') else 1)" && break
  sleep 5
done
python3 - "$N" "$OUT" "$HS" <<'PY'
import json,sys,urllib.request as R
n,out,hs=sys.argv[1:4]
h={x["name"]:x for x in json.load(open(f"{out}/.humans.json"))["humans"]}[n]
if not h.get("matrixUserID"): sys.exit(f"{n}: matrixUserID missing -> run repair-appservice.sh")
q=R.Request(f"{hs}/_matrix/client/v3/login",data=json.dumps({"type":"m.login.password","identifier":{"type":"m.id.user","user":n},
  "password":h["initialPassword"],"device_id":"A2A-"+n.upper(),"initial_device_display_name":"agent360-a2a "+n}).encode(),
  headers={"Content-Type":"application/json","User-Agent":"agent360-a2a/1.0"})
r=json.load(R.urlopen(q,timeout=20))
json.dump({"user_id":r["user_id"],"access_token":r["access_token"],"device_id":r.get("device_id"),"homeserver":hs},open(f"{out}/{n}.matrix.json","w"))
print(n,"->",r["user_id"])
PY
chmod 600 "$OUT/$N.matrix.json"; rm -f "$OUT/.humans.json"
