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
from a2a_core import (EVENT_TYPE, MAX_THREAD_TEXT, MSG_CANCEL, MSG_MESSAGE, MSG_REQUEST, MSG_RESULT, MSG_STATUS,
                      PROTOCOL_VERSION, TERMINAL, WRITE_WORDS, Ledger, MatrixClient, check_policy, ensure_channel,
                      new_id, now_iso, parse_agent_ref, redact, send_reliable)

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
    # registry.json is re-exported by a cron on the AgentTeams node; older than this -> degraded
    "registry_stale_after": int(os.environ.get("A2A_REGISTRY_STALE_SECONDS", "1200")),
    "qwenpaw": json.loads(os.environ.get("A2A_QWENPAW_INSTANCES", "{}")),  # {"core":"https://ai.hmz.one"}
    "static_agents": json.loads(os.environ.get("A2A_STATIC_AGENTS", "[]")),
    "registry_file": os.environ.get("A2A_REGISTRY_FILE", ""),  # exported by export_registry.sh
    "replay_window": int(os.environ.get("A2A_REPLAY_WINDOW_SECONDS", "300")),
    # v2 multi-turn defaults (per-delegation `conversation` overrides)
    "max_turns": int(os.environ.get("A2A_MAX_TURNS", "8")),
    "max_questions": int(os.environ.get("A2A_MAX_QUESTIONS", "3")),
    "question_timeout": int(os.environ.get("A2A_QUESTION_TIMEOUT_SECONDS", "600")),
    # parent wake-up when a blocking question arrives and no parent turn is waiting on it
    "wake_parent": os.environ.get("A2A_WAKE_PARENT", "true").lower() == "true",
    "wake_after": int(os.environ.get("A2A_WAKE_PARENT_AFTER_SECONDS", "20")),
}
L: Ledger | None = None
MX: MatrixClient | None = None


UA = {"User-Agent": "Mozilla/5.0 (compatible; agent360-a2a/1.0)"}
HEARTBEAT_TYPE = "agent360.worker.heartbeat.v1"
COUNTERS = {"callback_bad_signature": 0, "callback_stale": 0, "unverified_sender_dropped": 0,
            "duplicate_events": 0, "matrix_sync_errors": 0, "unauthorized": 0, "heartbeats": 0,
            "turns_total": 0, "questions_total": 0, "answers_total": 0, "followups_total": 0,
            "question_timeouts": 0, "parent_wakeups": 0, "parent_wakeup_errors": 0, "escalations": 0}
WORKER_PROTOCOL: dict[str, int] = {}   # child agent_ref -> protocol advertised in heartbeat
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
- `agent360_reply_external_delegation` — answer the sub-agent's open question (`pending_question`), optionally wait again.
- `agent360_followup_external_delegation` — ask a follow-up about a result; the sub-agent keeps the whole thread.
- `agent360_list_external_questions` — questions your sub-agents are waiting on you to answer.

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

## Conversation (multi-turn)

8. A result may instead come back as `status: awaiting_parent` with `pending_question` (and `options`). Answer it
   with `agent360_reply_external_delegation` (task_id + precise answer, `wait_seconds` 120–300), then continue until
   the result arrives. Answer from facts you know or can check; never invent values.
9. If the question needs a human decision, a secret, or a write approval (`kind: approval_request`), do not guess and
   do not send a non-answer: call `agent360_reply_external_delegation` with `escalate_to_operator: true` and a one-line
   reason. The question then waits for a human operator in Agent360 (only operators approve writes); tell the user.
10. To refine a result, prefer `agent360_followup_external_delegation` over `reject` (same session, same attempt).
10b. A result with `status: under_review` is NOT accepted yet. Always call `agent360_review_external_delegation`
     yourself and only report "accepted" after that call returns `status: accepted`.
