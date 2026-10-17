"""MCP server `agent360-a2a` (stdio, JSON-RPC 2.0) for parent agents.

Tools: agent360_list_external_subagents, agent360_delegate_external,
agent360_check_external_delegation, agent360_review_external_delegation.
Env: A2A_API_URL (control plane), A2A_SERVICE_TOKEN, A2A_PARENT_AGENT (e.g. mons or agent://core/mons).
"""
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

API = os.environ.get("A2A_API_URL", "http://127.0.0.1:8095").rstrip("/")
TOKEN = os.environ.get("A2A_SERVICE_TOKEN", "")
PARENT = os.environ.get("A2A_PARENT_AGENT", "mons")


def api(method, path, body=None, timeout=60):
    h = {"Content-Type": "application/json", "User-Agent": "Mozilla/5.0 (compatible; agent360-a2a-mcp/1.0)"}
    if TOKEN:
        h["Authorization"] = f"Bearer {TOKEN}"
    if API.endswith("/api") and path.startswith("/api/v1/"):
        path = path[len("/api/v1"):]  # public Agent360 proxy exposes /api/<route>
    r = urllib.request.Request(API + path, data=json.dumps(body).encode() if body is not None else None, headers=h, method=method)
    try:
        with urllib.request.urlopen(r, timeout=timeout) as resp:
            return json.loads(resp.read() or b"{}")
    except urllib.error.HTTPError as e:
        try:
            return {"error": json.loads(e.read()), "http_status": e.code}
        except Exception:
            return {"error": f"HTTP {e.code}", "http_status": e.code}


def compact(t):
    keys = ["id", "correlation_id", "child_agent_ref", "status", "attempt", "summary", "output", "evidence",
            "self_check", "acceptance", "acceptance_criteria", "deadline_at", "transport_message_id", "child_session_id"]
    return {k: t.get(k) for k in keys if k in t}


TOOLS = [
    {"name": "agent360_list_external_subagents",
     "description": "List external sub-agents (other QwenPaw instances via AgentTeams) bound to this agent, with health and allowed scope.",
     "inputSchema": {"type": "object", "properties": {"probe": {"type": "boolean", "description": "Run a live health probe first"}}}},
    {"name": "agent360_delegate_external",
     "description": "Delegate a task to an external sub-agent through Agent360 + AgentTeams. Returns task_id/correlation_id; with wait_seconds it waits for the result.",
     "inputSchema": {"type": "object", "required": ["child_agent_ref", "objective", "acceptance_criteria"], "properties": {
         "child_agent_ref": {"type": "string", "description": "e.g. agent://gm/default (must be bound)"},
         "objective": {"type": "string"}, "context": {"type": "string"},
         "constraints": {"type": "array", "items": {"type": "string"}},
         "expected_output": {"type": "string"},
         "acceptance_criteria": {"type": "array", "items": {"type": "string"}, "minItems": 1},
         "capabilities": {"type": "array", "items": {"type": "string"}},
         "approved": {"type": "boolean", "description": "Only true when a human approved a write action"},
         "wait_seconds": {"type": "integer", "minimum": 0, "maximum": 600}}}},
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
    end = time.time() + seconds
    t = api("GET", f"/api/v1/a2a/delegations/{urllib.parse.quote(task_id)}")
    while time.time() < end and t.get("status") in ("draft", "policy_checked", "sent", "delivered", "running", "retry_queued"):
        time.sleep(3)
        t = api("GET", f"/api/v1/a2a/delegations/{urllib.parse.quote(task_id)}")
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
        binds = api("GET", f"/api/v1/agents/{urllib.parse.quote(PARENT, safe='')}/external-subagents")
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
        body["parent_agent_ref"] = PARENT
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
            t = wait(t["id"], min(int(a["wait_seconds"]), 600))
        out = compact(t)
        if dropped:
            out["ignored_capabilities"] = dropped
        return out
    if name == "agent360_check_external_delegation":
        tid = a["task_id"]
        t = wait(tid, min(int(a.get("wait_seconds") or 0), 600)) if a.get("wait_seconds") else \
            api("GET", f"/api/v1/a2a/delegations/{urllib.parse.quote(tid)}")
        return compact(t) if "error" not in t else t
    if name == "agent360_review_external_delegation":
        return compact(api("POST", f"/api/v1/a2a/delegations/{urllib.parse.quote(a['task_id'])}/review",
                           {"decision": a["decision"], "feedback": a.get("feedback"), "reviewer": f"agent:{PARENT}"}))
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
