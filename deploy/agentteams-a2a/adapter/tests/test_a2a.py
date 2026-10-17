import json, os, sys, tempfile, threading, time, unittest, hmac, hashlib, urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import a2a_core as C

P, CH = "agent://core/mons", "agent://gm/default"


def ledger():
    d = tempfile.mkdtemp()
    L = C.Ledger(os.path.join(d, "t.db"))
    L.upsert_agent({"agent_ref": CH, "id": "x", "display_name": "GM", "runtime": "qwenpaw", "instance_id": "qwenpaw-gm",
                    "node": "6th", "agentteams_worker_id": "gm-default", "matrix_user_id": "@gm-default:hs",
                    "capabilities": ["deck"]})
    L.upsert_binding({"parent_agent_ref": P, "child_agent_ref": CH, "allowed_capabilities": ["deck:read"],
                      "max_parallel_tasks": 1, "timeout_seconds": 60})
    return L


def req(**kw):
    r = {"parent_agent_ref": P, "child_agent_ref": CH, "mode": "async", "objective": "Đếm card quá hạn",
         "constraints": ["Read-only"], "acceptance_criteria": ["có tổng số"], "capabilities": ["deck:read"]}
    r.update(kw)
    return r


def task(L, **kw):
    t = {"id": C.new_id("xdlg"), "correlation_id": C.new_id("a2a"), "root_run_id": "r", "parent_agent_ref": P,
         "child_agent_ref": CH, "transport": "m", "mode": "async", "objective": "o", "acceptance_criteria": ["c"],
         "write_intent": 0, "approved": 0, "status": "draft", "attempt": 1, "max_attempts": 2,
         "evidence": [], "self_check": [], "created_at": C.now_iso(), "updated_at": C.now_iso()}
    t.update(kw)
    return L.insert_task(t)


class Policy(unittest.TestCase):
    def test_allow(self):
        self.assertEqual(C.check_policy(ledger(), req()), (True, "ok"))

    def test_feature_flag(self):
        self.assertFalse(C.check_policy(ledger(), req(), enabled_flag=False)[0])

    def test_no_binding(self):
        self.assertIn("no enabled binding", C.check_policy(ledger(), req(child_agent_ref="agent://gm/other"))[1])

    def test_cycle(self):
        self.assertIn("cycle", C.check_policy(ledger(), req(call_chain=[P, CH]))[1])

    def test_depth(self):
        self.assertIn("depth", C.check_policy(ledger(), req(delegation_depth=2))[1])

    def test_capability_scope(self):
        self.assertIn("allowlist", C.check_policy(ledger(), req(capabilities=["deck:write"]))[1])

    def test_parallel(self):
        L = ledger(); task(L, status="running")
        self.assertIn("parallel", C.check_policy(L, req())[1])

    def test_secret_blocked(self):
        self.assertIn("secret", C.check_policy(ledger(), req(context="api_key=sk_live_ABCDEFGHIJKLMN"))[1])

    def test_write_needs_approval(self):
        r = req(objective="Xóa card cũ", constraints=[])
        self.assertIn("approval", C.check_policy(ledger(), r)[1])
        self.assertTrue(C.check_policy(ledger(), {**r, "approved": True})[0])

    def test_acceptance_required(self):
        self.assertIn("acceptance", C.check_policy(ledger(), req(acceptance_criteria=[]))[1])


class Redaction(unittest.TestCase):
    def test_redact(self):
        s = C.redact("Authorization: Bearer abcdefghijklmnop12345 token=supersecret123 ghp_AAAAAAAAAAAA")
        self.assertNotIn("abcdefghijklmnop12345", s); self.assertNotIn("supersecret123", s); self.assertNotIn("ghp_AAAA", s)

    def test_redact_keys(self):
        self.assertEqual(C.redact({"password": "x", "a": {"api_key": "y"}}), {"password": "<redacted>", "a": {"api_key": "<redacted>"}})


class StateAndEvents(unittest.TestCase):
    def test_legal_path(self):
        L = ledger(); t = task(L)
        for s in ["policy_checked", "sent", "delivered", "running", "completed", "under_review", "accepted"]:
            t = L.transition(t["id"], s)
        self.assertEqual(t["status"], "accepted")

    def test_illegal(self):
        L = ledger(); t = task(L)
        with self.assertRaises(ValueError):
            L.transition(t["id"], "accepted")

    def test_event_idempotent(self):
        L = ledger()
        self.assertTrue(L.record_event("e1", "c", "t", "s", {}))
        self.assertFalse(L.record_event("e1", "c", "t", "s", {}))

    def test_agent_ref(self):
        self.assertEqual(C.parse_agent_ref("agent://gm/default"), ("gm", "default"))
        with self.assertRaises(ValueError):
            C.parse_agent_ref("gm/default")