11. A message starting with `[Agent360 A2A question for you …]` means a sub-agent is waiting on you: answer it with
    the reply tool using the task_id given, then stop.
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
           "approved_write": bool(t["approved"]), "protocol": PROTOCOL_VERSION,
           "conversation": conversation_of(t)}
    if feedback:
        msg["context"] = (msg["context"] + f"\n\nPREVIOUS ATTEMPT REJECTED. Reviewer feedback: {feedback}").strip()
    msg["body"] = f"[{MSG_REQUEST}] {t['correlation_id']} attempt {t['attempt']}"
    txn = f"{t['correlation_id']}-{t['attempt']}"
    # same txn_id => idempotent on the homeserver; retried across direct/public paths with backoff
    eid = send_reliable(MX.send, room, redact(msg), txn, budget_seconds=float(os.environ.get("A2A_SEND_BUDGET", "45")))
    cur = L.task(t["id"])
    if cur["status"] not in ("policy_checked", "retry_queued"):
        # child events already advanced the task while the PUT was in flight
        L.x("UPDATE external_delegation_tasks SET transport_message_id=?, transport_room_id=? WHERE id=?",
            (eid, room, t["id"]))
        return L.task(t["id"])
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
        "evidence": [], "self_check": [], "created_at": now_iso(), "updated_at": now_iso(),
        "protocol": PROTOCOL_VERSION, "conversation": {
            "max_turns": int((body.get("conversation") or {}).get("max_turns", CFG["max_turns"])),
            "max_questions": int((body.get("conversation") or {}).get("max_questions", CFG["max_questions"])),
            "allow_questions": bool((body.get("conversation") or {}).get("allow_questions", True)),
            "question_timeout_seconds": int((body.get("conversation") or {}).get("question_timeout_seconds", CFG["question_timeout"])),
            # read-only work proceeds on its stated assumption; write work fails closed (design §15 Q2)
            "on_question_timeout": (body.get("conversation") or {}).get("on_question_timeout")
                                   or ("fail" if req["write_intent"] else "proceed")}})
    if t["status"] != "cancelled":
        L.add_turn(cid, 1, "parent", "request", req["objective"], turn_no=1, message_id=f"{cid}:1:parent:request",
                   author_ref=req["parent_agent_ref"])
    if not ok:
        return L.transition(t["id"], "cancelled", acceptance={"decision": "policy_denied", "reason": reason}), False
    L.transition(t["id"], "policy_checked")
    try:
        return send_request(L.task(t["id"])), False
    except Exception as e:  # noqa: BLE001
        log.exception("send failed")
        cur = L.task(t["id"])
        if cur["status"] != "policy_checked":  # child already reported progress => it was delivered
            return cur, False
        return L.transition(t["id"], "delivery_failed", summary=redact(str(e))[:500]), False


def apply_event(content, source):
    typ = content.get("type")
    cid = content.get("correlation_id")
    t = L.task(cid) if cid else None
    if not t or typ not in (MSG_STATUS, MSG_RESULT, MSG_MESSAGE):
        return "ignored"
    eid = content.get("event_id") or f"{cid}:{typ}:{content.get('status')}:{content.get('updated_at')}"
    if not L.record_event(eid, cid, typ, source, content):
        return "duplicate"
    if content.get("child_agent_ref") and content["child_agent_ref"] != t["child_agent_ref"]:
        return "identity_mismatch"
    if t["status"] in ("policy_checked", "delivery_failed"):
        # Send still in flight or timed out client-side, but the bound child (identity checked above) is
        # already reporting on this correlation_id: it was delivered.
        L.transition(t["id"], "sent", summary=None)
        t = L.task(t["id"])
    if typ == MSG_MESSAGE:
        return apply_child_message(t, content)
    if t["status"] in TERMINAL or t["status"] in ("completed", "under_review"):
        return "late_event"
    st = content.get("status")
    try:
        if typ == MSG_STATUS and st in ("delivered", "running"):
            if t["status"] == "sent" and st == "running":
                L.transition(t["id"], "delivered")
            if not (t["status"] == "awaiting_parent" and st == "delivered"):
                L.transition(t["id"], st)
        elif typ == MSG_RESULT:
            if st == "completed":
                L.add_turn(t["correlation_id"], t["attempt"], "child", "result", content.get("output") or content.get("summary") or "",
                           message_id=f"{t['correlation_id']}:a{t['attempt']}:result:{content.get('event_id') or now_iso()}",
                           author_ref=t["child_agent_ref"])
                bump("turns_total")
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


# ------------------------------------------------------------ v2 multi-turn conversation
def conversation_of(t):
    c = t.get("conversation") or {}
    return {"max_turns": c.get("max_turns", CFG["max_turns"]), "max_questions": c.get("max_questions", CFG["max_questions"]),
            "allow_questions": c.get("allow_questions", True),
            "question_timeout_seconds": c.get("question_timeout_seconds", CFG["question_timeout"]),
            "on_question_timeout": c.get("on_question_timeout", "proceed"),
            "turn_count": t.get("turn_count") or 0, "question_count": t.get("question_count") or 0}


