#!/bin/bash
# Run on the AgentTeams node. Exports a secret-free directory snapshot (workers + A2A
# service identities) consumed by the Agent360 A2A control plane as A2A_STATIC_AGENTS.
set -euo pipefail
C=${AGENTTEAMS_CONTAINER:-agentteams-controller-ew32vzaefuo4n75renbpbsj6}
OUT=${1:-/opt/agent360-a2a/registry.json}
docker exec "$C" sh -c 'T=$(cat /var/run/agentteams/cli-token); for p in workers humans; do curl -s -m10 -H "Authorization: Bearer $T" http://127.0.0.1:8090/api/v1/$p; echo; done' \
| python3 -c '
import sys,json,re
ls=[l for l in sys.stdin.read().splitlines() if l.strip()]; w=json.loads(ls[0]); h=json.loads(ls[1])
import os; ep=os.environ.get("A2A_EDGE_AGENTS","/opt/agent360-a2a/edge-agents.json"); edge=json.load(open(ep)) if os.path.exists(ep) else []
out=[]
for x in w.get("workers",[]):
  m=re.match(r"qwenpaw://([^/]+)/(.+)",x.get("identity") or ""); inst,name=(m.group(1),m.group(2)) if m else ("core",x["name"])
  out.append({"agent_ref":f"agent://{inst}/{name}","id":f"ext_{inst}_{name}","display_name":x.get("workerName") or name,
   "runtime":x.get("runtime","qwenpaw"),"instance_id":f"qwenpaw-{inst}","node":None,"agentteams_worker_id":x["name"],
   "matrix_user_id":x.get("matrixUserID"),"endpoint_ref":x.get("identity"),"capabilities":[],"source":"agentteams:worker"})
hm={x["name"]:x.get("matrixUserID") for x in h.get("humans",[])}
for e in edge:
  e=dict(e); e["matrix_user_id"]=hm.get(e["agentteams_worker_id"]); e["source"]="agentteams:edge"; out.append(e)
print(json.dumps(out))' > "$OUT.tmp"
mv "$OUT.tmp" "$OUT"; chmod 644 "$OUT"
python3 -c "import json;print(\"exported\",len(json.load(open(\"$OUT\"))),\"agents\")"
