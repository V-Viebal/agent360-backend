"""Agent360 external A2A control plane (HTTP API + Matrix listener + timeout sweeper).

Exposes the plan's routes under /api/v1 (proxied by Agent360 backend/nginx):
  GET  /api/v1/a2a/external-agents                       list directory
  POST /api/v1/a2a/external-agents/discover              sync from AgentTeams registry
  GET  /api/v1/a2a/external-agents/{agentRef}            detail
  POST /api/v1/a2a/external-agents/{agentRef}/probe      route + health probe
  GET  /api/v1/agents/{agentId}/external-subagents       bindings of a parent
  PUT  /api/v1/agents/{agentId}/external-subagents/{childRef}
  DELETE /api/v1/agents/{agentId}/external-subagents/{childRef}
  POST /api/v1/agents/{agentId}/external-subagents/sync-skill
  POST /api/v1/a2a/delegations                           create + send
  GET  /api/v1/a2a/delegations/{taskId}
  GET  /api/v1/agents/{agentId}/a2a/delegations
  POST /api/v1/a2a/delegations/{taskId}/cancel|review|retry
  POST /api/v1/a2a/agentteams/events                     signed inbound callback (HMAC)
  GET  /api/v1/a2a/health                                ok|degraded + reasons (public)
  GET  /api/v1/a2a/metrics                               counters, rates, latency (public, no payloads)
Auth: every route except the PUBLIC_READ set requires `Authorization: Bearer <service|operator token>`.
agentId may be a bare QwenPaw id (mons) or a full agent_ref; bare ids map to agent://core/<id>.
"""
from __future__ import annotations

import hashlib
import hmac
import json
import logging
import os
import re
import sys
import threading
import time
import urllib.parse
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from a2a_core import (EVENT_TYPE, MSG_CANCEL, MSG_REQUEST, MSG_RESULT, MSG_STATUS, TERMINAL, Ledger,
                      MatrixClient, check_policy, ensure_channel, new_id, now_iso, parse_agent_ref, redact)

log = logging.getLogger("a2a-cp")
CFG = {
    "enabled": os.environ.get("AGENT360_EXTERNAL_A2A_ENABLED", "true").lower() == "true",
    "db": os.environ.get("A2A_DB", "/state/agent360-a2a.db"),
    "cred": os.environ.get("A2A_MATRIX_CRED", "/secrets/agent360-a2a.matrix.json"),
    "domain": os.environ.get("A2A_MATRIX_DOMAIN", "agentteams-gateway.hmz.one"),
    "default_instance": os.environ.get("A2A_DEFAULT_INSTANCE", "core"),
    "agentteams_url": os.environ.get("AGENTTEAMS_CONTROLLER_URL", ""),
    "agentteams_token_file": os.environ.get("AGENTTEAMS_TOKEN_FILE", ""),
    "callback_secret": os.environ.get("A2A_CALLBACK_SECRET", ""),
    "service_token": os.environ.get("A2A_SERVICE_TOKEN", ""),
    # human operator key for the Agent360 UI (separate from the agent/MCP service token)
    "operator_token": os.environ.get("A2A_OPERATOR_TOKEN", ""),
    "offline_after": int(os.environ.get("A2A_OFFLINE_AFTER_SECONDS", "300")),
    "sync_stale_after": int(os.environ.get("A2A_SYNC_STALE_SECONDS", "120")),
    "qwenpaw": json.loads(os.environ.get("A2A_QWENPAW_INSTANCES", "{}")),  # {"core":"https://ai.hmz.one"}
    "static_agents": json.loads(os.environ.get("A2A_STATIC_AGENTS", "[]")),
    "registry_file": os.environ.get("A2A_REGISTRY_FILE", ""),  # exported by export_registry.sh
    "replay_window": int(os.environ.get("A2A_REPLAY_WINDOW_SECONDS", "300")),
}
L: Ledger | None = None
MX: MatrixClient | None = None


UA = {"User-Agent": "Mozilla/5.0 (compatible; agent360-a2a/1.0)"}
HEARTBEAT_TYPE = "agent360.worker.heartbeat.v1"
COUNTERS = {"callback_bad_signature": 0, "callback_stale": 0, "unverified_sender_dropped": 0,
            "duplicate_events": 0, "matrix_sync_errors": 0, "unauthorized": 0, "heartbeats": 0}
_CLOCK = threading.Lock()