def _iso_in(seconds):
    return (datetime.now(timezone.utc) + timedelta(seconds=seconds)).strftime("%Y-%m-%dT%H:%M:%SZ")


def apply_child_message(t, content):
    """Child -> parent turn (question / approval_request / progress). Identity already verified by caller."""
    kind = content.get("kind")
    if kind not in ("question", "approval_request", "progress"):
        return "ignored"
    if t["status"] in TERMINAL:
        return "late_event"
    conv = conversation_of(t)
    turn, created = L.add_turn(t["correlation_id"], t["attempt"], "child", kind, content.get("text", ""),
                               message_id=content.get("message_id"), options=content.get("options"),
                               blocking=bool(content.get("blocking")) and kind != "progress",
                               in_reply_to=content.get("in_reply_to"), author_ref=t["child_agent_ref"])
    if not created:
        return "duplicate"
    bump("turns_total")
    if kind == "progress" or not turn["blocking"]:
        return "applied"
    bump("questions_total")
    now = datetime.now(timezone.utc)
    # deadline is paused while waiting: remember remaining budget, restore it on answer
    remaining = None
    if t.get("deadline_at"):
        remaining = max(60, int((_ts(t["deadline_at"]) - now).total_seconds()))
    L.transition(t["id"], "awaiting_parent", awaiting_since=now_iso(),
                 question_deadline_at=_iso_in(conv["question_timeout_seconds"]),
                 deadline_at=_iso_in(10 * 365 * 86400) if remaining else t.get("deadline_at"),
                 summary=f"awaiting parent: {turn['text'][:160]}")
    L.kv_set(f"deadline_budget:{t['correlation_id']}", str(remaining or ""))
    if CFG["wake_parent"]:
        threading.Thread(target=wake_parent_later, args=(t["correlation_id"], turn["turn_no"]), daemon=True).start()
    return "applied"


def _send_turn_to_child(t, turn, extra=None):
    room = t.get("transport_room_id") or binding_room(L.binding(t["parent_agent_ref"], t["child_agent_ref"]))
    msg = {"type": MSG_MESSAGE, "correlation_id": t["correlation_id"], "task_id": t["id"],
           "child_agent_ref": t["child_agent_ref"], "parent_agent_ref": t["parent_agent_ref"],
           "attempt": t["attempt"], "turn_no": turn["turn_no"], "message_id": turn["message_id"],
           "in_reply_to": turn.get("in_reply_to"), "role": turn["role"], "kind": turn["kind"], "text": turn["text"],
           "conversation": conversation_of(L.task(t["id"])), "body": f"[{turn['kind']}] {t['correlation_id']} #{turn['turn_no']}",
           **(extra or {})}
    eid = send_reliable(MX.send, room, redact(msg), turn["message_id"].replace(":", "_"),
                        budget_seconds=float(os.environ.get("A2A_SEND_BUDGET", "45")))
    L.x("UPDATE a2a_turns SET delivered_at=? WHERE message_id=?", (now_iso(), turn["message_id"]))
    return eid


