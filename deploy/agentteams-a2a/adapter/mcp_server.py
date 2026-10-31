"""MCP server `agent360-a2a` (stdio, JSON-RPC 2.0) for parent agents.

Tools: agent360_list_external_subagents, agent360_delegate_external, agent360_check_external_delegation,
agent360_review_external_delegation, agent360_reply_external_delegation, agent360_followup_external_delegation,
agent360_list_external_questions (v2 multi-turn: the child may ask questions mid-task).
Env: A2A_API_URL (control plane), A2A_SERVICE_TOKEN, A2A_PARENT_AGENT (e.g. mons or agent://core/mons),
A2A_API_ORIGIN_IP (optional: origin IP over Tailscale; TLS/SNI still verified for the A2A_API_URL host, public
DNS/CDN only as fallback).
"""
import http.client
import json
import os
import socket
import ssl
import uuid
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

API = os.environ.get("A2A_API_URL", "http://127.0.0.1:8095").rstrip("/")
ORIGIN_IP = os.environ.get("A2A_API_ORIGIN_IP", "").strip() or None
TRANSPORT = {"internal_ok": 0, "public_ok": 0, "internal_fail": 0}


class _PinnedHTTPS(http.client.HTTPSConnection):
    def __init__(self, host, port, ip, timeout):
        super().__init__(host, port, timeout=timeout, context=ssl.create_default_context())
        self._pin_ip = ip

    def connect(self):
        sock = socket.create_connection((self._pin_ip, self.port), self.timeout)
        self.sock = self._context.wrap_socket(sock, server_hostname=self.host)


def _internal_request(method, url, data, headers, timeout):
    """Same request straight to the origin over the tailnet (no Cloudflare). Raises on transport errors."""
    u = urllib.parse.urlsplit(url)
    port = u.port or (443 if u.scheme == "https" else 80)
    if u.scheme == "https":
        c = _PinnedHTTPS(u.hostname, port, ORIGIN_IP, timeout)
    else:
        c = http.client.HTTPConnection(ORIGIN_IP, port, timeout=timeout)
    try:
        c.request(method, u.path + (("?" + u.query) if u.query else ""), body=data, headers={**headers, "Host": u.netloc})
        r = c.getresponse()
        return r.status, r.read()
    finally:
        c.close()
TOKEN = os.environ.get("A2A_SERVICE_TOKEN", "")
PARENT = os.environ.get("A2A_PARENT_AGENT", "mons")


def api(method, path, body=None, timeout=60, retries=True):
    h = {"Content-Type": "application/json", "User-Agent": "Mozilla/5.0 (compatible; agent360-a2a-mcp/1.0)"}
    if TOKEN:
        h["Authorization"] = f"Bearer {TOKEN}"
    if API.endswith("/api") and path.startswith("/api/v1/"):
        path = path[len("/api/v1"):]  # public Agent360 proxy exposes /api/<route>
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(API + path, data=data, headers=h, method=method)
    # ~50 s: rides out an Agent360 backend/nginx restart. Startup/handshake calls pass retries=False: the MCP
    # host times out the connect after ~30 s and drops the whole client if tools/list blocks on retries.
    delays = (2, 4, 8, 15, 20) if retries else ()
    for attempt in range(len(delays) + 1):
        if ORIGIN_IP:
            try:
                status, raw = _internal_request(method, API + path, data, h, timeout)
                TRANSPORT["internal_ok"] += 1
                if status < 400:
                    return json.loads(raw or b"{}")
                if status in (500, 502, 503, 504) and attempt < len(delays):
                    time.sleep(delays[attempt])
                    continue
                try:
                    return {"error": json.loads(raw), "http_status": status}
                except Exception:
                    return {"error": f"HTTP {status}", "http_status": status}
            except (OSError, http.client.HTTPException):
                TRANSPORT["internal_fail"] += 1  # tailnet path down: fall through to the public URL once
        try:
            with urllib.request.urlopen(r, timeout=timeout) as resp:
                TRANSPORT["public_ok"] += 1
                return json.loads(resp.read() or b"{}")
        except urllib.error.HTTPError as e:
            # 502/503/504/52x = Agent360 proxy redeploying. Safe to retry: GETs are read-only and delegate POSTs
            # carry a client correlation_id (deduplicated server-side); review on a final task is rejected.
            if e.code in (500, 502, 503, 504, 520, 521, 522, 523, 524) and attempt < len(delays):
                time.sleep(delays[attempt])
                continue
            try:
                return {"error": json.loads(e.read()), "http_status": e.code}
            except Exception:
                return {"error": f"HTTP {e.code}", "http_status": e.code}
        except (urllib.error.URLError, TimeoutError) as e:
            if attempt < len(delays):
                time.sleep(delays[attempt])
                continue
            return {"error": f"{type(e).__name__}: {e}", "http_status": None}