# ----------------------------------------------------- contract / integration with fakes
class FakeHS:
    """Minimal fake Matrix homeserver: rooms, send, sync, createRoom, alias, invite/join."""
    def __init__(self):
        self.events, self.rooms, self.aliases, self.invited, self.lock = [], {}, {}, {}, threading.Lock()
        self.tokens = {"tok-cp": "@agent360-a2a:hs", "tok-w": "@gm-default:hs"}
        hs = self

        class H(BaseHTTPRequestHandler):
            def log_message(self, *a): pass
            def _j(self, c, o):
                d = json.dumps(o).encode(); self.send_response(c); self.send_header("Content-Length", str(len(d))); self.end_headers(); self.wfile.write(d)
            def _u(self): return hs.tokens.get(self.headers.get("Authorization", "")[7:])
            def do_GET(self):
                u = self._u(); p = self.path.split("?")[0]
                if p.endswith("/account/whoami"): return self._j(200, {"user_id": u})
                if "/directory/room/" in p:
                    a = urllib.request.unquote(p.split("/directory/room/")[1])
                    return self._j(200, {"room_id": hs.aliases[a]}) if a in hs.aliases else self._j(404, {"errcode": "M_NOT_FOUND"})
                if p.endswith("/sync"):
                    from urllib.parse import parse_qs, urlsplit
                    q = parse_qs(urlsplit(self.path).query); since = int(q.get("since", ["0"])[0]); t0 = time.time()
                    while time.time() - t0 < 1.0:
                        with hs.lock:
                            new = [e for e in hs.events[since:] if u in hs.rooms[e["room"]]]
                            inv = {r: {} for r, m in hs.rooms.items() if u in hs.invited.get(r, set())}
                        if new or inv: break
                        time.sleep(0.05)
                    with hs.lock:
                        nb = len(hs.events)
                        join = {}
                        for e in new:
                            join.setdefault(e["room"], {"timeline": {"events": []}})["timeline"]["events"].append(e["ev"])
                    return self._j(200, {"next_batch": str(nb), "rooms": {"join": join, "invite": inv}})
                if "/profile/" in p: return self._j(200, {})
                self._j(404, {})
            def do_POST(self):
                u = self._u(); b = json.loads(self.rfile.read(int(self.headers.get("Content-Length") or 0)) or b"{}")
                p = self.path
                if p.endswith("/createRoom"):
                    rid = f"!r{len(hs.rooms)}:hs"; hs.rooms[rid] = {u}; hs.invited[rid] = set(b.get("invite", []))
                    hs.aliases[f"#{b['room_alias_name']}:agentteams-gateway.hmz.one"] = rid
                    return self._j(200, {"room_id": rid})
                if "/invite" in p:
                    rid = urllib.request.unquote(p.split("/rooms/")[1].split("/")[0]); hs.invited[rid].add(b["user_id"]); return self._j(200, {})
                if "/join/" in p:
                    rid = urllib.request.unquote(p.split("/join/")[1]); hs.rooms[rid].add(u); hs.invited[rid].discard(u); return self._j(200, {"room_id": rid})
                self._j(404, {})
            def do_PUT(self):
                u = self._u(); b = json.loads(self.rfile.read(int(self.headers.get("Content-Length") or 0)) or b"{}")
                rid = urllib.request.unquote(self.path.split("/rooms/")[1].split("/")[0])
                with hs.lock:
                    eid = f"$e{len(hs.events)}"
                    hs.events.append({"room": rid, "ev": {"type": C.EVENT_TYPE, "sender": u, "event_id": eid, "content": b}})
                self._j(200, {"event_id": eid})
        self.srv = ThreadingHTTPServer(("127.0.0.1", 0), H)
        self.url = f"http://127.0.0.1:{self.srv.server_port}"
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()


class FakeQwenPaw:
    def __init__(self, reply):
        self.reply, me = reply, self

        class H(BaseHTTPRequestHandler):
            def log_message(self, *a): pass
            def do_POST(self):
                self.rfile.read(int(self.headers.get("Content-Length") or 0))
                self.send_response(200); self.send_header("Content-Type", "text/event-stream"); self.end_headers()
                ev = {"object": "message", "type": "message", "role": "assistant", "status": "completed",
                      "content": [{"type": "text", "text": me.reply}]}
                self.wfile.write(b"data: " + json.dumps({"object": "message", "type": "reasoning", "role": "assistant", "status": "completed", "content": [{"type": "text", "text": "SECRET THINKING"}]}).encode() + b"\n\n")
                self.wfile.write(b"data: " + json.dumps(ev).encode() + b"\n\n")
            def do_GET(self):
                d = json.dumps({"agents": [{"id": "default", "enabled": True}]}).encode()
                self.send_response(200); self.send_header("Content-Length", str(len(d))); self.end_headers(); self.wfile.write(d)
        self.srv = ThreadingHTTPServer(("127.0.0.1", 0), H)
        self.url = f"http://127.0.0.1:{self.srv.server_port}"
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()


