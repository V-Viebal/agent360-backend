"""AgentTeams A2A worker adapter (child side).

Listens on the child's Matrix identity, accepts agent360.delegation.request.v1
events for its own agent_ref only, runs the task on the LOCAL QwenPaw agent
(/api/console/chat with X-Agent-Id) and replies status/result events.

Env:
  A2A_MATRIX_CRED   path to {"user_id","access_token","homeserver"} (0600)
  A2A_AGENT_REF     e.g. agent://gm/default
  A2A_QWENPAW_URL   e.g. http://qwenpaw-gm:8088
  A2A_QWENPAW_AGENT e.g. default
  A2A_ALLOWED_SENDERS comma list of Matrix ids allowed to delegate (control plane)
  A2A_STATE_DIR     sqlite state dir (idempotency)
  A2A_HEARTBEAT_SECONDS heartbeat period into joined rooms (default 60, 0 disables)
  A2A_MATRIX_ORIGIN_IP  optional homeserver origin IP (Tailscale) used before the public CDN path
  A2A_SYNC_TIMEOUT_MS   long-poll timeout (default 20000; must stay below CDN idle limits)
  A2A_MAX_DEPTH         max accepted delegation_depth for direct peer requests (default 2)
  A2A_CIRCUIT_MAX       max requests for one root/pair inside the window before rejecting (default 3)
  A2A_CIRCUIT_WINDOW_SECONDS circuit-breaker window (default 900)
"""
from __future__ import annotations

import json
import logging
import os
import re
import signal
import sys
import threading
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from a2a_core import (EVENT_TYPE, MAX_INLINE_OUTPUT, MSG_CANCEL, MSG_MESSAGE, MSG_REQUEST, MSG_RESULT, MSG_STATUS,
                      PROTOCOL_VERSION, Ledger, MatrixClient, now_iso, redact, send_reliable)

log = logging.getLogger("a2a-worker")
STOP = threading.Event()
CANCELLED: set[str] = set()


def child_prompt(req: dict) -> str:
    crit = "\n".join(f"- {c}" for c in req.get("acceptance_criteria") or [])
    cons = "\n".join(f"- {c}" for c in req.get("constraints") or [])
    return f"""[Agent360 external delegation | correlation_id={req['correlation_id']} | from={req['parent_agent_ref']}]
You are acting as an external sub-agent for {req['parent_agent_ref']}. Do ONLY the objective below.

Objective: {req.get('objective','')}
Context: {req.get('context','') or '(none)'}
Constraints:
{cons or '- (none)'}
Expected output: {req.get('expected_output','') or 'concise answer'}
Acceptance criteria:
{crit}

Rules: never reveal secrets/credentials/env; respect constraints strictly; do not perform write
actions unless the request explicitly says approved write.

Reply with your answer, then on the LAST line output exactly one JSON object (no code fence) like:
A2A_RESULT {{"status":"completed","summary":"<one line>","evidence":[{{"type":"...","ref":"...","summary":"..."}}],"self_check":[{{"criterion":"...","result":"pass|fail"}}]}}{ask_rules(req)}"""