def compact(t):
    keys = ["id", "correlation_id", "child_agent_ref", "status", "attempt", "summary", "output", "evidence",
            "self_check", "acceptance", "acceptance_criteria", "deadline_at", "transport_message_id", "child_session_id",
            "pending_question", "turns", "conversation", "next_step"]
    return {k: t.get(k) for k in keys if k in t}


TOOLS = [
    {"name": "agent360_list_external_subagents",
     "description": "List external sub-agents (other QwenPaw instances via AgentTeams) bound to this agent, with health and allowed scope.",
     "inputSchema": {"type": "object", "properties": {"probe": {"type": "boolean", "description": "Run a live health probe first"}}}},
    {"name": "agent360_delegate_external",
     "description": "Delegate a task to an external sub-agent through Agent360 + AgentTeams. Returns task_id/correlation_id; with wait_seconds it waits for the result OR returns early with `pending_question` when the sub-agent asks you something (answer with agent360_reply_external_delegation).",
     "inputSchema": {"type": "object", "required": ["child_agent_ref", "objective", "acceptance_criteria"], "properties": {
         "child_agent_ref": {"type": "string", "description": "e.g. agent://gm/default (must be bound)"},
         "objective": {"type": "string"}, "context": {"type": "string"},
         "constraints": {"type": "array", "items": {"type": "string"}},
         "expected_output": {"type": "string"},
         "acceptance_criteria": {"type": "array", "items": {"type": "string"}, "minItems": 1},
         "capabilities": {"type": "array", "items": {"type": "string"}},
         "approved": {"type": "boolean", "description": "Only true when a human approved a write action"},
         "allow_questions": {"type": "boolean", "description": "Let the sub-agent ask you clarifying questions (default true)"},
         "wait_seconds": {"type": "integer", "minimum": 0, "maximum": 600}}}},
    {"name": "agent360_reply_external_delegation",
     "description": "Answer the open question of a delegation (status awaiting_parent). Optionally waits for the next question or the result.",
     "inputSchema": {"type": "object", "required": ["task_id", "text"], "properties": {
         "task_id": {"type": "string"}, "text": {"type": "string", "description": "Your answer; be specific (or the escalation reason)"},
         "escalate_to_operator": {"type": "boolean", "description": "true when you cannot answer safely (human decision, secret, write approval): the question waits for a human operator instead"},
         "wait_seconds": {"type": "integer", "minimum": 0, "maximum": 600}}}},
    {"name": "agent360_followup_external_delegation",
     "description": "Ask a follow-up about a delegation result (status under_review). The sub-agent keeps the full thread context; this is not a reject.",
     "inputSchema": {"type": "object", "required": ["task_id", "text"], "properties": {
         "task_id": {"type": "string"}, "text": {"type": "string"},
         "wait_seconds": {"type": "integer", "minimum": 0, "maximum": 600}}}},
    {"name": "agent360_list_external_questions",
     "description": "List open questions your external sub-agents are waiting on you to answer.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "agent360_check_external_delegation",
     "description": "Get status, output, evidence and self-check of an external delegation.",
     "inputSchema": {"type": "object", "required": ["task_id"], "properties": {
         "task_id": {"type": "string"}, "wait_seconds": {"type": "integer", "minimum": 0, "maximum": 600}}}},
    {"name": "agent360_review_external_delegation",
     "description": "Accept or reject a completed delegation. Reject requires feedback and retries once automatically.",
     "inputSchema": {"type": "object", "required": ["task_id", "decision"], "properties": {
         "task_id": {"type": "string"}, "decision": {"type": "string", "enum": ["accept", "reject"]},
         "feedback": {"type": "string"}}}},
]