class EndToEnd(unittest.TestCase):
    def test_delegate_result_review_retry(self):
        hs = FakeHS()
        qp = FakeQwenPaw('Có 3 card quá hạn.\nA2A_RESULT {"status":"completed","summary":"3 card","evidence":[{"type":"deck_card","ref":"board:7/card:1","summary":"x"}],"self_check":[{"criterion":"có tổng số","result":"pass"}]}')
        d = tempfile.mkdtemp()
        json.dump({"homeserver": hs.url, "access_token": "tok-cp", "user_id": "@agent360-a2a:hs"}, open(f"{d}/cp.json", "w"))
        json.dump({"homeserver": hs.url, "access_token": "tok-w", "user_id": "@gm-default:hs"}, open(f"{d}/w.json", "w"))
        os.environ.update({"A2A_DB": f"{d}/cp.db", "A2A_MATRIX_CRED": f"{d}/cp.json",
                           "A2A_STATIC_AGENTS": json.dumps([{"agent_ref": CH, "id": "ext_gm_default", "display_name": "GM",
                               "runtime": "qwenpaw", "instance_id": "qwenpaw-gm", "node": "6th", "agentteams_worker_id": "gm-default",
                               "matrix_user_id": "@gm-default:hs", "capabilities": ["deck"]}]),
                           "A2A_QWENPAW_INSTANCES": json.dumps({"gm": qp.url})})
        import importlib, control_plane as CP, worker_adapter as W
        importlib.reload(CP)
        CP.L = C.Ledger(CP.CFG["db"]); CP.MX = C.MatrixClient(hs.url, "tok-cp", "@agent360-a2a:hs"); CP.discover()
        threading.Thread(target=CP.matrix_loop, daemon=True).start()
        os.environ.update({"A2A_MATRIX_CRED": f"{d}/w.json", "A2A_AGENT_REF": CH, "A2A_QWENPAW_URL": qp.url,
                           "A2A_QWENPAW_AGENT": "default", "A2A_ALLOWED_SENDERS": "@agent360-a2a:hs", "A2A_STATE_DIR": d})
        importlib.reload(W); w = W.Worker(); threading.Thread(target=w.loop, daemon=True).start()
        CP.L.upsert_binding({"parent_agent_ref": P, "child_agent_ref": CH, "timeout_seconds": 60})
        CP.binding_room(CP.L.binding(P, CH)); time.sleep(1.5)  # worker joins + first sync

        def wait_status(tid, st, n=40):
            for _ in range(n):
                t = CP.L.task(tid)
                if t["status"] == st: return t
                time.sleep(0.25)
            self.fail(f"status {CP.L.task(tid)['status']} != {st}")

        t, dup = CP.create_delegation({"parent_agent_ref": "mons", "child_agent_ref": CH, "objective": "Đếm card quá hạn",
                                       "constraints": ["Read-only"], "acceptance_criteria": ["có tổng số"],
                                       "correlation_id": "a2a_test_1"})
        self.assertEqual(t["status"], "sent")
        t = wait_status(t["id"], "under_review")
        self.assertIn("3 card", t["output"]); self.assertNotIn("SECRET THINKING", t["output"])
        self.assertEqual(t["evidence"][0]["ref"], "board:7/card:1")
        # idempotent create
        self.assertTrue(CP.create_delegation({"parent_agent_ref": "mons", "child_agent_ref": CH, "objective": "x",
                                              "acceptance_criteria": ["y"], "correlation_id": "a2a_test_1"})[1])
        # reject -> auto retry -> accept
        t = CP.review(t["id"], "reject", "Thiếu danh sách id")
        self.assertEqual((t["status"], t["attempt"]), ("sent", 2))
        t = wait_status(t["id"], "under_review")
        t = CP.review(t["id"], "accept")
        self.assertEqual(t["status"], "accepted")
        # duplicate events were not applied twice
        self.assertGreaterEqual(len(CP.L.q("SELECT * FROM a2a_events")), 4)
        # policy deny ledgered as cancelled
        t2, _ = CP.create_delegation({"parent_agent_ref": "mons", "child_agent_ref": CH, "objective": "Xóa card",
                                      "acceptance_criteria": ["done"]})
        self.assertEqual(t2["acceptance"]["decision"], "policy_denied")
        # skill render
        self.assertIn(CH, CP.render_skill(P))


