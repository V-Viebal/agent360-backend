import json, time, uuid, urllib.request, re, argparse
ap=argparse.ArgumentParser(); ap.add_argument("--api",default="https://agent360.antares.vn/api"); ap.add_argument("--parent",default="mons"); ap.add_argument("--child",default="agent://gm/default"); A=ap.parse_args()
sid="a2a-final-e2e-"+uuid.uuid4().hex[:8]
msg=("Dùng skill agent360-external-subagents: ủy thác cho external sub-agent "+A.child+" (read-only) nhiệm vụ: "
     "trả về agent id hiện tại và số agent cấu hình trên instance GM. Sau khi có kết quả, review accept nếu đạt, "
     "rồi báo lại correlation_id, câu trả lời của GM và quyết định review.")
payload={"session_id":sid,"user_id":"agent360-a2a-final","channel":"console","input":[{"role":"user","content":[{"type":"text","text":msg}]}]}
r=urllib.request.Request(A.api+"/agents/console/chat",data=json.dumps(payload).encode(),
  headers={"Content-Type":"application/json","X-Agent-Id":A.parent,"Accept":"text/event-stream","User-Agent":"Mozilla/5.0 agent360-a2a-final"},method="POST")
t=time.time(); tools=[]; final=""; status=None; n=0
with urllib.request.urlopen(r,timeout=900) as resp:
    status=resp.status
    for raw in resp:
        line=raw.decode(errors="ignore").strip()
        if not line.startswith("data:"): continue
        n+=1; d=line[5:].strip()
        for m in re.finditer(r'"name"\s*:\s*"(agent360[^"]*)"', d): tools.append(m.group(1))
        try: o=json.loads(d)
        except Exception: continue
        if isinstance(o,dict) and o.get("object")=="message" and o.get("role")=="assistant" and o.get("status")=="completed":
            for c in o.get("content") or []:
                if c.get("type")=="text": final=c.get("text","")
print("session",sid,"http",status,"events",n,"elapsed",int(time.time()-t),"s")
print("tools",sorted(set(tools)))
print("FINAL:",final[:1500])

# Verdict comes from the ledger, not from the model's wording.
import os, sys
cids = re.findall(r"a2a_[0-9a-f]{12,}", final)
ok = "agent360-a2a__agent360_delegate_external" in tools and bool(cids)
if ok and os.environ.get("A2A_TOKEN"):
    t = json.load(urllib.request.urlopen(urllib.request.Request(A.api + "/a2a/delegations/" + cids[0],
        headers={"Authorization": "Bearer " + os.environ["A2A_TOKEN"], "User-Agent": "Mozilla/5.0 agent360-a2a-final"}), timeout=60))
    print("ledger", cids[0], t.get("status"), (t.get("acceptance") or {}).get("reviewer"))
    ok = t.get("status") == "accepted"
sys.exit(0 if ok else 1)
