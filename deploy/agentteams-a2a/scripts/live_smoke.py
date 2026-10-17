#!/usr/bin/env python3
"""Live acceptance for external A2A (run from anywhere that reaches the public Agent360 API).
Usage: A2A_TOKEN=<service|operator token> live_smoke.py --api https://agent360.antares.vn/api --parent mons --child agent://gm/default
       [--offline-cmd "ssh root@<child-node> docker stop <worker>" --online-cmd "ssh ... docker start <worker>"]
Exit 0 only if: health ok, anonymous writes rejected, probe ok, delegation accepted, reject->retry->accepted,
cancel works, write denied, metrics consistent, and (when offline/online commands are given) child offline is
detected (timed_out + health degraded) and recovers."""
import argparse, json, os, subprocess, sys, time, urllib.parse, urllib.request

ap = argparse.ArgumentParser()
ap.add_argument("--api", required=True); ap.add_argument("--parent", required=True); ap.add_argument("--child", required=True)
ap.add_argument("--wait", type=int, default=300)
ap.add_argument("--offline-cmd"); ap.add_argument("--online-cmd")
ap.add_argument("--offline-timeout", type=int, default=45)
A = ap.parse_args(); RUN = f"{int(time.time())}"
enc = lambda s: urllib.parse.quote(s, safe="")


TOKEN = os.environ.get("A2A_TOKEN", "")


def call(m, p, b=None, auth=True):
    h = {"Content-Type": "application/json", "User-Agent": "agent360-a2a-smoke/1.0"}
    if auth and TOKEN:
        h["Authorization"] = f"Bearer {TOKEN}"
    r = urllib.request.Request(A.api.rstrip("/") + p, data=json.dumps(b).encode() if b is not None else None,
                               headers=h, method=m)
    try:
        return json.loads(urllib.request.urlopen(r, timeout=120).read() or b"{}")
    except urllib.error.HTTPError as e:
        return {"http_error": e.code, **(json.loads(e.read() or b"{}") if e.code != 502 else {})}


def wait(tid):
    end = time.time() + A.wait
    while time.time() < end:
        t = call("GET", f"/a2a/delegations/{tid}")
        if t.get("status") in ("under_review", "accepted", "failed", "timed_out", "cancelled", "delivery_failed"):
            return t
        time.sleep(6)
    return t


ok = True
def check(name, cond, info=""):
    global ok
    ok &= bool(cond); print(("PASS " if cond else "FAIL ") + name, info)


h = call("GET", "/a2a/health", auth=False); check("health ok", h.get("status") == "ok", h.get("reasons"))
check("anon ledger read rejected", call("GET", f"/agents/{A.parent}/a2a/delegations", auth=False).get("http_error") == 401)
check("anon delegate rejected", call("POST", "/a2a/delegations", {"parent_agent_ref": A.parent}, auth=False).get("http_error") == 401)
p = call("POST", f"/a2a/external-agents/{enc(A.child)}/probe"); check("probe matrix", p.get("matrix") == "ok", p)
b = call("GET", f"/agents/{A.parent}/external-subagents"); check("binding exists", any(x["child_agent_ref"] == A.child for x in b))
t = call("POST", "/a2a/delegations", {"parent_agent_ref": A.parent, "child_agent_ref": A.child, "correlation_id": f"a2a_smoke_{RUN}",
     "objective": "Read-only: state your agent id and how many agents your QwenPaw instance has.", "constraints": ["Read-only"],
     "acceptance_criteria": ["States agent id", "States agent count", "No write action"]})
check("sent", t.get("status") == "sent", t.get("transport_message_id"))
t = wait(t["id"]); check("under_review", t.get("status") == "under_review", (t.get("summary") or "")[:120])
t = call("POST", f"/a2a/delegations/{t['id']}/review", {"decision": "accept", "reviewer": "live_smoke"}); check("accepted", t.get("status") == "accepted")
t = call("POST", "/a2a/delegations", {"parent_agent_ref": A.parent, "child_agent_ref": A.child, "correlation_id": f"a2a_smoke_retry_{RUN}",
     "objective": "Read-only: list the agent ids of your QwenPaw instance.", "constraints": ["Read-only"], "acceptance_criteria": ["Lists ids"]})
