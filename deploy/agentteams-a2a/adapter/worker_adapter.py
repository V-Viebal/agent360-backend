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
from a2a_core import (EVENT_TYPE, MAX_INLINE_OUTPUT, MSG_CANCEL, MSG_REQUEST, MSG_RESULT, MSG_STATUS,
                      Ledger, MatrixClient, now_iso, redact)

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
A2A_RESULT {{"status":"completed","summary":"<one line>","evidence":[{{"type":"...","ref":"...","summary":"..."}}],"self_check":[{{"criterion":"...","result":"pass|fail"}}]}}"""


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


def run_qwenpaw(base: str, agent: str, req: dict, deadline: float, _retry: bool = True) -> tuple[str, str]:
    session = f"a2a-{req['correlation_id']}"
    body = json.dumps({
        "input": [{"role": "user", "content": [{"type": "text", "text": child_prompt(req)}]}],
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
            return run_qwenpaw(base, agent, req, deadline, _retry=False)
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

    def emit(self, room, typ, req, **kw):
        content = {"type": typ, "correlation_id": req["correlation_id"], "task_id": req.get("task_id"),
                   "child_agent_ref": self.agent_ref, "event_id": f"{req['correlation_id']}:{typ}:{kw.get('status','')}:{int(time.time()*1000)}",
                   "updated_at": now_iso(), **redact(kw)}
        content["body"] = f"[{typ}] {kw.get('status','')} {req['correlation_id']}"
        return self.mx.send(room, content)

    def handle(self, room, sender, content):
        typ = content.get("type")
        cid = content.get("correlation_id")
        if typ == MSG_CANCEL:
            CANCELLED.add(cid)
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
        threading.Thread(target=self._run, args=(room, content), daemon=True).start()

    def _run(self, room, req):
        with self.sem:
            cid = req["correlation_id"]
            try:
                deadline_at = req.get("deadline_at")
                deadline = (datetime.strptime(deadline_at, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc).timestamp()
                            if deadline_at else time.time() + 900)
                self.emit(room, MSG_STATUS, req, status="delivered", message="worker accepted request")
                self.emit(room, MSG_STATUS, req, status="running", message=f"running on {self.qp_agent}")
                text, session = run_qwenpaw(self.qp_url, self.qp_agent, req, deadline)
                if cid in CANCELLED:
                    self.emit(room, MSG_RESULT, req, status="cancelled", summary="cancelled by parent", output="")
                    return
                res = parse_result(text)
                out = res["output"]
                if len(out) > MAX_INLINE_OUTPUT:
                    out = out[:MAX_INLINE_OUTPUT] + "\n…[truncated]"
                status = "failed" if not text else ("completed" if res["status"] != "failed" else "failed")
                self.emit(room, MSG_RESULT, req, status=status, summary=res["summary"], output=out,
                          evidence=res["evidence"], self_check=res["self_check"], child_session_id=session,
                          completed_at=now_iso())
                log.info("result %s status=%s chars=%d", cid, status, len(out))
            except Exception as e:  # noqa: BLE001
                log.exception("task %s failed", cid)
                try:
                    self.emit(room, MSG_RESULT, req, status="failed", summary=f"worker error: {type(e).__name__}",
                              output=redact(str(e))[:2000])
                except Exception:
                    pass

    def heartbeat_loop(self):
        period = int(os.environ.get("A2A_HEARTBEAT_SECONDS", "60"))
        if period <= 0:
            return
        while not STOP.wait(period):
            try:
                for rid in self.mx.joined_rooms():
                    self.mx.send(rid, {"type": "agent360.worker.heartbeat.v1", "child_agent_ref": self.agent_ref,
                                       "at": now_iso(), "body": f"[heartbeat] {self.agent_ref}"})
                self.ledger.kv_set("heartbeat_sent", now_iso())
            except Exception as e:  # noqa: BLE001
                log.warning("heartbeat: %s", redact(str(e))[:200])

    def loop(self):
        threading.Thread(target=self.heartbeat_loop, daemon=True).start()
        me = self.mx.whoami()["user_id"]
        log.info("worker %s as %s -> %s/%s", self.agent_ref, me, self.qp_url, self.qp_agent)
        since = self.ledger.kv_get("since")
        while not STOP.is_set():
            try:
                s = self.mx.sync(since, timeout_ms=25000)
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
            except Exception as e:  # noqa: BLE001
                log.warning("sync error: %s", e)
                STOP.wait(5)


def main():
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    for sig in (signal.SIGTERM, signal.SIGINT):
        signal.signal(sig, lambda *_: STOP.set())
    Worker().loop()


if __name__ == "__main__":
    main()
