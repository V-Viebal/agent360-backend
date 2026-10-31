"""Agent360 external A2A core: contracts, Matrix transport, ledger, policy.

Transport: AgentTeams v1.2.3 Matrix (Tuwunel) rooms (plan Option A).
Agent360 is the control plane + ledger; AgentTeams provides identity/routing.
Stdlib only.
"""
from __future__ import annotations

import http.client
import json
import os
import random
import socket
import ssl
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
MSG_MESSAGE = "agent360.delegation.message.v1"   # v2 multi-turn: question/answer/followup/redirect/progress
PROTOCOL_VERSION = 2
TURN_KINDS = {"request", "question", "answer", "followup", "redirect", "progress", "result", "system",
              "approval_request", "approval", "escalation"}
TURN_ROLES = {"parent", "child", "operator", "system"}
MAX_TURN_TEXT = 8000
MAX_THREAD_TEXT = 64000
EVENT_TYPE = "vn.antares.agent360.a2a"  # custom Matrix event type
MAX_INLINE_OUTPUT = 12000

ACTIVE = {"draft", "policy_checked", "sent", "delivered", "running", "retry_queued", "awaiting_parent"}
TERMINAL = {"accepted", "rejected", "failed", "timed_out", "cancelled", "delivery_failed"}
TRANSITIONS = {
    "draft": {"policy_checked", "cancelled"},
    "policy_checked": {"sent", "delivery_failed", "cancelled"},
    "sent": {"delivered", "running", "completed", "failed", "delivery_failed", "timed_out", "cancelled"},
    "delivered": {"running", "completed", "failed", "timed_out", "cancelled", "awaiting_parent"},
    "running": {"running", "completed", "failed", "timed_out", "cancelled", "awaiting_parent"},
    # v2: child asked a blocking question; deadline paused until the parent/operator answers.
    "awaiting_parent": {"running", "completed", "failed", "timed_out", "cancelled", "awaiting_parent"},
    "completed": {"under_review"},
    # v2: a follow-up continues the same attempt/session instead of a reject+retry.
    "under_review": {"accepted", "rejected", "running"},
    "rejected": {"retry_queued"},
    "retry_queued": {"sent", "delivery_failed", "cancelled"},
    # A Matrix PUT can time out client-side after the homeserver accepted it; a signed event from the bound
    # child proves delivery, so the ledger may recover to `sent` (see control_plane.apply_event).
    "delivery_failed": {"sent"},
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
def send_reliable(send, room_id: str, content: dict, txn_id: str, budget_seconds: float = 180):
    """PUT with a deterministic txn id (homeserver de-duplicates) retried with jittered backoff."""
    deadline, delay = time.time() + budget_seconds, 1.0
    while True:
        try:
            return send(room_id, content, txn_id=txn_id)
        except Exception:  # noqa: BLE001
            if time.time() + delay > deadline:
                raise
            time.sleep(delay + random.random() * 0.5)
            delay = min(delay * 2, 20)


class _PinnedHTTPS(http.client.HTTPSConnection):
    """HTTPS to a pinned origin IP while keeping SNI + certificate verification for the real hostname."""

    def __init__(self, host, port, ip, timeout, context):
        super().__init__(host, port, timeout=timeout, context=context)
        self._pin_ip = ip

    def connect(self):
        sock = socket.create_connection((self._pin_ip or self.host, self.port), self.timeout)
        sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.sock = self._context.wrap_socket(sock, server_hostname=self.host)


RETRYABLE_STATUS = {502, 503, 504, 520, 521, 522, 523, 524, 525, 526}


class MatrixClient:
    """Matrix client-server API over keep-alive connections.

    Transport (A2A_MATRIX_ORIGIN_IP): when set, requests go straight to the homeserver origin (e.g. the
    AgentTeams node's Tailscale IP) with SNI/cert verification for the public hostname, and fall back to
    public DNS (Cloudflare) on connection errors/5xx. A circuit breaker skips the direct path for
    A2A_MATRIX_DIRECT_COOLDOWN seconds after a failure. Connections are per-thread (sync, heartbeat,
    senders never share a socket)."""

    def __init__(self, homeserver: str, access_token: str, user_id: str, timeout: int = 30, origin_ip: str | None = None):
        self.hs = homeserver.rstrip("/")
        self.token = access_token
        self.user_id = user_id
        self.timeout = timeout
        u = urllib.parse.urlsplit(self.hs)
        self._https = u.scheme == "https"
        self._host = u.hostname
        self._port = u.port or (443 if self._https else 80)
        self._prefix = u.path.rstrip("/")
        self.origin_ip = (origin_ip if origin_ip is not None else os.environ.get("A2A_MATRIX_ORIGIN_IP", "")).strip() or None
        self.direct_cooldown = float(os.environ.get("A2A_MATRIX_DIRECT_COOLDOWN", "60"))
        self._direct_down_until = 0.0
        self._direct_streak = 0
        self._ctx = ssl.create_default_context() if self._https else None
        self._local = threading.local()
        self._lock = threading.Lock()
        self.stats = {"direct_ok": 0, "public_ok": 0, "direct_fail": 0, "public_fail": 0, "fallbacks": 0,
                      "last_path": None, "last_ms": None}

    def _conn(self, path_kind, timeout):
        c = getattr(self._local, path_kind, None)
        if c is None:
            ip = self.origin_ip if path_kind == "direct" else None
            if self._https:
                c = _PinnedHTTPS(self._host, self._port, ip, timeout, self._ctx)
            else:
                c = http.client.HTTPConnection(ip or self._host, self._port, timeout=timeout)
            setattr(self._local, path_kind, c)
        c.timeout = timeout
        if c.sock is not None:
            c.sock.settimeout(timeout)
        return c

    def _drop(self, path_kind):
        c = getattr(self._local, path_kind, None)
        if c is not None:
            try:
                c.close()
            except Exception:  # noqa: BLE001
                pass
        setattr(self._local, path_kind, None)

    def _paths(self):
        if self.origin_ip and time.time() >= self._direct_down_until:
            return ["direct", "direct", "public"]
        return ["public", "public"]

    def _call(self, method: str, path: str, body=None, query=None, timeout=None):
        target = f"{self._prefix}/_matrix/client/v3{path}"
        if query:
            target += "?" + urllib.parse.urlencode(query)
        data = json.dumps(body).encode() if body is not None else None
        headers = {"Authorization": f"Bearer {self.token}", "Content-Type": "application/json",
                   "User-Agent": "agent360-a2a/1.1", "Connection": "keep-alive"}
        last = None
        for i, kind in enumerate(self._paths()):
            t0 = time.time()
            try:
                c = self._conn(kind, timeout or self.timeout)
                c.request(method, target, body=data, headers=headers)
                r = c.getresponse()
                raw = r.read()
                if r.getheader("connection", "").lower() == "close":
                    self._drop(kind)
            except (OSError, http.client.HTTPException) as e:
                self._drop(kind)
                last = e
                self._fail(kind)
                continue
            if r.status in RETRYABLE_STATUS:
                self._drop(kind)
                last = RuntimeError(f"matrix {method} {path} -> {r.status}")
                self._fail(kind)
                continue
            with self._lock:
                if kind == "direct":
                    self._direct_streak = 0
                self.stats[f"{kind}_ok"] += 1
                self.stats["last_path"], self.stats["last_ms"] = kind, int((time.time() - t0) * 1000)
                if i and kind == "public":
                    self.stats["fallbacks"] += 1
            if r.status >= 400:
                raise RuntimeError(f"matrix {method} {path} -> {r.status}: {raw.decode('utf-8', 'ignore')[:400]}")
            return json.loads(raw or b"{}")
        raise RuntimeError(f"matrix {method} {path} failed on all paths: {type(last).__name__}: {last}")

    def _fail(self, kind):
        with self._lock:
            self.stats[f"{kind}_fail"] += 1
            if kind == "direct":
                # one failure is usually a stale keep-alive socket; two in a row => origin path is down
                self._direct_streak += 1
                if self._direct_streak >= 2:
                    self._direct_down_until = time.time() + self.direct_cooldown
                    self._direct_streak = 0

    def send_reliable(self, room_id: str, content: dict, txn_id: str, budget_seconds: float = 180):
        return send_reliable(self.send, room_id, content, txn_id, budget_seconds)

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
CREATE TABLE IF NOT EXISTS a2a_turns(
  id INTEGER PRIMARY KEY AUTOINCREMENT, correlation_id TEXT, attempt INTEGER, turn_no INTEGER,
  message_id TEXT UNIQUE, role TEXT, kind TEXT, text TEXT, options TEXT, blocking INTEGER,
  in_reply_to INTEGER, author_ref TEXT, created_at TEXT, delivered_at TEXT);
CREATE INDEX IF NOT EXISTS a2a_turns_cid ON a2a_turns(correlation_id, turn_no);
"""

# v2 columns added to existing ledgers in place (idempotent).
TASK_V2_COLUMNS = {"turn_count": "INTEGER DEFAULT 0", "question_count": "INTEGER DEFAULT 0",
                   "awaiting_since": "TEXT", "question_deadline_at": "TEXT", "conversation": "TEXT",
                   "protocol": "INTEGER DEFAULT 1"}


class Ledger:
    def __init__(self, path: str):
        os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
        self.db = sqlite3.connect(path, check_same_thread=False, isolation_level=None)
        self.db.row_factory = sqlite3.Row
        self.lock = threading.RLock()
        with self.lock:
            self.db.execute("PRAGMA journal_mode=WAL")
            self.db.executescript(SCHEMA)
            have = {r[1] for r in self.db.execute("PRAGMA table_info(external_delegation_tasks)").fetchall()}
            for col, decl in TASK_V2_COLUMNS.items():
                if col not in have:
                    self.db.execute(f"ALTER TABLE external_delegation_tasks ADD COLUMN {col} {decl}")

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
    _JSON = ("constraints", "acceptance_criteria", "evidence", "self_check", "acceptance", "conversation")

    def _task_row(self, r):
        for k in self._JSON:
            r[k] = json.loads(r[k]) if r.get(k) else ([] if k not in ("acceptance", "conversation") else None)
        r["write_intent"] = bool(r["write_intent"])
        r["approved"] = bool(r["approved"])
        return r

    # ------------------------------------------------------------ v2 turns
    def add_turn(self, cid, attempt, role, kind, text, *, turn_no=None, message_id=None, options=None,
                 blocking=False, in_reply_to=None, author_ref=None):
        """Append one conversation turn. Idempotent on message_id; returns (turn, created)."""
        if role not in TURN_ROLES or kind not in TURN_KINDS:
            raise ValueError(f"invalid turn role/kind {role}/{kind}")
        text = redact(str(text or ""))[:MAX_TURN_TEXT]
        with self.lock:
            if message_id:
                ex = self.q("SELECT * FROM a2a_turns WHERE message_id=?", (message_id,))
                if ex:
                    return self._turn_row(ex[0]), False
            if turn_no is None:
                turn_no = (self.q("SELECT COALESCE(MAX(turn_no),0) n FROM a2a_turns WHERE correlation_id=?", (cid,))[0]["n"] or 0) + 1
            elif self.q("SELECT 1 FROM a2a_turns WHERE correlation_id=? AND turn_no=?", (cid, turn_no)):
                # collision (both sides numbered concurrently): re-sequence at the end, keep order of arrival
                turn_no = (self.q("SELECT MAX(turn_no) n FROM a2a_turns WHERE correlation_id=?", (cid,))[0]["n"] or 0) + 1
            message_id = message_id or f"{cid}:{turn_no}:{role}:{kind}"
            self.x("INSERT INTO a2a_turns(correlation_id,attempt,turn_no,message_id,role,kind,text,options,blocking,"
                   "in_reply_to,author_ref,created_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",
                   (cid, attempt, turn_no, message_id, role, kind, text, json.dumps(options or []),
                    int(bool(blocking)), in_reply_to, author_ref, now_iso()))
            self.x("UPDATE external_delegation_tasks SET turn_count=COALESCE(turn_count,0)+1"
                   + (", question_count=COALESCE(question_count,0)+1" if kind == "question" else "")
                   + " WHERE correlation_id=?", (cid,))
            return self._turn_row(self.q("SELECT * FROM a2a_turns WHERE message_id=?", (message_id,))[0]), True

    def turns(self, cid, after=0, limit=200):
        return [self._turn_row(r) for r in self.q(
            "SELECT * FROM a2a_turns WHERE correlation_id=? AND turn_no>? ORDER BY turn_no LIMIT ?", (cid, after, limit))]

    def open_question(self, cid):
        """Latest child question that has no answer/redirect/approval after it."""
        rows = self.q("SELECT * FROM a2a_turns WHERE correlation_id=? ORDER BY turn_no", (cid,))
        pending = None
        for r in rows:
            if r["role"] == "child" and r["kind"] in ("question", "approval_request") and r["blocking"]:
                pending = r
            elif pending and r["kind"] in ("answer", "redirect", "approval", "system") and r["role"] != "child":
                pending = None
            elif pending and r["role"] == "child" and r["kind"] == "result":
                pending = None
        return self._turn_row(pending) if pending else None

    def thread_chars(self, cid):
        return self.q("SELECT COALESCE(SUM(LENGTH(text)),0) n FROM a2a_turns WHERE correlation_id=?", (cid,))[0]["n"]

    @staticmethod
    def _turn_row(r):
        r = dict(r)
        r["options"] = json.loads(r["options"]) if r.get("options") else []
        r["blocking"] = bool(r.get("blocking"))
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