# ----------------------------------------------------- contract tests (plan §14)
class _Hdr(dict):
    def get(self, k, d=None):
        return super().get(k, d)


class _Req:
    def __init__(self, raw, headers):
        self._raw, self.headers = raw, _Hdr(headers)


def _cp(tmp, **env):
    import importlib, control_plane as CP
    os.environ.update({"A2A_DB": f"{tmp}/cp.db", "A2A_CALLBACK_SECRET": "s3cr3t", "A2A_STATIC_AGENTS": "[]",
                       "A2A_SERVICE_TOKEN": "svc", "A2A_OPERATOR_TOKEN": "op", **env})
    importlib.reload(CP)
    CP.L = ledger()
    CP.MX = type("MX", (), {"user_id": "@cp:hs", "send": lambda *a, **k: "$ev", "_call": lambda *a, **k: {}})()
    return CP


def _sign(body, ts=None, secret="s3cr3t"):
    ts = str(int(ts if ts is not None else time.time()))
    raw = json.dumps(body).encode()
    sig = hmac.new(secret.encode(), f"{ts}.".encode() + raw, hashlib.sha256).hexdigest()
    return raw, {"X-A2A-Timestamp": ts, "X-A2A-Signature": "sha256=" + sig}


class Contract(unittest.TestCase):
    def setUp(self):
        self.CP = _cp(tempfile.mkdtemp())
        self.t = task(self.CP.L, status="sent")

    def ev(self, **kw):
        e = {"type": C.MSG_RESULT, "correlation_id": self.t["correlation_id"], "child_agent_ref": CH,
             "event_id": C.new_id("e"), "status": "completed", "output": "ok", "summary": "ok"}
        e.update(kw)
        return e

    def post(self, body, **kw):
        raw, h = _sign(body, **kw)
        return self.CP.r_events(_Req(raw, h), {}, body)

    def test_callback_valid_and_duplicate(self):
        e = self.ev(type=C.MSG_STATUS, status="running")
        self.assertEqual(self.post(e), (200, {"result": "applied"}))
        self.assertEqual(self.post(e), (200, {"result": "duplicate"}))
        self.assertEqual(self.CP.COUNTERS["duplicate_events"], 1)
        self.assertEqual(self.CP.L.task(self.t["id"])["status"], "running")

    def test_callback_bad_signature(self):
        raw, h = _sign(self.ev(), secret="wrong")
        code, _ = self.CP.r_events(_Req(raw, h), {}, self.ev())
        self.assertEqual(code, 401)
        self.assertEqual(self.CP.COUNTERS["callback_bad_signature"], 1)

    def test_callback_replay_window(self):
        code, body = self.post(self.ev(), ts=time.time() - 3600)
        self.assertEqual((code, body["error"]), (401, "stale timestamp"))

    def test_identity_mismatch(self):
        r = self.CP.apply_event(self.ev(child_agent_ref="agent://gm/other"), "test")
        self.assertEqual(r, "identity_mismatch")

    def test_result_completed_goes_under_review(self):
        self.CP.apply_event(self.ev(type=C.MSG_STATUS, status="running"), "t")
        self.CP.apply_event(self.ev(), "t")
        self.assertEqual(self.CP.L.task(self.t["id"])["status"], "under_review")

    def test_worker_failed_result(self):
        self.CP.apply_event(self.ev(type=C.MSG_STATUS, status="running"), "t")
        self.CP.apply_event(self.ev(status="failed", summary="worker error: Timeout"), "t")
        self.assertEqual(self.CP.L.task(self.t["id"])["status"], "failed")

    def test_late_event_after_terminal(self):
        self.CP.L.transition(self.t["id"], "cancelled")
        self.assertEqual(self.CP.apply_event(self.ev(), "t"), "late_event")

    def test_timeout_sweeper_logic(self):
        self.CP.L.x("UPDATE external_delegation_tasks SET deadline_at='2000-01-01T00:00:00Z' WHERE id=?", (self.t["id"],))
        import threading as th
        stop = th.Thread(target=self.CP.sweeper_loop, daemon=True); stop.start(); time.sleep(0.5)
        self.assertEqual(self.CP.L.task(self.t["id"])["status"], "timed_out")

    def test_cancel(self):
        self.assertEqual(self.CP.cancel(self.t["id"])["status"], "cancelled")

    def test_delivery_failed(self):
        def boom(*a, **k):
            raise RuntimeError("matrix down token=abc123secretvalue")
        self.CP.MX.send = boom
        self.CP.L.transition(self.t["id"], "cancelled")  # free the parallel slot
        self.CP.L.x("UPDATE external_subagent_bindings SET matrix_room_id='!r:hs'")
        t, _ = self.CP.create_delegation({"parent_agent_ref": P, "child_agent_ref": CH, "objective": "Đếm card",
                                          "acceptance_criteria": ["x"], "capabilities": ["deck:read"]})
        self.assertEqual(t["status"], "delivery_failed")

    def test_auth(self):
        a = self.CP.authorized
        self.assertTrue(a("GET", "/api/v1/a2a/health", ""))
        self.assertTrue(a("GET", "/api/v1/a2a/external-agents", ""))
        self.assertFalse(a("POST", "/api/v1/a2a/external-agents/x/probe", ""))
        self.assertFalse(a("POST", "/api/v1/a2a/delegations", ""))
        self.assertFalse(a("GET", "/api/v1/agents/mons/a2a/delegations", "Bearer nope"))
        self.assertTrue(a("POST", "/api/v1/a2a/delegations", "Bearer svc"))
        self.assertTrue(a("POST", "/api/v1/a2a/delegations/x/review", "Bearer op"))
        self.assertTrue(a("POST", "/api/v1/a2a/agentteams/events", ""))

    def test_metrics_and_health(self):
        self.CP.L.transition(self.t["id"], "timed_out")
        m = self.CP.metrics()
        self.assertEqual(m["by_status"].get("timed_out"), 1)
        self.assertIn("counters", m)
        h = self.CP.health()
        self.assertEqual(h["status"], "degraded")
        self.assertTrue(any(r.startswith("agentteams_matrix_sync_stale") for r in h["reasons"]))