def bump(name, n=1):
    with _CLOCK:
        COUNTERS[name] = COUNTERS.get(name, 0) + n


import ssl as _ssl


def qp_open(inst: str, path: str, method="GET", body=None, headers=None, timeout=60):
    """Open a QwenPaw instance URL. Instance cfg may be "https://host" or
    {"url":"https://<tailscale-ip>","host":"public.host"} (bypasses Cloudflare bot checks via Traefik)."""
    c = CFG["qwenpaw"].get(inst)
    if not c:
        raise RuntimeError(f"no QwenPaw base configured for instance {inst}")
    if isinstance(c, str):
        c = {"url": c}
    h = {**UA, **(headers or {})}
    ctx = None
    if c.get("host"):
        h["Host"] = c["host"]
        ctx = _ssl.create_default_context(); ctx.check_hostname = False; ctx.verify_mode = _ssl.CERT_NONE
    r = urllib.request.Request(c["url"].rstrip("/") + path, data=json.dumps(body).encode() if body is not None else None,
                               headers=h, method=method)
    return urllib.request.urlopen(r, timeout=timeout, context=ctx)


def norm_ref(ref: str) -> str:
    """Accept agent://i/a, URL-encoded, or proxy-collapsed agent:/i/a."""
    ref = urllib.parse.unquote(ref or "")
    return re.sub(r"^agent:/+", "agent://", ref)


def to_ref(agent_id: str) -> str:
    agent_id = norm_ref(agent_id)
    if agent_id.startswith("agent://"):
        return agent_id
    if agent_id.startswith("bot:"):
        agent_id = agent_id.split(":")[-1]
    return f"agent://{CFG['default_instance']}/{agent_id}"


# ------------------------------------------------------------ directory
def discover():
    found = 0
    # 1) AgentTeams registry workers + humans (service identities)
    if CFG["agentteams_url"] and CFG["agentteams_token_file"] and os.path.exists(CFG["agentteams_token_file"]):
        tok = open(CFG["agentteams_token_file"]).read().strip()
        def get(p):
            r = urllib.request.Request(CFG["agentteams_url"].rstrip("/") + p, headers={"Authorization": f"Bearer {tok}", **UA})
            return json.load(urllib.request.urlopen(r, timeout=15))
        for w in get("/api/v1/workers").get("workers", []):
            ident = w.get("identity") or ""
            m = re.match(r"qwenpaw://([^/]+)/(.+)", ident)
            inst, name = (m.group(1), m.group(2)) if m else (CFG["default_instance"], w["name"])
            L.upsert_agent({"agent_ref": f"agent://{inst}/{name}", "id": f"ext_{inst}_{name}",
                            "display_name": w.get("workerName") or name, "runtime": w.get("runtime", "qwenpaw"),
                            "instance_id": f"qwenpaw-{inst}", "node": None, "agentteams_worker_id": w["name"],
                            "matrix_user_id": w.get("matrixUserID"), "endpoint_ref": ident,
                            "capabilities": [], "source": "agentteams:worker"})
            found += 1
    # 2) registry snapshot exported from the AgentTeams node (workers + edge identities)
    if CFG["registry_file"] and os.path.exists(CFG["registry_file"]):
        for a in json.load(open(CFG["registry_file"])):
            L.upsert_agent(a)
            found += 1
    # 3) statically declared external agents (edge identities provisioned as AgentTeams humans)
    for a in CFG["static_agents"]:
        L.upsert_agent({**a, "source": a.get("source", "agentteams:edge")})
        found += 1
    return found


def probe(ref: str):
    a = L.agent(ref)
    if not a:
        raise KeyError(ref)
    res = {"agent_ref": ref, "matrix": None, "runtime": None}
    if a.get("matrix_user_id"):
        try:
            MX._call("GET", "/profile/" + urllib.parse.quote(a["matrix_user_id"]))
            res["matrix"] = "ok"
        except Exception as e:  # noqa: BLE001
            res["matrix"] = f"error: {str(e)[:120]}"
    inst, name = parse_agent_ref(ref)
    if CFG["qwenpaw"].get(inst):
        try:
            d = json.load(qp_open(inst, "/api/agents", timeout=30))
            ag = {x["id"]: x for x in d.get("agents", [])}
            res["runtime"] = "ok" if ag.get(name, {}).get("enabled") else "agent_missing_or_disabled"
        except urllib.error.HTTPError as e:
            # auth-protected runtimes: reachable but private -> rely on worker heartbeat via Matrix
            res["runtime"] = "auth_protected" if e.code in (401, 403) else f"error: HTTP {e.code}"
        except Exception as e:  # noqa: BLE001
            res["runtime"] = f"error: {str(e)[:120]}"
    healthy = res["matrix"] == "ok" and res["runtime"] in ("ok", "auth_protected", None)
    L.set_health(ref, "online" if healthy else "degraded")
    res["health"] = "online" if healthy else "degraded"
    return res