def post_message(task_id, body, author="parent"):
    """Parent/operator -> child turn: answer | followup | redirect | approval."""
    t = L.task(task_id)
    if not t:
        raise KeyError(task_id)
    kind = (body or {}).get("kind", "answer")
    text = str((body or {}).get("text") or "").strip()
    if kind not in ("answer", "followup", "redirect", "approval", "escalation"):
        raise ValueError(f"unsupported kind {kind}")
    if not text and kind != "approval":
        raise ValueError("text required")
    role = "operator" if author.startswith("operator") else "parent"
    client_mid = (body or {}).get("message_id")
    if client_mid:  # idempotent retry of an already-applied turn (MCP retries on 5xx with the same id)
        ex = L.q("SELECT * FROM a2a_turns WHERE message_id=?", (f"{t['correlation_id']}:{role}:{client_mid}",))
        if ex:
            return {"duplicate": True, "turn": L._turn_row(ex[0]), "task": t}
    if kind == "approval" and role != "operator":
        return {"error": "policy_denied", "reason": "write approval mid-task can only be granted by a human operator"}
    conv = conversation_of(t)
    if conv["turn_count"] >= conv["max_turns"] and kind != "approval":
        raise ValueError(f"turn limit {conv['max_turns']} reached; review the result or cancel")
    if L.thread_chars(t["correlation_id"]) + len(text) > MAX_THREAD_TEXT:
        raise ValueError("thread size limit reached")
    # a parent turn must not smuggle write work into a read-only task (same rule as the request)
    if kind in ("followup", "redirect") and not t["approved"] and WRITE_WORDS.search(text) and \
            "read-only" not in text.lower() and "read only" not in text.lower():
        return {"error": "policy_denied", "reason": "follow-up introduces a write action; needs approved=true from a human operator",
                "hint": "Re-phrase as read-only, or ask an operator to approve."}
    pending = L.open_question(t["correlation_id"])
    if kind == "escalation":
        # The parent agent cannot answer safely: keep the question open for a human operator (no child turn).
        if t["status"] != "awaiting_parent" or not pending:
            raise ValueError(f"no open question (status {t['status']})")
        turn, created = L.add_turn(t["correlation_id"], t["attempt"], role, "escalation", text or "Needs an operator decision.",
                                   in_reply_to=pending["turn_no"], author_ref=author,
                                   message_id=f"{t['correlation_id']}:{role}:{client_mid}" if client_mid else None)
        if created:
            bump("turns_total"); bump("escalations")
            L.x("UPDATE external_delegation_tasks SET summary=? WHERE id=?",
                (f"escalated to operator: {pending['text'][:140]}", t["id"]))
        return {"turn": turn, "task": L.task(task_id), "escalated": True}
    if kind in ("answer", "approval"):
        if t["status"] != "awaiting_parent" or not pending:
            raise ValueError(f"no open question (status {t['status']})")
        in_reply_to = pending["turn_no"]
    elif kind == "followup":
        if t["status"] != "under_review":
            raise ValueError(f"follow-up requires under_review, got {t['status']}")
        in_reply_to = None
    else:  # redirect: change course while running/awaiting
        if t["status"] not in ("running", "delivered", "awaiting_parent", "sent"):
            raise ValueError(f"redirect requires an active task, got {t['status']}")
        in_reply_to = pending["turn_no"] if pending else None
    if kind == "approval":
        L.x("UPDATE external_delegation_tasks SET approved=1 WHERE id=?", (t["id"],))
        text = text or "Approved by operator."
    cid = t["correlation_id"]
    turn, created = L.add_turn(cid, t["attempt"], role, kind, text, in_reply_to=in_reply_to,
                               message_id=f"{cid}:{role}:{client_mid}" if client_mid else None,
                               author_ref=author)
    if not created:
        return {"duplicate": True, "turn": turn, "task": L.task(task_id)}
    bump("turns_total"); bump("answers_total" if kind in ("answer", "approval") else "followups_total")
    if t["status"] in ("awaiting_parent", "under_review"):
        budget = L.kv_get(f"deadline_budget:{cid}") or ""
        timeout = (L.binding(t["parent_agent_ref"], t["child_agent_ref"]) or {}).get("timeout_seconds", 900)
        L.transition(t["id"], "running", awaiting_since=None, question_deadline_at=None, output=None if kind == "followup" else t.get("output"),
                     deadline_at=_iso_in(int(budget) if budget else timeout), summary=f"{kind} from {author}")
    _send_turn_to_child(L.task(task_id), turn, {"approved_write": bool(L.task(task_id)["approved"])})
    return {"turn": turn, "task": L.task(task_id)}


def wake_parent_later(cid, turn_no):
    """If nobody answers a blocking question within wake_after seconds (a parent MCP waiting with wait_seconds
    answers immediately), start a parent turn so an idle parent agent learns about it (design §8 fallback)."""
    time.sleep(CFG["wake_after"])
    try:
        t = L.task(cid)
        q = L.open_question(cid)
        if not t or t["status"] != "awaiting_parent" or not q or q["turn_no"] != turn_no:
            return
        inst, parent = parse_agent_ref(t["parent_agent_ref"])
        if not CFG["qwenpaw"].get(inst):
            return
        prompt = (f"[Agent360 A2A question for you | correlation_id={cid} | task_id={t['id']}]\n"
                  f"Your external sub-agent {t['child_agent_ref']} is waiting for your answer on the delegation "
                  f"\"{(t.get('objective') or '')[:200]}\".\nQuestion: {q['text']}\n"
                  + (f"Options: {', '.join(map(str, q['options']))}\n" if q.get("options") else "")
                  + "Answer it now with the tool agent360_reply_external_delegation (task_id above) using facts you know. "
                    "If you cannot decide safely (needs a human decision, a secret, or a write approval), call the same tool "
                    "with escalate_to_operator=true and a one-line reason: the question then waits for a human in Agent360.")
        body = {"input": [{"role": "user", "content": [{"type": "text", "text": prompt}]}],
                "session_id": f"a2a-parent-{cid}", "user_id": "agent360-a2a", "channel": "console", "stream": True}
        with qp_open(inst, "/api/console/chat", "POST", body, {"Content-Type": "application/json", "X-Agent-Id": parent},
                     timeout=600) as resp:
            for _ in resp:  # drain the stream; the parent answers through the MCP tool
                pass
        bump("parent_wakeups")
        log.info("woke parent %s for %s turn %s", parent, cid, turn_no)
    except Exception as e:  # noqa: BLE001
        bump("parent_wakeup_errors")
        log.warning("wake parent %s: %s", cid, redact(str(e))[:200])