class BindingMerge(unittest.TestCase):
    def test_partial_put_keeps_fields(self):
        L = ledger()
        L.upsert_binding({"parent_agent_ref": P, "child_agent_ref": CH, "role": "gm-operations", "matrix_room_id": "!r:hs"})
        b = L.upsert_binding({"parent_agent_ref": P, "child_agent_ref": CH, "timeout_seconds": 45})
        self.assertEqual((b["role"], b["timeout_seconds"], b["allowed_capabilities"], b["matrix_room_id"]),
                         ("gm-operations", 45, ["deck:read"], "!r:hs"))


class McpServer(unittest.TestCase):
    def setUp(self):
        import importlib, mcp_server as M
        self.sent = []
        importlib.reload(M)
        def api(method, path, body=None, timeout=60):
            if path.endswith("/external-subagents"):
                return [{"child_agent_ref": CH, "allowed_capabilities": ["deck:read", "worktime:read"]}]
            if method == "POST" and path.endswith("/a2a/delegations"):
                self.sent.append(body)
                return {"id": "t1", "correlation_id": "c1", "status": "sent", "child_agent_ref": CH}
            return {}
        M.api = api
        self.M = M

    def test_tools_list_exposes_allowlist(self):
        t = [x for x in self.M.tools_list() if x["name"] == "agent360_delegate_external"][0]["inputSchema"]["properties"]
        self.assertEqual(t["capabilities"]["items"]["enum"], ["deck:read", "worktime:read"])
        self.assertEqual(t["child_agent_ref"]["enum"], [CH])

    def test_unknown_capabilities_dropped(self):
        out = self.M.call_tool("agent360_delegate_external", {"child_agent_ref": CH, "objective": "o",
                               "acceptance_criteria": ["a"], "capabilities": ["hr-ops", "deck:read"]})
        self.assertEqual(self.sent[0]["capabilities"], ["deck:read"])
        self.assertEqual(out["ignored_capabilities"], ["hr-ops"])
        self.assertEqual(self.sent[0]["parent_agent_ref"], self.M.PARENT)


class WorkerParsing(unittest.TestCase):
    def test_malformed_output(self):
        import worker_adapter as W
        r = W.parse_result("Plain answer without trailer")
        self.assertEqual((r["status"], r["output"], r["evidence"]), ("completed", "Plain answer without trailer", []))
        r = W.parse_result('x\nA2A_RESULT {not json}')
        self.assertEqual(r["evidence"], [])

    def test_approval_required_in_prompt(self):
        import worker_adapter as W
        p = W.child_prompt({"correlation_id": "c", "parent_agent_ref": P, "objective": "o", "acceptance_criteria": ["a"]})
        self.assertIn("do not perform write", p)


if __name__ == "__main__":
    unittest.main(verbosity=1)