# ------------------------------------------------------------ skill
def render_skill(parent_ref: str) -> str:
    rows = []
    for b in L.bindings_for(parent_ref):
        a = L.agent(b["child_agent_ref"]) or {}
        rows.append(f"| `{b['child_agent_ref']}` | {a.get('display_name','')} | {b['role']} | "
                    f"{', '.join(a.get('capabilities') or []) or '-'} | {', '.join(b['allowed_capabilities']) or 'any'} | "
                    f"{b['timeout_seconds']}s | {'yes' if b['requires_human_approval_for_writes'] else 'no'} | {a.get('health','unknown')} |")
    table = "\n".join(rows) or "| _(no external sub-agents bound)_ | | | | | | | |"
    return f"""---
name: agent360-external-subagents
description: Delegate work to external sub-agents on other QwenPaw instances through Agent360 + AgentTeams (Matrix). Use when a task belongs to another project/instance listed below.
status: active
owner: agent360
version: 1.0.0
---

# External sub-agents (Agent360 A2A via AgentTeams)

Parent: `{parent_ref}` — generated by Agent360 at {now_iso()}. Do not edit by hand; re-sync from Agent360.

## Bound external sub-agents

| agent_ref | name | role | capabilities | allowed scope | timeout | write approval | health |
|---|---|---|---|---|---|---|---|
{table}

## Tools (MCP server `agent360-a2a`)

- `agent360_list_external_subagents` — refresh this list (health, bindings).
- `agent360_delegate_external` — send a task. Required: `child_agent_ref`, `objective`, `acceptance_criteria` (list). Optional: `context`, `constraints`, `expected_output`, `capabilities` (ONLY values from the *allowed scope* column; omit if unsure), `wait_seconds` (blocks until done, max 600).
- `agent360_check_external_delegation` — read status/output/evidence by `task_id`.
- `agent360_review_external_delegation` — `decision` = `accept` | `reject` (+`feedback`); reject auto-retries once.

## Rules

1. Only delegate to an agent in the table above; never invent `agent_ref`.
2. Default mode is async; use `wait_seconds` for short read-only tasks.
3. Never put secrets, tokens, env values or credentials in any field.
4. Write actions (create/update/delete/send) require explicit human approval: pass `approved=true` only when the user approved.
   For read-only tasks add the constraint `Read-only` so the policy does not treat verbs like "list/send report" as writes.
4b. If the tool returns `policy_denied`, read the reason and `allowed_capabilities`, fix the request and call again once.
5. Always provide concrete `acceptance_criteria`; review the returned `evidence` and `self_check` against them before `accept`.
6. If output fails criteria, `reject` with precise feedback (one retry max), otherwise report the gap to the user.
7. Quote the `correlation_id` when reporting delegated results.
"""


def sync_skill(parent_ref: str):
    inst, name = parse_agent_ref(parent_ref)
    content = render_skill(parent_ref)
    skill = "agent360-external-subagents"
    hdr = {"Content-Type": "application/json", "X-Agent-Id": name}

    def call(method, path, body=None):
        try:
            with qp_open(inst, path, method, body, hdr, timeout=90) as resp:
                return resp.status, json.loads(resp.read() or b"{}")
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode("utf-8", "ignore")[:300]
    st, body = call("POST", "/api/skills", {"name": skill, "content": content, "enable": True})
    if st == 409:
        st, body = call("PUT", "/api/skills/save", {"name": skill, "content": content, "overwrite": True})
    call("POST", f"/api/skills/{skill}/enable")
    return {"status": st, "skill": skill, "agent": name, "bytes": len(content)}


# ------------------------------------------------------------ delegation
def binding_room(b):
    if b.get("matrix_room_id"):
        return b["matrix_room_id"]
    child = L.agent(b["child_agent_ref"])
    if not child or not child.get("matrix_user_id"):
        raise RuntimeError(f"child {b['child_agent_ref']} has no Matrix identity")
    rid = ensure_channel(MX, CFG["domain"], b["parent_agent_ref"], b["child_agent_ref"], child["matrix_user_id"])
    L.upsert_binding({**b, "matrix_room_id": rid})
    return rid


