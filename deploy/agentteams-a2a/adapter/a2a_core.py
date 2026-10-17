"""Agent360 external A2A core: contracts, Matrix transport, ledger, policy.

Transport: AgentTeams v1.2.3 Matrix (Tuwunel) rooms (plan Option A).
Agent360 is the control plane + ledger; AgentTeams provides identity/routing.
Stdlib only.
"""
from __future__ import annotations

import json
import os
import re
import secrets
import sqlite3
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timedelta, timezone

MSG_REQUEST = "agent360.delegation.request.v1"
MSG_STATUS = "agent360.delegation.status.v1"
MSG_RESULT = "agent360.delegation.result.v1"
MSG_CANCEL = "agent360.delegation.cancel.v1"
EVENT_TYPE = "vn.antares.agent360.a2a"  # custom Matrix event type
MAX_INLINE_OUTPUT = 12000

ACTIVE = {"draft", "policy_checked", "sent", "delivered", "running", "retry_queued"}
TERMINAL = {"accepted", "rejected", "failed", "timed_out", "cancelled", "delivery_failed"}
TRANSITIONS = {
    "draft": {"policy_checked", "cancelled"},
    "policy_checked": {"sent", "delivery_failed", "cancelled"},
    "sent": {"delivered", "running", "completed", "failed", "delivery_failed", "timed_out", "cancelled"},
    "delivered": {"running", "completed", "failed", "timed_out", "cancelled"},
    "running": {"running", "completed", "failed", "timed_out", "cancelled"},
    "completed": {"under_review"},
    "under_review": {"accepted", "rejected"},
    "rejected": {"retry_queued"},
    "retry_queued": {"sent", "delivery_failed", "cancelled"},
}

_SECRET_PATTERNS = [
    re.compile(r"(?i)(bearer\s+)[A-Za-z0-9._\-]{12,}"),
    re.compile(r"(?i)((?:api[_-]?key|token|secret|password|passwd|access[_-]?key)\s*[\"']?\s*[:=]\s*[\"']?)[^\s\"',}]{6,}"),
    re.compile(r"\b(sk|ghp|github_pat|ol_api|ck|xox[abp])_[A-Za-z0-9_\-]{8,}"),
    re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]+?-----END [A-Z ]*PRIVATE KEY-----"),
]


def now_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def new_id(prefix: str) -> str:
    return f"{prefix}_{int(time.time()*1000):x}{secrets.token_hex(4)}"


def redact(value):
    """Recursively redact secret-looking material."""
    if isinstance(value, str):
        out = value
        for p in _SECRET_PATTERNS:
            out = p.sub(lambda m: (m.group(1) if m.lastindex else "") + "<redacted>", out)
        return out
    if isinstance(value, list):
        return [redact(v) for v in value]
    if isinstance(value, dict):
        return {k: ("<redacted>" if re.search(r"(?i)secret|password|token|api_?key|credential", k) else redact(v)) for k, v in value.items()}
    return value


def contains_secret(text: str) -> bool:
    return redact(text) != text


def parse_agent_ref(ref: str) -> tuple[str, str]:
    m = re.fullmatch(r"agent://([a-z0-9][a-z0-9_.\-]*)/([A-Za-z0-9][A-Za-z0-9_.\-]*)", ref or "")
    if not m:
        raise ValueError(f"invalid agent_ref: {ref!r}")
    return m.group(1), m.group(2)