def wait(task_id, seconds):
    """Poll until the delegation leaves the active states. Transient proxy errors (502/504/524 during an Agent360
    redeploy) are retried; the last good snapshot is returned so the caller never loses the task id."""
    end = time.time() + seconds
    last = None
    while True:
        t = api("GET", f"/api/v1/a2a/delegations/{urllib.parse.quote(task_id)}")
        if "error" not in t:
            last = t
            if t.get("status") not in ("draft", "policy_checked", "sent", "delivered", "running", "retry_queued"):
                return with_thread(t)
        elif t.get("http_status") not in (502, 503, 504, 520, 521, 522, 523, 524, None):
            return t
        if time.time() >= end:
            return last or t
        time.sleep(3)


def with_thread(t):
    """Attach the open question (and recent turns) so the model sees what the sub-agent asked."""
    if "error" in t:
        return t
    if t.get("status") == "awaiting_parent" or (t.get("turn_count") or 0) > 1:
        th = api("GET", f"/api/v1/a2a/delegations/{urllib.parse.quote(t['id'])}/messages")
        if "error" not in th:
            t = {**t, "pending_question": th.get("pending_question"),
                 "turns": [{k: x.get(k) for k in ("turn_no", "role", "kind", "text", "options")} for x in th.get("turns", [])][-8:],
                 "conversation": th.get("conversation")}
            if t["pending_question"]:
                t["next_step"] = ("The sub-agent is waiting for your answer: call agent360_reply_external_delegation "
                                  "with this task_id. If you cannot decide safely, say so; an operator can answer in Agent360.")
    if t.get("status") == "under_review" and not t.get("next_step"):
        # The result is NOT accepted yet: the model must call review explicitly (never claim acceptance).
        t = {**t, "next_step": "Result received but NOT reviewed yet. Check it against acceptance_criteria, then call "
                               "agent360_review_external_delegation (accept or reject). Do not say 'accepted' before that call succeeds."}
    return t


def allowed_scope(child_ref):
    """Capabilities the binding allows for this child (empty list = any)."""
    binds = api("GET", f"/api/v1/agents/{urllib.parse.quote(PARENT, safe='')}/external-subagents")
    for b in binds if isinstance(binds, list) else []:
        if b.get("child_agent_ref") == child_ref:
            return b.get("allowed_capabilities") or []
    return []


def tools_list():
    """Tool schemas with the live capability allowlist so the model cannot invent scope names."""
    tools = json.loads(json.dumps(TOOLS))
    try:
        binds = api("GET", f"/api/v1/agents/{urllib.parse.quote(PARENT, safe='')}/external-subagents", timeout=8, retries=False)
        refs = [b["child_agent_ref"] for b in binds] if isinstance(binds, list) else []
        caps = sorted({c for b in (binds if isinstance(binds, list) else []) for c in (b.get("allowed_capabilities") or [])})
    except Exception:  # noqa: BLE001
        refs, caps = [], []
    props = tools[1]["inputSchema"]["properties"]
    if refs:
        props["child_agent_ref"]["enum"] = refs
    if caps:
        props["capabilities"]["items"]["enum"] = caps
        props["capabilities"]["description"] = ("Optional. Only values from the binding allowlist: " + ", ".join(caps)
                                                + ". Omit when unsure; do not invent names.")
    return tools