def send_request(t, feedback=None):
    b = L.binding(t["parent_agent_ref"], t["child_agent_ref"])
    room = binding_room(b)
    msg = {"type": MSG_REQUEST, "correlation_id": t["correlation_id"], "task_id": t["id"],
           "root_run_id": t["root_run_id"], "parent_agent_ref": t["parent_agent_ref"],
           "child_agent_ref": t["child_agent_ref"], "delegation_depth": 1,
           "call_chain": [t["parent_agent_ref"], t["child_agent_ref"]], "mode": t["mode"],
           "objective": t["objective"], "context": t["context"] or "", "constraints": t["constraints"],
           "expected_output": t["expected_output"] or "", "acceptance_criteria": t["acceptance_criteria"],
           "deadline_at": t["deadline_at"], "reply_to": t["parent_agent_ref"], "attempt": t["attempt"],
           "approved_write": bool(t["approved"])}
    if feedback:
        msg["context"] = (msg["context"] + f"\n\nPREVIOUS ATTEMPT REJECTED. Reviewer feedback: {feedback}").strip()
    msg["body"] = f"[{MSG_REQUEST}] {t['correlation_id']} attempt {t['attempt']}"
    eid = MX.send(room, redact(msg), txn_id=f"{t['correlation_id']}-{t['attempt']}")
    return L.transition(t["id"], "sent", transport_message_id=eid, transport_room_id=room)


def create_delegation(body):
    req = {
        "parent_agent_ref": to_ref(body["parent_agent_ref"]),
        "child_agent_ref": norm_ref(body["child_agent_ref"]),
        "mode": body.get("mode", "async"),
        "objective": body["objective"],
        "context": body.get("context", ""),
        "constraints": body.get("constraints") or [],
        "expected_output": body.get("expected_output", ""),
        "acceptance_criteria": body.get("acceptance_criteria") or [],
        "capabilities": body.get("capabilities") or [],
        "write_intent": body.get("write_intent", False),
        "approved": body.get("approved", False),
        "delegation_depth": body.get("delegation_depth", 1),
        "call_chain": body.get("call_chain"),
    }
    # idempotency: same correlation id returns the existing task
    cid = body.get("correlation_id") or new_id("a2a")
    existing = L.task(cid)
    if existing:
        return existing, True
    ok, reason = check_policy(L, req, CFG["enabled"])
    b = L.binding(req["parent_agent_ref"], req["child_agent_ref"])
    timeout = (b or {}).get("timeout_seconds", 900)
    t = L.insert_task({
        "id": new_id("xdlg"), "correlation_id": cid, "root_run_id": body.get("root_run_id") or new_id("run"),
        "parent_agent_ref": req["parent_agent_ref"], "child_agent_ref": req["child_agent_ref"],
        "transport": "agentteams-matrix", "mode": req["mode"], "objective": redact(req["objective"]),
        "context": redact(req["context"]), "constraints": redact(req["constraints"]),
        "expected_output": redact(req["expected_output"]), "acceptance_criteria": req["acceptance_criteria"],
        "write_intent": int(bool(req["write_intent"])), "approved": int(bool(req["approved"])),
        "status": "draft", "attempt": 1, "max_attempts": int(body.get("max_attempts", 2)),
        "deadline_at": (datetime.now(timezone.utc) + timedelta(seconds=timeout)).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "evidence": [], "self_check": [], "created_at": now_iso(), "updated_at": now_iso()})
    if not ok:
        return L.transition(t["id"], "cancelled", acceptance={"decision": "policy_denied", "reason": reason}), False
    L.transition(t["id"], "policy_checked")
    try:
        return send_request(L.task(t["id"])), False
    except Exception as e:  # noqa: BLE001
        log.exception("send failed")
        return L.transition(t["id"], "delivery_failed", summary=redact(str(e))[:500]), False