def inbox(parent_ref):
    out = []
    for t in L.q("SELECT id FROM external_delegation_tasks WHERE parent_agent_ref=? AND status='awaiting_parent' "
                 "ORDER BY updated_at DESC LIMIT 100", (parent_ref,)):
        task = L.task(t["id"])
        q = L.open_question(task["correlation_id"])
        if q:
            out.append({"task_id": task["id"], "correlation_id": task["correlation_id"], "child_agent_ref": task["child_agent_ref"],
                        "objective": task["objective"], "question": q, "awaiting_since": task.get("awaiting_since"),
                        "question_deadline_at": task.get("question_deadline_at")})
    return out


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
    backoff = 1
    while True:
        try:
            s = MX.sync(since, timeout_ms=int(os.environ.get("A2A_SYNC_TIMEOUT_MS", "20000")))
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
                                WORKER_PROTOCOL[child["agent_ref"]] = int(content.get("protocol") or 1)
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
            backoff = 1
        except Exception as e:  # noqa: BLE001
            bump("matrix_sync_errors")
            log.warning("matrix sync: %s", redact(str(e))[:200])
            time.sleep(backoff)
            backoff = min(backoff * 2, 15)


def question_timeout(t):
    bump("question_timeouts")
    if conversation_of(t)["on_question_timeout"] == "fail" or t["write_intent"]:
        L.add_turn(t["correlation_id"], t["attempt"], "system", "system", "No answer before the question deadline; task stopped.")
        L.transition(t["id"], "timed_out", summary="question unanswered before deadline")
        return
    turn, _ = L.add_turn(t["correlation_id"], t["attempt"], "system", "system",
                         "No answer before the question deadline. Proceed with your best stated assumption and finish.")
    budget = L.kv_get(f"deadline_budget:{t['correlation_id']}") or ""
    L.transition(t["id"], "running", awaiting_since=None, question_deadline_at=None,
                 deadline_at=_iso_in(int(budget) if budget else 900), summary="question timed out; proceeding")
    try:
        _send_turn_to_child(L.task(t["id"]), turn)
    except Exception as e:  # noqa: BLE001
        log.warning("question timeout notify %s: %s", t["correlation_id"], e)


def sweeper_loop():
    while True:
        try:
            now = datetime.now(timezone.utc)
            for t in L.tasks(limit=500):
                if t["status"] == "awaiting_parent" and t.get("question_deadline_at") and _ts(t["question_deadline_at"]) < now:
                    question_timeout(t)
                    continue
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
        "conversation": conversation_metrics(since),
        "worker_protocol": dict(WORKER_PROTOCOL),
        "matrix_transport": {"origin_pinned": bool(getattr(MX, "origin_ip", None)), **dict(getattr(MX, "stats", {}) or {})},
        "agentteams_registry": registry_status(),
    }