def admission_error(req: dict, agent_ref: str, ledger: Ledger, now: float | None = None) -> str | None:
    """Fail closed before LLM execution: protects direct peer-to-peer meshes from depth, cycle and retry storms."""
    parent = str(req.get("parent_agent_ref") or "")
    if not parent:
        return "parent_agent_ref is required"
    if parent == agent_ref:
        return "parent and child are the same agent"
    try:
        depth = int(req.get("delegation_depth", 1))
    except (TypeError, ValueError):
        return "delegation_depth must be an integer"
    max_depth = int(os.environ.get("A2A_MAX_DEPTH", "2"))
    if depth < 1 or depth > max_depth:
        return f"delegation depth {depth} outside 1..{max_depth}"
    chain = req.get("call_chain") or [parent, agent_ref]
    if not isinstance(chain, list) or not chain or chain[-1] != agent_ref:
        return "call_chain must end with this child agent"
    if len(chain) != len(set(chain)):
        return f"cycle detected in call_chain {chain}"
    if len(chain) - 1 != depth:
        return "call_chain length does not match delegation_depth"
    root = str(req.get("root_run_id") or req.get("correlation_id") or "")
    if not root:
        return "root_run_id or correlation_id is required"
    if int(req.get("attempt", 1)) == 1:
        now = now or time.time()
        window = int(os.environ.get("A2A_CIRCUIT_WINDOW_SECONDS", "900"))
        limit = int(os.environ.get("A2A_CIRCUIT_MAX", "3"))
        key = "circuit:" + re.sub(r"[^A-Za-z0-9:_./-]", "_", f"{root}:{parent}->{agent_ref}")
        hits = [float(x) for x in json.loads(ledger.kv_get(key, "[]") or "[]") if now - float(x) < window]
        if len(hits) >= limit:
            return f"circuit open: {len(hits)} requests for {parent}->{agent_ref} in {window}s"
        hits.append(now)
        ledger.kv_set(key, json.dumps(hits[-limit:]))
    return None


def ask_rules(req: dict) -> str:
    conv = req.get("conversation") or {}
    if req.get("protocol", 1) < 2 or not conv.get("allow_questions", True):
        return ""
    left = max(0, int(conv.get("max_questions", 3)) - int(conv.get("question_count", 0)))
    if not left:
        return "\nYou may not ask further questions: finish with A2A_RESULT using a stated assumption."
    return f"""

If (and only if) the objective is ambiguous or you need a decision you must not guess, you may instead end with
exactly one line asking the requesting agent ({left} question(s) left):
A2A_ASK {{{{"question":"<one precise question>","options":["<choice>", "..."],"blocking":true,"why":"<one line>"}}}}
Use "blocking":false when you can proceed on a stated assumption meanwhile. Before any write action that was not
approved, ask with A2A_APPROVAL {{{{"question":"<what you will change>","why":"..."}}}} instead of acting."""


def continuation_prompt(msg: dict) -> str:
    kind, text = msg.get("kind"), msg.get("text", "")
    conv = msg.get("conversation") or {}
    head = {"answer": f"[Parent answer to turn {msg.get('in_reply_to')}]",
            "followup": "[Parent follow-up on your result]",
            "redirect": "[Parent redirect — change course]",
            "approval": "[Operator approved the write action you asked about]",
            "system": "[System]"}.get(kind, f"[{kind}]")
    who = " (from human operator)" if msg.get("role") == "operator" else ""
    left = max(0, int(conv.get("max_turns", 8)) - int(conv.get("turn_count", 0)))
    return (f"[Agent360 external delegation | correlation_id={msg['correlation_id']} | turn {msg.get('turn_no')}]\n"
            f"{head}{who}: {text}\n\nContinue the same task with this input ({left} turn(s) left). End with exactly one "
            f"final line: A2A_RESULT {{...}} as before, or A2A_ASK {{...}} if you must ask again.")


_QP_TOKEN = {"v": None}