def apply_event(content, source):
    typ = content.get("type")
    cid = content.get("correlation_id")
    t = L.task(cid) if cid else None
    if not t or typ not in (MSG_STATUS, MSG_RESULT):
        return "ignored"
    eid = content.get("event_id") or f"{cid}:{typ}:{content.get('status')}:{content.get('updated_at')}"
    if not L.record_event(eid, cid, typ, source, content):
        return "duplicate"
    if content.get("child_agent_ref") and content["child_agent_ref"] != t["child_agent_ref"]:
        return "identity_mismatch"
    if t["status"] in TERMINAL or t["status"] in ("completed", "under_review"):
        return "late_event"
    st = content.get("status")
    try:
        if typ == MSG_STATUS and st in ("delivered", "running"):
            if t["status"] == "sent" and st == "running":
                L.transition(t["id"], "delivered")
            L.transition(t["id"], st)
        elif typ == MSG_RESULT:
            if st == "completed":
                L.transition(t["id"], "completed", output=content.get("output"), summary=content.get("summary"),
                             evidence=content.get("evidence") or [], self_check=content.get("self_check") or [],
                             child_session_id=content.get("child_session_id"))
                L.transition(t["id"], "under_review")
            elif st == "cancelled":
                L.transition(t["id"], "cancelled", summary=content.get("summary"))
            else:
                L.transition(t["id"], "failed", output=content.get("output"), summary=content.get("summary"))
    except ValueError as e:
        log.warning("event %s ignored: %s", eid, e)
        return "illegal_transition"
    return "applied"


def review(task_id, decision, feedback=None, reviewer="parent", checklist=None):
    t = L.task(task_id)
    if not t:
        raise KeyError(task_id)
    if t["status"] != "under_review":
        raise ValueError(f"task status {t['status']} is not under_review")
    crit = t["acceptance_criteria"]
    sc = {s.get("criterion"): s.get("result") for s in (t["self_check"] or [])}
    checklist = checklist or [{"criterion": c, "self_check": sc.get(c, "n/a")} for c in crit]
    acc = {"decision": decision, "reviewer": reviewer, "feedback": feedback, "checklist": checklist, "at": now_iso()}
    if decision == "accept":
        return L.transition(task_id, "accepted", acceptance=acc)
    t = L.transition(task_id, "rejected", acceptance=acc, feedback=feedback)
    if t["attempt"] < t["max_attempts"]:
        return retry(task_id, feedback)
    return t


def retry(task_id, feedback=None):
    t = L.task(task_id)
    if t["status"] != "rejected":
        raise ValueError(f"retry requires rejected, got {t['status']}")
    timeout = (L.binding(t["parent_agent_ref"], t["child_agent_ref"]) or {}).get("timeout_seconds", 900)
    L.transition(task_id, "retry_queued", attempt=t["attempt"] + 1, output=None, summary=None,
                 deadline_at=(datetime.now(timezone.utc) + timedelta(seconds=timeout)).strftime("%Y-%m-%dT%H:%M:%SZ"))
    return send_request(L.task(task_id), feedback=feedback or t.get("feedback"))


def cancel(task_id):
    t = L.task(task_id)
    if t["status"] in TERMINAL:
        return t
    if t.get("transport_room_id"):
        try:
            MX.send(t["transport_room_id"], {"type": MSG_CANCEL, "correlation_id": t["correlation_id"],
                                             "task_id": t["id"], "body": f"[cancel] {t['correlation_id']}"})
        except Exception:  # noqa: BLE001
            pass
    if t["status"] in ("completed", "under_review"):
        raise ValueError("task already completed; review it instead")
    return L.transition(task_id, "cancelled")


# ------------------------------------------------------------ background loops
def matrix_loop():
    me = MX.whoami()["user_id"]
    since = L.kv_get("cp_since")
    while True:
        try:
            s = MX.sync(since, timeout_ms=25000)
            if since:
                for rid, room in (s.get("rooms", {}).get("join") or {}).items():
                    for ev in room.get("timeline", {}).get("events", []):
                        if ev.get("type") == EVENT_TYPE and ev.get("sender") != me:
                            sender = ev.get("sender")
                            content = ev.get("content") or {}
                            child = L.agent(content.get("child_agent_ref") or "")
                            if not child or child.get("matrix_user_id") != sender:
                                bump("unverified_sender_dropped")
                                log.warning("drop event from unverified sender %s", sender)
                                continue
                            if content.get("type") == HEARTBEAT_TYPE:
                                bump("heartbeats")
                                L.set_health(child["agent_ref"], "online")
                                continue
                            content.setdefault("event_id", ev.get("event_id"))
                            r = apply_event(content, f"matrix:{sender}")
                            if r == "duplicate":
                                bump("duplicate_events")
                            L.set_health(child["agent_ref"], "online")
                            log.info("event %s %s -> %s", content.get("type"), content.get("correlation_id"), r)
            since = s.get("next_batch", since)
            L.kv_set("cp_since", since)
            L.kv_set("cp_last_sync_ok", str(int(time.time())))
        except Exception as e:  # noqa: BLE001
            bump("matrix_sync_errors")
            log.warning("matrix sync: %s", redact(str(e))[:200])
            time.sleep(5)