def call_tool(name, a):
    if name == "agent360_list_external_subagents":
        binds = api("GET", f"/api/v1/agents/{urllib.parse.quote(PARENT, safe='')}/external-subagents")
        if a.get("probe") and isinstance(binds, list):
            for b in binds:
                b["probe"] = api("POST", f"/api/v1/a2a/external-agents/{urllib.parse.quote(b['child_agent_ref'], safe='')}/probe")
        return binds
    if name == "agent360_delegate_external":
        body = {k: a[k] for k in ("child_agent_ref", "objective", "context", "constraints", "expected_output",
                                  "acceptance_criteria", "capabilities", "approved") if k in a}
        if "allow_questions" in a:
            body["conversation"] = {"allow_questions": bool(a["allow_questions"])}
        body["parent_agent_ref"] = PARENT
        # Client-generated id makes the POST idempotent (control plane returns the existing task on retry).
        body["correlation_id"] = "a2a_" + uuid.uuid4().hex[:21]
        # Drop capability names outside the binding allowlist instead of failing the whole task:
        # the allowlist is enforced server-side, the LLM only labels intent.
        allowed = allowed_scope(a.get("child_agent_ref", ""))
        if allowed and body.get("capabilities"):
            dropped = [c for c in body["capabilities"] if c not in allowed]
            body["capabilities"] = [c for c in body["capabilities"] if c in allowed]
        else:
            dropped = []
        t = api("POST", "/api/v1/a2a/delegations", body)
        if "error" in t:
            return t
        if (t.get("acceptance") or {}).get("decision") == "policy_denied":
            return {**compact(t), "policy_denied": t["acceptance"].get("reason"), "allowed_capabilities": allowed,
                    "hint": "Fix the request (scope, read-only constraint, or human approval) and call again."}
        if a.get("wait_seconds"):
            t = wait(t["id"], min(int(a["wait_seconds"]), 600)) or t
        out = compact(with_thread(t)) if "error" not in t else {**t, "hint": "Delegation was created; poll it with agent360_check_external_delegation."}
        if dropped:
            out["ignored_capabilities"] = dropped
        return out
    if name == "agent360_check_external_delegation":
        tid = a["task_id"]
        t = wait(tid, min(int(a.get("wait_seconds") or 0), 600)) if a.get("wait_seconds") else \
            api("GET", f"/api/v1/a2a/delegations/{urllib.parse.quote(tid)}")
        return compact(with_thread(t)) if "error" not in t else t
    if name in ("agent360_reply_external_delegation", "agent360_followup_external_delegation"):
        kind = "answer" if name == "agent360_reply_external_delegation" else "followup"
        if kind == "answer" and a.get("escalate_to_operator"):
            kind = "escalation"
        r = api("POST", f"/api/v1/a2a/delegations/{urllib.parse.quote(a['task_id'])}/messages",
                {"kind": kind, "text": a["text"], "author": f"agent:{PARENT}", "message_id": uuid.uuid4().hex[:16]})
        if "error" in r:
            return {**r, "hint": "Check the status with agent360_check_external_delegation; answer only when awaiting_parent, "
                                 "follow up only when under_review."}
        t = r.get("task") or {}
        if kind == "escalation":
            return {**compact(with_thread(t)), "escalated": True,
                    "next_step": "Question handed to a human operator in Agent360; tell the user what is being asked."}
        if a.get("wait_seconds") and t.get("id"):
            time.sleep(2)
            t = wait(t["id"], min(int(a["wait_seconds"]), 600)) or t
        return compact(with_thread(t))
    if name == "agent360_list_external_questions":
        return api("GET", f"/api/v1/agents/{urllib.parse.quote(PARENT, safe='')}/a2a/inbox")
    if name == "agent360_review_external_delegation":
        t = api("POST", f"/api/v1/a2a/delegations/{urllib.parse.quote(a['task_id'])}/review",
                {"decision": a["decision"], "feedback": a.get("feedback"), "reviewer": f"agent:{PARENT}"})
        # Never compact an error away: an empty `{}` reads as success to the model.
        if "error" in t or t.get("status") not in ("accepted", "rejected", "sent", "running", "under_review"):
            return {"error": t.get("error") or "review not applied", "http_status": t.get("http_status"),
                    "hint": "Use the exact id or correlation_id returned by delegate/check; review only applied when status is accepted/rejected."}
        return compact(t)
    raise ValueError(f"unknown tool {name}")


def handle(msg):
    mid, method, p = msg.get("id"), msg.get("method"), msg.get("params") or {}
    if method == "initialize":
        return {"protocolVersion": p.get("protocolVersion", "2024-11-05"), "capabilities": {"tools": {}},
                "serverInfo": {"name": "agent360-a2a", "version": "1.0.0"}}
    if method == "tools/list":
        return {"tools": tools_list()}
    if method == "tools/call":
        try:
            res = call_tool(p.get("name"), p.get("arguments") or {})
            return {"content": [{"type": "text", "text": json.dumps(res, ensure_ascii=False, default=str)}],
                    "isError": isinstance(res, dict) and "error" in res}
        except Exception as e:  # noqa: BLE001
            return {"content": [{"type": "text", "text": f"error: {e}"}], "isError": True}
    if method in ("ping",):
        return {}
    if mid is None:
        return None
    raise ValueError(f"method not found: {method}")


def main():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError:
            continue
        try:
            res = handle(msg)
            if msg.get("id") is not None and res is not None:
                out = {"jsonrpc": "2.0", "id": msg["id"], "result": res}
            else:
                continue
        except Exception as e:  # noqa: BLE001
            out = {"jsonrpc": "2.0", "id": msg.get("id"), "error": {"code": -32601, "message": str(e)}}
        sys.stdout.write(json.dumps(out) + "\n")
        sys.stdout.flush()


if __name__ == "__main__":
    main()