# ---------------------------------------------------------------- Matrix
class MatrixClient:
    def __init__(self, homeserver: str, access_token: str, user_id: str, timeout: int = 30):
        self.hs = homeserver.rstrip("/")
        self.token = access_token
        self.user_id = user_id
        self.timeout = timeout

    def _call(self, method: str, path: str, body=None, query=None, timeout=None):
        url = f"{self.hs}/_matrix/client/v3{path}"
        if query:
            url += "?" + urllib.parse.urlencode(query)
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(url, data=data, method=method, headers={
            "Authorization": f"Bearer {self.token}",
            "Content-Type": "application/json",
            "User-Agent": "agent360-a2a/1.0",
        })
        try:
            with urllib.request.urlopen(req, timeout=timeout or self.timeout) as r:
                return json.loads(r.read() or b"{}")
        except urllib.error.HTTPError as e:
            raw = e.read().decode("utf-8", "ignore")[:400]
            raise RuntimeError(f"matrix {method} {path} -> {e.code}: {raw}") from None

    def whoami(self):
        return self._call("GET", "/account/whoami")

    def resolve_alias(self, alias: str):
        try:
            return self._call("GET", "/directory/room/" + urllib.parse.quote(alias))["room_id"]
        except RuntimeError:
            return None

    def create_room(self, alias_local: str, name: str, invite: list[str]):
        return self._call("POST", "/createRoom", {
            "preset": "private_chat", "room_alias_name": alias_local, "name": name,
            "invite": invite, "is_direct": False,
            "topic": "Agent360 external A2A delegation channel (machine-to-machine)",
        })["room_id"]

    def invite(self, room_id: str, user_id: str):
        try:
            self._call("POST", f"/rooms/{urllib.parse.quote(room_id)}/invite", {"user_id": user_id})
        except RuntimeError as e:
            if "already in the room" not in str(e) and "M_FORBIDDEN" not in str(e):
                raise

    def join(self, room_id_or_alias: str):
        return self._call("POST", "/join/" + urllib.parse.quote(room_id_or_alias), {})["room_id"]

    def joined_rooms(self):
        return self._call("GET", "/joined_rooms").get("joined_rooms", [])

    def send(self, room_id: str, content: dict, txn_id: str | None = None):
        txn = txn_id or new_id("txn")
        r = self._call("PUT", f"/rooms/{urllib.parse.quote(room_id)}/send/{EVENT_TYPE}/{urllib.parse.quote(txn)}", content)
        return r["event_id"]

    def sync(self, since: str | None, timeout_ms: int = 25000, room_filter: list[str] | None = None):
        flt = {"room": {"timeline": {"limit": 50, "types": [EVENT_TYPE]},
                        "state": {"lazy_load_members": True}, "ephemeral": {"types": []}},
               "presence": {"types": []}, "account_data": {"types": []}}
        if room_filter:
            flt["room"]["rooms"] = room_filter
        q = {"timeout": str(timeout_ms), "filter": json.dumps(flt)}
        if since:
            q["since"] = since
        return self._call("GET", "/sync", query=q, timeout=timeout_ms / 1000 + 20)


def ensure_channel(mx: MatrixClient, domain: str, parent_ref: str, child_ref: str, child_mxid: str) -> str:
    """One private room per parent->child binding; idempotent via alias."""
    pi, pa = parse_agent_ref(parent_ref)
    ci, ca = parse_agent_ref(child_ref)
    local = re.sub(r"[^a-z0-9_.\-]", "-", f"a2a.{pi}.{pa}.{ci}.{ca}".lower())
    alias = f"#{local}:{domain}"
    rid = mx.resolve_alias(alias)
    if not rid:
        rid = mx.create_room(local, f"A2A {parent_ref} -> {child_ref}", [child_mxid])
    else:
        mx.invite(rid, child_mxid)
    return rid