def conversation_metrics(since):
    rows = L.q("SELECT turn_count, question_count FROM external_delegation_tasks WHERE created_at >= ?", (since,))
    waits = []
    for q in L.q("SELECT q.correlation_id, q.turn_no, q.created_at qa, a.created_at aa FROM a2a_turns q JOIN a2a_turns a "
                 "ON a.correlation_id=q.correlation_id AND a.in_reply_to=q.turn_no WHERE q.kind IN ('question','approval_request') "
                 "AND q.created_at >= ?", (since,)):
        waits.append((_ts(q["aa"]) - _ts(q["qa"])).total_seconds())
    waits.sort()
    return {"multi_turn_tasks": sum(1 for r in rows if (r["question_count"] or 0) > 0 or (r["turn_count"] or 0) > 2),
            "awaiting_parent_now": L.q("SELECT COUNT(*) n FROM external_delegation_tasks WHERE status='awaiting_parent'")[0]["n"],
            "question_answer_seconds": {"p50": waits[len(waits) // 2] if waits else None,
                                        "p95": waits[int(len(waits) * 0.95)] if waits else None}}


def registry_status():
    """AgentTeams drift detector: the exported registry must be fresh and every worker/edge identity
    must still carry a Matrix user (an empty matrixUserID means the AppService registration broke)."""
    path = CFG["registry_file"]
    out = {"file": path, "age_seconds": None, "stale": False, "missing_matrix_identity": []}
    if not path or not os.path.exists(path):
        return out
    out["age_seconds"] = int(time.time() - os.path.getmtime(path))
    out["stale"] = out["age_seconds"] > CFG["registry_stale_after"]
    try:
        reg = json.load(open(path))
        out["missing_matrix_identity"] = sorted(a["agent_ref"] for a in reg if not a.get("matrix_user_id"))
        for a in reg:  # keep directory identities in sync with the latest export
            if a.get("matrix_user_id"):
                L.upsert_agent(a)
                if a.get("health") == "online":
                    L.set_health(a["agent_ref"], "online")
        audit_path = os.path.join(os.path.dirname(path), "room-audit.json")
        if os.path.exists(audit_path):
            out["room_audit"] = json.load(open(audit_path))
    except Exception as e:  # noqa: BLE001
        out["error"] = str(e)[:200]
    return out


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
    stale_q = L.q("SELECT COUNT(*) n FROM external_delegation_tasks WHERE status='awaiting_parent' AND question_deadline_at < ?",
                  (now_iso(),))[0]["n"]
    if stale_q:
        reasons.append(f"questions_past_deadline:{stale_q}")
    if m["delegations_total"] >= 3 and m["timeout_rate"] > 0.3:
        reasons.append(f"timeout_rate_high:{m['timeout_rate']}")
    if m["counters"].get("callback_bad_signature", 0) >= 5:
        reasons.append("callback_signature_failures")
    reg = registry_status()
    if reg["stale"]:
        reasons.append(f"agentteams_registry_stale:{reg['age_seconds']}")
    for ref in reg["missing_matrix_identity"]:
        reasons.append(f"agentteams_identity_missing:{ref}")
    ra = reg.get("room_audit") or {}
    if ra.get("exceeded"):
        reasons.append(f"matrix_room_spam_exceeded:{ra.get('max_joined_rooms')}>{ra.get('threshold')}")
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


@route("GET", r"/api/v1/a2a/delegations/(?P<tid>[^/]+)/messages")
def r_messages(h, m, b):
    t = L.task(m["tid"])
    if not t:
        raise KeyError(m["tid"])
    qs = urllib.parse.parse_qs(urllib.parse.urlsplit(h.path).query)
    after = int((qs.get("after") or ["0"])[0])
    wait = min(int((qs.get("wait") or ["0"])[0]), 25)
    end = time.time() + wait
    turns = L.turns(t["correlation_id"], after)
    while not turns and time.time() < end:
        time.sleep(0.5)
        turns = L.turns(t["correlation_id"], after)
    t = L.task(m["tid"])
    return 200, {"task_id": t["id"], "correlation_id": t["correlation_id"], "status": t["status"],
                 "conversation": conversation_of(t), "pending_question": L.open_question(t["correlation_id"]), "turns": turns}


@route("POST", r"/api/v1/a2a/delegations/(?P<tid>[^/]+)/messages")
def r_post_message(h, m, b):
    b = b or {}
    author = b.get("author") or "parent"
    if author.startswith("operator") and not _is_operator(h):
        return 403, {"error": "operator token required for operator turns"}
    out = post_message(m["tid"], b, author)
    return (403 if out.get("error") == "policy_denied" else 200 if out.get("duplicate") else 201), out


@route("GET", r"/api/v1/agents/(?P<aid>[^/]+)/a2a/inbox")
def r_inbox(h, m, b):
    return 200, inbox(to_ref(m["aid"]))


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


def _is_operator(h):
    tok = CFG["operator_token"]
    given = (h.headers.get("Authorization", "") or "").removeprefix("Bearer ").strip()
    return (not tok and not CFG["service_token"]) or (bool(tok) and hmac.compare_digest(given, tok))


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