def qp_token(base: str, force: bool = False):
    """Bearer token for the local QwenPaw (auth-enabled runtimes). Credentials come from
    A2A_QWENPAW_AUTH_FILE (0600, this instance only); never from the A2A message."""
    path = os.environ.get("A2A_QWENPAW_AUTH_FILE")
    if not path or not os.path.exists(path):
        return None
    if _QP_TOKEN["v"] and not force:
        return _QP_TOKEN["v"]
    cred = json.load(open(path))
    r = urllib.request.Request(f"{base.rstrip('/')}/api/auth/login", method="POST",
                               data=json.dumps({"username": cred["username"], "password": cred["password"]}).encode(),
                               headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(r, timeout=30) as resp:
        _QP_TOKEN["v"] = json.loads(resp.read()).get("token")
    return _QP_TOKEN["v"]


def run_qwenpaw(base: str, agent: str, req: dict, deadline: float, _retry: bool = True, text: str | None = None) -> tuple[str, str]:
    # One QwenPaw session per delegation attempt: every turn of the thread keeps the child's context.
    session = f"a2a-{req['correlation_id']}" + (f"-a{req.get('attempt')}" if int(req.get("attempt") or 1) > 1 else "")
    body = json.dumps({
        "input": [{"role": "user", "content": [{"type": "text", "text": text if text is not None else child_prompt(req)}]}],
        "session_id": session, "user_id": "agent360-a2a", "channel": "console", "stream": True,
    }).encode()
    hdr = {"Content-Type": "application/json", "X-Agent-Id": agent}
    tok = qp_token(base)
    if tok:
        hdr["Authorization"] = f"Bearer {tok}"
    r = urllib.request.Request(f"{base.rstrip('/')}/api/console/chat", data=body, method="POST", headers=hdr)
    text_parts: list[str] = []
    final_text = ""
    timeout = max(30, deadline - time.time())
    try:
        resp = urllib.request.urlopen(r, timeout=timeout)
    except urllib.error.HTTPError as e:
        if e.code == 401 and _retry and os.environ.get("A2A_QWENPAW_AUTH_FILE"):
            qp_token(base, force=True)
            return run_qwenpaw(base, agent, req, deadline, _retry=False, text=text)
        if e.code == 403:
            try:
                detail = json.loads(e.read() or b"{}").get("detail")
            except Exception:  # noqa: BLE001
                detail = None
            raise RuntimeError(f"local QwenPaw rejected agent {agent}: {detail or 'forbidden'}") from None
        raise
    with resp:
        for raw in resp:
            if req["correlation_id"] in CANCELLED:
                break
            line = raw.decode("utf-8", "ignore").strip()
            if not line.startswith("data:"):
                continue
            try:
                ev = json.loads(line[5:].strip())
            except Exception:
                continue
            # QwenPaw stream: final answer = completed assistant message of type "message"
            # (type "reasoning" is internal thinking and must not leak into the result).
            if (ev.get("object") == "message" and ev.get("role") == "assistant"
                    and ev.get("type") == "message" and ev.get("status") == "completed"):
                texts = [c.get("text", "") for c in ev.get("content") or [] if c.get("type") == "text"]
                if any(texts):
                    final_text = (final_text + "\n\n" + "\n".join(texts)).strip()
            elif ev.get("object") == "response" and ev.get("status") == "completed" and not final_text:
                for m in ev.get("output") or []:
                    if m.get("type") == "message":
                        text_parts += [c.get("text", "") for c in m.get("content") or [] if c.get("type") == "text"]
    return (final_text or "".join(text_parts)).strip(), session


def parse_ask(text: str) -> dict | None:
    """Last-line A2A_ASK / A2A_APPROVAL -> question dict, else None (A2A_RESULT or plain text = result)."""
    m = None
    for m in re.finditer(r"(A2A_ASK|A2A_APPROVAL)\s*(\{.*\})\s*$", text, re.S | re.M):
        pass
    if not m:
        return None
    tail = text[m.end():].strip()
    if "A2A_RESULT" in tail:
        return None
    try:
        meta = json.loads(m.group(2))
    except Exception:
        return None
    q = str(meta.get("question") or "").strip()
    if not q:
        return None
    approval = m.group(1) == "A2A_APPROVAL"
    return {"kind": "approval_request" if approval else "question", "text": q,
            "options": [str(o) for o in (meta.get("options") or [])][:12],
            "blocking": True if approval else bool(meta.get("blocking", True)),
            "why": str(meta.get("why") or "")[:300], "preamble": text[:m.start()].strip()}


def parse_result(text: str) -> dict:
    m = None
    for m in re.finditer(r"A2A_RESULT\s*(\{.*\})\s*$", text, re.S | re.M):
        pass
    meta = {}
    if m:
        try:
            meta = json.loads(m.group(1))
        except Exception:
            meta = {}
        body = text[:m.start()].strip()
    else:
        body = text
    return {"output": body, "summary": meta.get("summary") or body[:200], "status": meta.get("status", "completed"),
            "evidence": meta.get("evidence") or [], "self_check": meta.get("self_check") or []}


class Worker:
    def __init__(self):
        cred = json.load(open(os.environ["A2A_MATRIX_CRED"]))
        self.mx = MatrixClient(cred["homeserver"], cred["access_token"], cred["user_id"])
        self.agent_ref = os.environ["A2A_AGENT_REF"]
        self.qp_url = os.environ["A2A_QWENPAW_URL"]
        self.qp_agent = os.environ["A2A_QWENPAW_AGENT"]
        self.allowed = {s.strip() for s in os.environ.get("A2A_ALLOWED_SENDERS", "").split(",") if s.strip()}
        self.ledger = Ledger(os.path.join(os.environ.get("A2A_STATE_DIR", "/state"), "worker.db"))
        self.sem = threading.Semaphore(int(os.environ.get("A2A_MAX_CONCURRENCY", "2")))
        self.flush_now = threading.Event()

    def emit(self, room, typ, req, **kw):
        """Durable emit: persisted in the outbox first, sent with a deterministic txn id (homeserver
        de-duplicates retries); a background flusher re-sends anything a transient error left behind."""
        cid = req["correlation_id"]
        key = kw.get("message_id") or f"{cid}:{req.get('attempt', 1)}:{typ}:{kw.get('status', '')}" + (
            f":{kw['seq']}" if kw.get("seq") else (f":t{kw['turn']}" if kw.get("turn") else ""))
        content = {"type": typ, "correlation_id": cid, "task_id": req.get("task_id"),
                   "child_agent_ref": self.agent_ref, "event_id": key, "updated_at": now_iso(), **redact(kw)}
        content["body"] = f"[{typ}] {kw.get('status','')} {cid}"
        txn = "w-" + re.sub(r"[^A-Za-z0-9._-]", "_", key)
        self.ledger.x("INSERT OR REPLACE INTO a2a_kv(k, v) VALUES(?, ?)",
                      ("outbox:" + txn, json.dumps({"room": room, "content": content, "at": time.time()})))
        try:
            eid = send_reliable(self.mx.send, room, content, txn, 20)
        except Exception as e:  # noqa: BLE001
            log.warning("emit %s deferred to outbox: %s", key, redact(str(e))[:160])
            self.flush_now.set()
            return None
        self.ledger.x("DELETE FROM a2a_kv WHERE k=?", ("outbox:" + txn,))
        return eid

    def outbox_loop(self):
        while not STOP.is_set():
            self.flush_now.wait(10)
            self.flush_now.clear()
            for row in self.ledger.q("SELECT k, v FROM a2a_kv WHERE k LIKE 'outbox:%' ORDER BY k"):
                item = json.loads(row["v"])
                if time.time() - item.get("at", 0) > 86400:
                    self.ledger.x("DELETE FROM a2a_kv WHERE k=?", (row["k"],))
                    continue
                try:
                    self.mx.send(item["room"], item["content"], txn_id=row["k"][len("outbox:"):])
                    self.ledger.x("DELETE FROM a2a_kv WHERE k=?", (row["k"],))
                    log.info("outbox delivered %s", row["k"])
                except Exception as e:  # noqa: BLE001
                    log.warning("outbox retry %s: %s", row["k"], redact(str(e))[:160])
                    break

    def handle(self, room, sender, content):
        typ = content.get("type")
        cid = content.get("correlation_id")
        if typ == MSG_CANCEL:
            CANCELLED.add(cid)
            self.unpark(cid)
            return
        if typ == MSG_MESSAGE:
            if self.allowed and sender not in self.allowed:
                return
            if content.get("child_agent_ref") != self.agent_ref or content.get("role") == "child":
                return
            if not self.ledger.record_event(f"{cid}:msg:{content.get('message_id')}", cid, typ, sender, content):
                return
            self.resume(room, content)
            return
        if typ != MSG_REQUEST:
            return
        if self.allowed and sender not in self.allowed:
            log.warning("reject sender %s for %s", sender, cid)
            return
        if content.get("child_agent_ref") != self.agent_ref:
            return
        attempt = int(content.get("attempt", 1))
        if not self.ledger.record_event(f"{cid}:req:{attempt}", cid, typ, sender, content):
            log.info("duplicate request %s attempt %s ignored", cid, attempt)
            return
        err = admission_error(content, self.agent_ref, self.ledger)
        if err:
            log.warning("reject request %s: %s", cid, err)
            self.emit(room, MSG_RESULT, content, status="failed", summary="policy_denied", output=err)
            return
        self.ledger.kv_set(f"req:{cid}", json.dumps(content))
        threading.Thread(target=self._run, args=(room, content), daemon=True).start()

    def _run(self, room, req, text=None):
        """Run one child turn; on A2A_ASK emit a question and park (blocking) or continue (non-blocking)."""
        with self.sem:
            cid = req["correlation_id"]
            try:
                deadline_at = req.get("deadline_at")
                deadline = (datetime.strptime(deadline_at, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc).timestamp()
                            if deadline_at else time.time() + 900)
                if deadline < time.time() + 30:
                    deadline = time.time() + 900  # deadline paused upstream while waiting on the parent
                if text is None:
                    self.emit(room, MSG_STATUS, req, status="delivered", message="worker accepted request")
                self.emit(room, MSG_STATUS, req, status="running", message=f"running on {self.qp_agent}",
                          turn=int(req.get("turn_seq", 0)))
                for _ in range(4):  # non-blocking questions continue in-place; bounded
                    out_text, session = run_qwenpaw(self.qp_url, self.qp_agent, req, deadline, text=text)
                    if cid in CANCELLED:
                        self.emit(room, MSG_RESULT, req, status="cancelled", summary="cancelled by parent", output="")
                        return
                    ask = parse_ask(out_text) if req.get("protocol", 1) >= 2 else None
                    if not ask:
                        break
                    seq = self.next_seq(cid)
                    self.emit(room, MSG_MESSAGE, req, kind=ask["kind"], text=ask["text"], options=ask["options"],
                              blocking=ask["blocking"], why=ask["why"], preamble=ask["preamble"][:2000],
                              message_id=f"{cid}:a{req.get('attempt', 1)}:child:{seq}", child_session_id=session)
                    log.info("question %s #%s blocking=%s", cid, seq, ask["blocking"])
                    if ask["blocking"]:
                        self.park(cid, room, req)
                        return
                    text = ("[No answer yet] Proceed now on your best stated assumption (say which), and finish with "
                            "A2A_RESULT {...}.")
                res = parse_result(out_text)
                out = res["output"]
                if len(out) > MAX_INLINE_OUTPUT:
                    out = out[:MAX_INLINE_OUTPUT] + "\n…[truncated]"
                status = "failed" if not out_text else ("completed" if res["status"] != "failed" else "failed")
                self.emit(room, MSG_RESULT, req, status=status, summary=res["summary"], output=out,
                          evidence=res["evidence"], self_check=res["self_check"], child_session_id=session,
                          completed_at=now_iso(), seq=self.next_seq(cid))
                self.unpark(cid)
                log.info("result %s status=%s chars=%d", cid, status, len(out))
            except Exception as e:  # noqa: BLE001
                log.exception("task %s failed", cid)
                try:
                    self.emit(room, MSG_RESULT, req, status="failed", summary=f"worker error: {type(e).__name__}",
                              output=redact(str(e))[:2000], seq=self.next_seq(cid))
                except Exception:
                    pass

    # ---- parked conversations survive restarts (worker.db), and hold no concurrency slot while waiting
    def next_seq(self, cid):
        n = int(self.ledger.kv_get(f"seq:{cid}", "0") or 0) + 1
        self.ledger.kv_set(f"seq:{cid}", str(n))
        return n

    def park(self, cid, room, req):
        self.ledger.kv_set(f"parked:{cid}", json.dumps({"room": room, "req": req, "at": now_iso()}))

    def unpark(self, cid):
        self.ledger.x("DELETE FROM a2a_kv WHERE k=?", (f"parked:{cid}",))

    def parked(self, cid):
        v = self.ledger.kv_get(f"parked:{cid}")
        return json.loads(v) if v else None

    def resume(self, room, msg):
        """Parent/operator/system turn for a conversation (parked, or a follow-up after a result)."""
        cid = msg["correlation_id"]
        p = self.parked(cid)
        req = (p or {}).get("req") or self.ledger_req(cid)
        if not req:
            log.warning("message for unknown conversation %s ignored", cid)
            return
        req = {**req, "conversation": msg.get("conversation") or req.get("conversation"),
               "approved_write": bool(msg.get("approved_write", req.get("approved_write")))}
        if msg.get("attempt"):
            req["attempt"] = msg["attempt"]
        self.ledger.kv_set(f"req:{cid}", json.dumps(req))
        threading.Thread(target=self._run, args=((p or {}).get("room") or room, req, continuation_prompt(msg)),
                         daemon=True).start()

    def ledger_req(self, cid):
        v = self.ledger.kv_get(f"req:{cid}")
        return json.loads(v) if v else None

    def heartbeat_loop(self):
        period = int(os.environ.get("A2A_HEARTBEAT_SECONDS", "60"))
        if period <= 0:
            return
        rooms, rooms_at = [], 0.0
        first = True
        while first or not STOP.wait(period):
            first = False
            try:
                if not rooms or time.time() - rooms_at > 600:
                    rooms, rooms_at = self.mx.joined_rooms(), time.time()
                for rid in rooms:
                    self.mx.send(rid, {"type": "agent360.worker.heartbeat.v1", "child_agent_ref": self.agent_ref,
                                       "protocol": PROTOCOL_VERSION, "features": ["multi_turn"],
                                       "at": now_iso(), "body": f"[heartbeat] {self.agent_ref}"})
                self.ledger.kv_set("heartbeat_sent", now_iso())
            except Exception as e:  # noqa: BLE001
                log.warning("heartbeat: %s", redact(str(e))[:200])
                rooms = []

    def loop(self):
        threading.Thread(target=self.heartbeat_loop, daemon=True).start()
        threading.Thread(target=self.outbox_loop, daemon=True).start()
        me = self.mx.whoami()["user_id"]
        log.info("worker %s as %s -> %s/%s", self.agent_ref, me, self.qp_url, self.qp_agent)
        since = self.ledger.kv_get("since")
        backoff = 1
        while not STOP.is_set():
            try:
                s = self.mx.sync(since, timeout_ms=int(os.environ.get("A2A_SYNC_TIMEOUT_MS", "20000")))
                for rid in (s.get("rooms", {}).get("invite") or {}):
                    try:
                        self.mx.join(rid)
                        log.info("joined %s", rid)
                    except Exception as e:  # noqa: BLE001
                        log.warning("join %s: %s", rid, e)
                if since:  # skip backlog on very first sync
                    for rid, room in (s.get("rooms", {}).get("join") or {}).items():
                        for ev in room.get("timeline", {}).get("events", []):
                            if ev.get("type") == EVENT_TYPE and ev.get("sender") != me:
                                self.handle(rid, ev.get("sender"), ev.get("content") or {})
                since = s.get("next_batch", since)
                self.ledger.kv_set("since", since)
                self.ledger.kv_set("heartbeat", now_iso())
                backoff = 1
            except Exception as e:  # noqa: BLE001
                log.warning("sync error: %s", redact(str(e))[:200])
                STOP.wait(backoff)
                backoff = min(backoff * 2, 15)


def main():
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    for sig in (signal.SIGTERM, signal.SIGINT):
        signal.signal(sig, lambda *_: STOP.set())
    Worker().loop()


if __name__ == "__main__":
    main()
