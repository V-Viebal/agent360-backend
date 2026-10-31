#!/bin/bash
# Run on the AgentTeams node. Exports a secret-free directory snapshot (workers + A2A
# service identities) consumed by the Agent360 A2A control plane as A2A_STATIC_AGENTS.
set -euo pipefail
C=${AGENTTEAMS_CONTAINER:-agentteams-controller-ew32vzaefuo4n75renbpbsj6}
OUT=${1:-/opt/agent360-a2a/registry.json}
docker exec "$C" sh -c 'T=$(cat /var/run/agentteams/cli-token); for p in workers humans; do curl -s -m10 -H "Authorization: Bearer $T" http://127.0.0.1:8090/api/v1/$p; echo; done' \
| python3 -c '
import sys,json,re,os
from datetime import datetime, timezone
ls=[l for l in sys.stdin.read().splitlines() if l.strip()]; w=json.loads(ls[0]); h=json.loads(ls[1])
ep=os.environ.get("A2A_EDGE_AGENTS","/opt/agent360-a2a/edge-agents.json"); edge=json.load(open(ep)) if os.path.exists(ep) else []
now_iso=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
out=[]
for x in w.get("workers",[]):
  m=re.match(r"qwenpaw://([^/]+)/(.+)",x.get("identity") or ""); inst,name=(m.group(1),m.group(2)) if m else ("core",x["name"])
  is_running=str(x.get("state") or "").lower()=="running" and bool(x.get("matrixUserID"))
  out.append({"agent_ref":f"agent://{inst}/{name}","id":f"ext_{inst}_{name}","display_name":x.get("workerName") or name,
   "runtime":x.get("runtime","qwenpaw"),"instance_id":f"qwenpaw-{inst}","node":"3rd","agentteams_worker_id":x["name"],
   "matrix_user_id":x.get("matrixUserID"),"endpoint_ref":x.get("identity"),"capabilities":["core","a2a","kb","s3"],
   "health":"online" if is_running else "unknown","last_seen_at":now_iso if is_running else None,"source":"agentteams:worker"})
hm={x["name"]:x.get("matrixUserID") for x in h.get("humans",[])}
hphase={x["name"]:x.get("phase") for x in h.get("humans",[])}
for e in edge:
  e=dict(e); wid=e["agentteams_worker_id"]; e["matrix_user_id"]=hm.get(wid)
  is_active=str(hphase.get(wid) or "").lower()=="active" and bool(e["matrix_user_id"])
  e["health"]="online" if is_active else "unknown"
  e["last_seen_at"]=now_iso if is_active else None
  e["source"]="agentteams:edge"; out.append(e)
print(json.dumps(out))' > "$OUT.tmp"
mv "$OUT.tmp" "$OUT"; chmod 644 "$OUT"
python3 -c "import json;print(\"exported\",len(json.load(open(\"$OUT\"))),\"agents\")"