# ---------------------------------------------------------------- Ledger
SCHEMA = """
CREATE TABLE IF NOT EXISTS external_agents(
  agent_ref TEXT PRIMARY KEY, id TEXT, display_name TEXT, runtime TEXT, instance_id TEXT,
  node TEXT, agentteams_worker_id TEXT, matrix_user_id TEXT, endpoint_ref TEXT,
  capabilities TEXT, health TEXT, last_seen_at TEXT, enabled INTEGER DEFAULT 1, source TEXT);
CREATE TABLE IF NOT EXISTS external_subagent_bindings(
  id TEXT PRIMARY KEY, parent_agent_ref TEXT, child_agent_ref TEXT, role TEXT,
  allowed_modes TEXT, allowed_capabilities TEXT, max_depth INTEGER, max_parallel_tasks INTEGER,
  timeout_seconds INTEGER, requires_human_approval_for_writes INTEGER, enabled INTEGER,
  matrix_room_id TEXT, updated_at TEXT, UNIQUE(parent_agent_ref, child_agent_ref));
CREATE TABLE IF NOT EXISTS external_delegation_tasks(
  id TEXT PRIMARY KEY, correlation_id TEXT UNIQUE, root_run_id TEXT, parent_agent_ref TEXT,
  child_agent_ref TEXT, transport TEXT, transport_message_id TEXT, transport_room_id TEXT,
  mode TEXT, objective TEXT, context TEXT, constraints TEXT, expected_output TEXT,
  acceptance_criteria TEXT, write_intent INTEGER, approved INTEGER, status TEXT, attempt INTEGER,
  max_attempts INTEGER, deadline_at TEXT, output TEXT, summary TEXT, evidence TEXT, self_check TEXT,
  acceptance TEXT, child_session_id TEXT, feedback TEXT, created_at TEXT, updated_at TEXT);
CREATE TABLE IF NOT EXISTS a2a_events(
  event_id TEXT PRIMARY KEY, correlation_id TEXT, type TEXT, source TEXT, received_at TEXT, payload TEXT);
CREATE TABLE IF NOT EXISTS a2a_kv(k TEXT PRIMARY KEY, v TEXT);
"""