t = wait(t["id"]); t = call("POST", f"/a2a/delegations/{t['id']}/review", {"decision": "reject", "feedback": "End with a line TOTAL=<count>."})
check("reject->retry", t.get("status") == "sent" and t.get("attempt") == 2)
t = wait(t["id"]); check("retry output has TOTAL", "TOTAL=" in (t.get("output") or ""))
t = call("POST", f"/a2a/delegations/{t['id']}/review", {"decision": "accept"}); check("retry accepted", t.get("status") == "accepted")
d = call("POST", "/a2a/delegations", {"parent_agent_ref": A.parent, "child_agent_ref": A.child, "objective": "Delete all overdue cards",
     "acceptance_criteria": ["done"], "correlation_id": f"a2a_smoke_deny_{RUN}"})
check("write denied", (d.get("acceptance") or {}).get("decision") == "policy_denied", (d.get("acceptance") or {}).get("reason"))
c = call("POST", "/a2a/delegations", {"parent_agent_ref": A.parent, "child_agent_ref": A.child, "correlation_id": f"a2a_smoke_cancel_{RUN}",
     "objective": "Read-only: count to 3 slowly.", "acceptance_criteria": ["counts"]})
c = call("POST", f"/a2a/delegations/{c['id']}/cancel"); check("cancel", c.get("status") == "cancelled")
time.sleep(5)
m = call("GET", "/a2a/metrics", auth=False)
check("metrics", m.get("by_status", {}).get("accepted", 0) >= 2 and m.get("counters", {}).get("callback_bad_signature", 0) == 0,
      {k: m.get(k) for k in ("delegations_total", "acceptance_rate", "timeout_rate", "retry_rate")})

if A.offline_cmd and A.online_cmd:
    subprocess.run(A.offline_cmd, shell=True, check=True, timeout=120)
    try:
        call("PUT", f"/agents/{A.parent}/external-subagents/{enc(A.child)}", {"timeout_seconds": A.offline_timeout,
             "allowed_capabilities": [x for b2 in b if b2["child_agent_ref"] == A.child for x in b2["allowed_capabilities"]]})
        o = call("POST", "/a2a/delegations", {"parent_agent_ref": A.parent, "child_agent_ref": A.child,
                 "correlation_id": f"a2a_smoke_offline_{RUN}", "objective": "Read-only: say hi.", "acceptance_criteria": ["hi"]})
        if "id" not in o:
            print("offline create response:", o)
            o = {"id": None, "status": "create_failed"}
        end = time.time() + A.offline_timeout + 60
        while o["id"] and time.time() < end and o.get("status") not in ("timed_out", "delivery_failed"):
            time.sleep(5); o = call("GET", f"/a2a/delegations/{o['id']}")
        check("child offline -> timed_out", o.get("status") == "timed_out", o.get("status"))
        end = time.time() + 400
        hh = {}
        while time.time() < end:
            hh = call("GET", "/a2a/health", auth=False)
            if any("worker_offline" in r for r in hh.get("reasons", [])):
                break
            time.sleep(15)
        check("health degraded: worker offline", any("worker_offline" in r for r in hh.get("reasons", [])), hh.get("reasons"))
    finally:
        orig = [x for x in b if x["child_agent_ref"] == A.child][0]
        call("PUT", f"/agents/{A.parent}/external-subagents/{enc(A.child)}", {k: orig[k] for k in (
             "role", "allowed_modes", "allowed_capabilities", "max_depth", "max_parallel_tasks", "timeout_seconds",
             "requires_human_approval_for_writes", "enabled")})
        subprocess.run(A.online_cmd, shell=True, check=True, timeout=120)
    end = time.time() + 180
    while time.time() < end and call("GET", "/a2a/health", auth=False).get("status") != "ok":
        time.sleep(10)
    check("health recovered", call("GET", "/a2a/health", auth=False).get("status") == "ok")

print("RESULT", "PASS" if ok else "FAIL"); sys.exit(0 if ok else 1)