def sweeper_loop():
    while True:
        try:
            now = datetime.now(timezone.utc)
            for t in L.tasks(limit=500):
                if t["status"] in ("sent", "delivered", "running", "retry_queued") and t.get("deadline_at"):
                    if datetime.strptime(t["deadline_at"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc) < now:
                        L.transition(t["id"], "timed_out", summary="deadline exceeded")
                        log.info("timed_out %s", t["correlation_id"])
            bound = {b["child_agent_ref"] for b in L.q("SELECT child_agent_ref FROM external_subagent_bindings WHERE enabled=1")}
            for a in L.agents():
                if a["agent_ref"] in bound and a.get("health") == "online" and a.get("last_seen_at"):
                    seen = datetime.strptime(a["last_seen_at"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
                    if (now - seen).total_seconds() > CFG["offline_after"]:
                        L.x("UPDATE external_agents SET health='offline' WHERE agent_ref=?", (a["agent_ref"],))
                        log.warning("agent %s offline (no heartbeat since %s)", a["agent_ref"], a["last_seen_at"])
        except Exception as e:  # noqa: BLE001
            log.warning("sweeper: %s", e)
        time.sleep(10)


# ------------------------------------------------------------ HTTP
ROUTES = []


def route(method, pattern):
    def deco(fn):
        ROUTES.append((method, re.compile("^" + pattern + "$"), fn))
        return fn
    return deco


def _ts(v):
    return datetime.strptime(v, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc) if v else None


def metrics(window_hours=24):
    since = (datetime.now(timezone.utc) - timedelta(hours=window_hours)).strftime("%Y-%m-%dT%H:%M:%SZ")
    rows = L.q("SELECT status, attempt, child_agent_ref, created_at, updated_at, acceptance FROM external_delegation_tasks "
               "WHERE created_at >= ?", (since,))
    by = {}
    for r in rows:
        by[r["status"]] = by.get(r["status"], 0) + 1
    total = len(rows)
    finished = [r for r in rows if r["status"] in TERMINAL]
    lat = sorted((_ts(r["updated_at"]) - _ts(r["created_at"])).total_seconds() for r in finished if r["status"] == "accepted")
    reviewed = [r for r in rows if r["status"] in ("accepted", "rejected") or (r["attempt"] or 1) > 1]

    def rate(n, d):
        return round(n / d, 3) if d else 0.0
    active = {}
    for r in L.q("SELECT child_agent_ref, COUNT(*) n FROM external_delegation_tasks WHERE status IN "
                 "('sent','delivered','running','retry_queued','policy_checked') GROUP BY child_agent_ref"):
        active[r["child_agent_ref"]] = r["n"]
    last_ok = int(L.kv_get("cp_last_sync_ok", "0") or 0)
    return {
        "window_hours": window_hours, "delegations_total": total, "by_status": by,
        "timeout_rate": rate(by.get("timed_out", 0), total),
        "failure_rate": rate(by.get("failed", 0) + by.get("delivery_failed", 0), total),
        "retry_rate": rate(sum(1 for r in rows if (r["attempt"] or 1) > 1), total),
        "acceptance_rate": rate(by.get("accepted", 0), len(reviewed)),
        "policy_denied": sum(1 for r in rows if r["status"] == "cancelled" and '"policy_denied"' in (r["acceptance"] or "")),
        "latency_seconds_accepted": {"p50": lat[len(lat) // 2] if lat else None, "max": lat[-1] if lat else None},
        "active_by_child": active,
        "agents": [{"agent_ref": a["agent_ref"], "health": a["health"], "last_seen_at": a["last_seen_at"]} for a in L.agents()],
        "matrix_last_sync_age_seconds": int(time.time()) - last_ok if last_ok else None,
        "counters": dict(COUNTERS),
    }


def health():
    m = metrics()
    reasons = []
    if not CFG["enabled"]:
        reasons.append("feature_flag_disabled")
    age = m["matrix_last_sync_age_seconds"]
    if age is None or age > CFG["sync_stale_after"]:
        reasons.append(f"agentteams_matrix_sync_stale:{age}")
    bound = {b["child_agent_ref"] for b in L.q("SELECT child_agent_ref FROM external_subagent_bindings WHERE enabled=1")}
    for a in m["agents"]:
        if a["agent_ref"] in bound and a["health"] != "online":
            reasons.append(f"worker_{a['health']}:{a['agent_ref']}")
    if m["delegations_total"] >= 3 and m["timeout_rate"] > 0.3:
        reasons.append(f"timeout_rate_high:{m['timeout_rate']}")
    if m["counters"].get("callback_bad_signature", 0) >= 5:
        reasons.append("callback_signature_failures")
    return {"status": "ok" if not reasons else "degraded", "reasons": reasons, "enabled": CFG["enabled"],
            "matrix_user": MX.user_id, "agents": len(m["agents"]), "bound_children": sorted(bound)}


@route("GET", r"/api/v1/a2a/health")
def r_health(h, m, b):
    return 200, health()


@route("GET", r"/api/v1/a2a/metrics")
def r_metrics(h, m, b):
    return 200, metrics()


@route("GET", r"/api/v1/a2a/external-agents")
def r_agents(h, m, b):
    return 200, L.agents()


@route("POST", r"/api/v1/a2a/external-agents/discover")
def r_discover(h, m, b):
    return 200, {"discovered": discover(), "agents": L.agents()}


@route("GET", r"/api/v1/a2a/external-agents/(?P<ref>.+?)")
def r_agent(h, m, b):
    a = L.agent(norm_ref(m["ref"]))
    return (200, a) if a else (404, {"error": "not found"})


@route("POST", r"/api/v1/a2a/external-agents/(?P<ref>.+)/probe")
def r_probe(h, m, b):
    return 200, probe(norm_ref(m["ref"]))


@route("GET", r"/api/v1/agents/(?P<aid>[^/]+)/external-subagents")
def r_bindings(h, m, b):
    return 200, L.bindings_for(to_ref(m["aid"]))


@route("POST", r"/api/v1/agents/(?P<aid>[^/]+)/external-subagents/sync-skill")
def r_sync(h, m, b):
    return 200, sync_skill(to_ref(m["aid"]))


@route("PUT", r"/api/v1/agents/(?P<aid>[^/]+)/external-subagents/(?P<ref>.+)")
def r_bind(h, m, b):
    child = norm_ref(m["ref"])
    if not L.agent(child):
        return 404, {"error": f"external agent {child} not in directory (discover first)"}
    bind = L.upsert_binding({**(b or {}), "parent_agent_ref": to_ref(m["aid"]), "child_agent_ref": child})
    try:
        binding_room(bind)
    except Exception as e:  # noqa: BLE001
        return 502, {"error": f"binding saved but channel failed: {e}"}
    return 200, L.binding(to_ref(m["aid"]), child)


@route("DELETE", r"/api/v1/agents/(?P<aid>[^/]+)/external-subagents/(?P<ref>.+)")
def r_unbind(h, m, b):
    return 200, {"deleted": L.delete_binding(to_ref(m["aid"]), norm_ref(m["ref"]))}


@route("POST", r"/api/v1/a2a/delegations")
def r_create(h, m, b):
    t, dup = create_delegation(b or {})
    code = 200 if dup else (201 if t["status"] not in ("cancelled", "delivery_failed") else 422)
    return code, {**t, "idempotent_replay": dup}


@route("GET", r"/api/v1/a2a/delegations/(?P<tid>[^/]+)")
def r_task(h, m, b):
    t = L.task(m["tid"])
    return (200, t) if t else (404, {"error": "not found"})


@route("GET", r"/api/v1/agents/(?P<aid>[^/]+)/a2a/delegations")
def r_tasks(h, m, b):
    return 200, L.tasks(to_ref(m["aid"]))


@route("POST", r"/api/v1/a2a/delegations/(?P<tid>[^/]+)/cancel")
def r_cancel(h, m, b):
    return 200, cancel(m["tid"])


@route("POST", r"/api/v1/a2a/delegations/(?P<tid>[^/]+)/review")
def r_review(h, m, b):
    b = b or {}
    return 200, review(m["tid"], b.get("decision", "accept"), b.get("feedback"), b.get("reviewer", "parent"), b.get("checklist"))


@route("POST", r"/api/v1/a2a/delegations/(?P<tid>[^/]+)/retry")
def r_retry(h, m, b):
    return 200, retry(m["tid"], (b or {}).get("feedback"))


@route("POST", r"/api/v1/a2a/agentteams/events")
def r_events(h, m, b):
    sec = CFG["callback_secret"]
    if not sec:
        return 503, {"error": "callback secret not configured"}
    ts = h.headers.get("X-A2A-Timestamp", "")
    sig = h.headers.get("X-A2A-Signature", "")
    try:
        if abs(time.time() - int(ts)) > CFG["replay_window"]:
            bump("callback_stale")
            return 401, {"error": "stale timestamp"}
    except ValueError:
        return 401, {"error": "missing timestamp"}
    expect = hmac.new(sec.encode(), f"{ts}.".encode() + h._raw, hashlib.sha256).hexdigest()
    if not hmac.compare_digest(expect, sig.removeprefix("sha256=")):
        bump("callback_bad_signature")
        return 401, {"error": "bad signature"}
    r = apply_event(b or {}, "callback")
    if r == "duplicate":
        bump("duplicate_events")
    return 200, {"result": r}


# Read-only, payload-free routes that the public UI may call without a token.
PUBLIC_READ = [re.compile(p) for p in (
    r"^/api/v1/a2a/health$", r"^/api/v1/a2a/metrics$", r"^/api/v1/a2a/external-agents(/.*)?$",
    r"^/api/v1/agents/[^/]+/external-subagents$")]


def authorized(method, path, auth_header):
    if path.endswith("/agentteams/events"):
        return True  # HMAC-verified in the handler
    if method == "GET" and any(rx.match(path) for rx in PUBLIC_READ) and not path.endswith("/probe"):
        return True
    tokens = [t for t in (CFG["service_token"], CFG["operator_token"]) if t]
    if not tokens:
        return True  # local/dev mode without tokens
    given = auth_header.removeprefix("Bearer ").strip()
    return any(hmac.compare_digest(given, t) for t in tokens)


class H(BaseHTTPRequestHandler):
    server_version = "agent360-a2a/1.0"

    def log_message(self, fmt, *args):
        log.info("%s %s", self.address_string(), fmt % args)

    def _do(self, method):
        path = urllib.parse.urlsplit(self.path).path
        n = int(self.headers.get("Content-Length") or 0)
        self._raw = self.rfile.read(n) if n else b""
        if not authorized(method, path, self.headers.get("Authorization", "")):
            bump("unauthorized")
            return self._send(401, {"error": "unauthorized: A2A service or operator token required"})
        try:
            body = json.loads(self._raw) if self._raw else None
        except json.JSONDecodeError:
            return self._send(400, {"error": "invalid json"})
        for mth, rx, fn in ROUTES:
            mm = rx.match(path)
            if mth == method and mm:
                try:
                    code, payload = fn(self, mm.groupdict(), body)
                except KeyError as e:
                    code, payload = 404, {"error": f"not found: {e}"}
                except ValueError as e:
                    code, payload = 409, {"error": str(e)}
                except Exception as e:  # noqa: BLE001
                    log.exception("handler")
                    code, payload = 500, {"error": redact(str(e))[:300]}
                return self._send(code, payload)
        self._send(404, {"error": "no route"})

    def _send(self, code, payload):
        data = json.dumps(payload, ensure_ascii=False, default=str).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    do_GET = lambda s: s._do("GET")  # noqa: E731
    do_POST = lambda s: s._do("POST")  # noqa: E731
    do_PUT = lambda s: s._do("PUT")  # noqa: E731
    do_DELETE = lambda s: s._do("DELETE")  # noqa: E731


def main():
    global L, MX
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    L = Ledger(CFG["db"])
    cred = json.load(open(CFG["cred"]))
    MX = MatrixClient(cred["homeserver"], cred["access_token"], cred["user_id"])
    discover()
    threading.Thread(target=matrix_loop, daemon=True).start()
    threading.Thread(target=sweeper_loop, daemon=True).start()
    port = int(os.environ.get("PORT", "8095"))
    log.info("agent360-a2a control plane on :%d as %s", port, MX.user_id)
    ThreadingHTTPServer(("0.0.0.0", port), H).serve_forever()


if __name__ == "__main__":
    main()