class Ledger:
    def __init__(self, path: str):
        os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
        self.db = sqlite3.connect(path, check_same_thread=False, isolation_level=None)
        self.db.row_factory = sqlite3.Row
        self.lock = threading.RLock()
        with self.lock:
            self.db.execute("PRAGMA journal_mode=WAL")
            self.db.executescript(SCHEMA)

    def q(self, sql, args=()):
        with self.lock:
            return [dict(r) for r in self.db.execute(sql, args).fetchall()]

    def x(self, sql, args=()):
        with self.lock:
            return self.db.execute(sql, args).rowcount

    def kv_get(self, k, default=None):
        r = self.q("SELECT v FROM a2a_kv WHERE k=?", (k,))
        return r[0]["v"] if r else default

    def kv_set(self, k, v):
        self.x("INSERT INTO a2a_kv(k,v) VALUES(?,?) ON CONFLICT(k) DO UPDATE SET v=excluded.v", (k, v))

    # ---- events: idempotent
    def record_event(self, event_id, correlation_id, typ, source, payload) -> bool:
        try:
            self.x("INSERT INTO a2a_events VALUES(?,?,?,?,?,?)",
                   (event_id, correlation_id, typ, source, now_iso(), json.dumps(redact(payload))[:200000]))
            return True
        except sqlite3.IntegrityError:
            return False

    # ---- agents
    def upsert_agent(self, a: dict):
        self.x("""INSERT INTO external_agents(agent_ref,id,display_name,runtime,instance_id,node,
                agentteams_worker_id,matrix_user_id,endpoint_ref,capabilities,health,last_seen_at,enabled,source)
                VALUES(:agent_ref,:id,:display_name,:runtime,:instance_id,:node,:agentteams_worker_id,
                :matrix_user_id,:endpoint_ref,:capabilities,:health,:last_seen_at,:enabled,:source)
                ON CONFLICT(agent_ref) DO UPDATE SET display_name=excluded.display_name,runtime=excluded.runtime,
                instance_id=excluded.instance_id,node=excluded.node,agentteams_worker_id=excluded.agentteams_worker_id,
                matrix_user_id=excluded.matrix_user_id,endpoint_ref=excluded.endpoint_ref,
                capabilities=excluded.capabilities,source=excluded.source""",
               {**{"health": "unknown", "last_seen_at": None, "enabled": 1, "source": "manual",
                   "endpoint_ref": None, "matrix_user_id": None}, **a,
                "capabilities": json.dumps(a.get("capabilities") or [])})

    def agents(self):
        out = self.q("SELECT * FROM external_agents ORDER BY agent_ref")
        for a in out:
            a["capabilities"] = json.loads(a["capabilities"] or "[]")
            a["enabled"] = bool(a["enabled"])
        return out

    def agent(self, ref):
        r = [a for a in self.agents() if a["agent_ref"] == ref]
        return r[0] if r else None

    def set_health(self, ref, health):
        self.x("UPDATE external_agents SET health=?, last_seen_at=? WHERE agent_ref=?", (health, now_iso(), ref))

    # ---- bindings
    def upsert_binding(self, b: dict):
        cur = self.binding(b["parent_agent_ref"], b["child_agent_ref"])
        # PUT is a partial update: unspecified fields keep their stored value.
        b = {**{k: v for k, v in (cur or {}).items() if v is not None}, **{k: v for k, v in b.items() if v is not None}}
        row = {
            "id": (cur or {}).get("id") or new_id("esab"),
            "role": b.get("role", "external-subagent"),
            "allowed_modes": json.dumps(b.get("allowed_modes", ["async"])),
            "allowed_capabilities": json.dumps(b.get("allowed_capabilities", [])),
            "max_depth": int(b.get("max_depth", 1)),
            "max_parallel_tasks": int(b.get("max_parallel_tasks", 1)),
            "timeout_seconds": int(b.get("timeout_seconds", 900)),
            "requires_human_approval_for_writes": int(bool(b.get("requires_human_approval_for_writes", True))),
            "enabled": int(bool(b.get("enabled", True))),
            "matrix_room_id": b.get("matrix_room_id") or (cur or {}).get("matrix_room_id"),
            "parent_agent_ref": b["parent_agent_ref"], "child_agent_ref": b["child_agent_ref"],
            "updated_at": now_iso(),
        }
        self.x("""INSERT INTO external_subagent_bindings VALUES(:id,:parent_agent_ref,:child_agent_ref,:role,
               :allowed_modes,:allowed_capabilities,:max_depth,:max_parallel_tasks,:timeout_seconds,
               :requires_human_approval_for_writes,:enabled,:matrix_room_id,:updated_at)
               ON CONFLICT(parent_agent_ref,child_agent_ref) DO UPDATE SET role=excluded.role,
               allowed_modes=excluded.allowed_modes,allowed_capabilities=excluded.allowed_capabilities,
               max_depth=excluded.max_depth,max_parallel_tasks=excluded.max_parallel_tasks,
               timeout_seconds=excluded.timeout_seconds,
               requires_human_approval_for_writes=excluded.requires_human_approval_for_writes,
               enabled=excluded.enabled,matrix_room_id=excluded.matrix_room_id,updated_at=excluded.updated_at""", row)
        return self.binding(b["parent_agent_ref"], b["child_agent_ref"])

    def _bind_row(self, r):
        r["allowed_modes"] = json.loads(r["allowed_modes"])
        r["allowed_capabilities"] = json.loads(r["allowed_capabilities"])
        r["enabled"] = bool(r["enabled"])
        r["requires_human_approval_for_writes"] = bool(r["requires_human_approval_for_writes"])
        return r

    def binding(self, parent, child):
        r = self.q("SELECT * FROM external_subagent_bindings WHERE parent_agent_ref=? AND child_agent_ref=?", (parent, child))
        return self._bind_row(r[0]) if r else None

    def bindings_for(self, parent):
        return [self._bind_row(r) for r in self.q(
            "SELECT * FROM external_subagent_bindings WHERE parent_agent_ref=? ORDER BY child_agent_ref", (parent,))]

    def delete_binding(self, parent, child):
        return self.x("DELETE FROM external_subagent_bindings WHERE parent_agent_ref=? AND child_agent_ref=?", (parent, child))

    # ---- tasks
    _JSON = ("constraints", "acceptance_criteria", "evidence", "self_check", "acceptance")

    def _task_row(self, r):
        for k in self._JSON:
            r[k] = json.loads(r[k]) if r.get(k) else ([] if k != "acceptance" else None)
        r["write_intent"] = bool(r["write_intent"])
        r["approved"] = bool(r["approved"])
        return r

    def insert_task(self, t):
        cols = ",".join(t.keys())
        vals = {k: (json.dumps(v) if k in self._JSON else v) for k, v in t.items()}
        self.x(f"INSERT INTO external_delegation_tasks({cols}) VALUES({','.join(':'+k for k in t)})", vals)
        return self.task(t["id"])

    def task(self, tid):
        r = self.q("SELECT * FROM external_delegation_tasks WHERE id=? OR correlation_id=?", (tid, tid))
        return self._task_row(r[0]) if r else None

    def tasks(self, parent=None, limit=100):
        if parent:
            rows = self.q("SELECT * FROM external_delegation_tasks WHERE parent_agent_ref=? ORDER BY created_at DESC LIMIT ?", (parent, limit))
        else:
            rows = self.q("SELECT * FROM external_delegation_tasks ORDER BY created_at DESC LIMIT ?", (limit,))
        return [self._task_row(r) for r in rows]

    def active_count(self, parent, child):
        return self.q(f"SELECT COUNT(*) n FROM external_delegation_tasks WHERE parent_agent_ref=? AND child_agent_ref=? "
                      f"AND status IN ({','.join('?'*len(ACTIVE))})", (parent, child, *ACTIVE))[0]["n"]

    def transition(self, tid, new_status, **fields):
        t = self.task(tid)
        if not t:
            raise KeyError(tid)
        old = t["status"]
        if new_status != old and new_status not in TRANSITIONS.get(old, set()):
            raise ValueError(f"illegal transition {old} -> {new_status}")
        fields = {k: (json.dumps(v) if k in self._JSON else v) for k, v in fields.items()}
        fields["status"] = new_status
        fields["updated_at"] = now_iso()
        sets = ",".join(f"{k}=:{k}" for k in fields)
        self.x(f"UPDATE external_delegation_tasks SET {sets} WHERE id=:id", {**fields, "id": t["id"]})
        return self.task(t["id"])


# ---------------------------------------------------------------- Policy
WRITE_WORDS = re.compile(r"(?i)\b(create|update|delete|remove|write|modify|send|post|submit|approve|move|archive|tạo|sửa|xóa|xoá|cập nhật|gửi|duyệt)\b")


def check_policy(ledger: Ledger, req: dict, enabled_flag: bool = True) -> tuple[bool, str]:
    if not enabled_flag:
        return False, "Agent360:ExternalA2A:Enabled=false"
    parent, child = req["parent_agent_ref"], req["child_agent_ref"]
    if parent == child:
        return False, "parent and child are the same agent"
    chain = req.get("call_chain") or [parent]
    if child in chain:
        return False, f"cycle detected in call_chain {chain}"
    b = ledger.binding(parent, child)
    if not b or not b["enabled"]:
        return False, f"no enabled binding {parent} -> {child}"
    a = ledger.agent(child)
    if not a or not a["enabled"]:
        return False, f"external agent {child} not registered/enabled"
    if req.get("mode", "async") not in b["allowed_modes"]:
        return False, f"mode {req.get('mode')} not allowed"
    depth = int(req.get("delegation_depth", 1))
    if depth > b["max_depth"]:
        return False, f"delegation depth {depth} exceeds max_depth {b['max_depth']}"
    caps = req.get("capabilities") or []
    if b["allowed_capabilities"] and any(c not in b["allowed_capabilities"] for c in caps):
        return False, f"capabilities {caps} outside allowlist {b['allowed_capabilities']}"
    if ledger.active_count(parent, child) >= b["max_parallel_tasks"]:
        return False, f"parallel limit {b['max_parallel_tasks']} reached"
    if not req.get("acceptance_criteria"):
        return False, "acceptance_criteria is required"
    blob = json.dumps([req.get("objective"), req.get("context"), req.get("constraints")], ensure_ascii=False)
    if contains_secret(blob):
        return False, "payload contains secret-like material"
    write_intent = bool(req.get("write_intent")) or (
        bool(WRITE_WORDS.search(req.get("objective", ""))) and not any(
            re.search(r"(?i)read[- ]?only|chỉ đọc", c) for c in req.get("constraints") or []))
    if write_intent and b["requires_human_approval_for_writes"] and not req.get("approved"):
        return False, "write task requires human approval (approved=true)"
    return True, "ok"
