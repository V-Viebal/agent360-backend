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
    def __init__(self, reply, script=None):
        self.reply, me = reply, self
        self.script = list(script or [])   # successive replies (multi-turn); falls back to `reply`
        self.prompts = []                  # (session_id, text)

        class H(BaseHTTPRequestHandler):
            def log_message(self, *a): pass
            def do_POST(self):
                b = json.loads(self.rfile.read(int(self.headers.get("Content-Length") or 0)) or b"{}")
                me.prompts.append((b.get("session_id"), b["input"][0]["content"][0]["text"]))
                if me.script:
                    me.reply = me.script.pop(0)
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

    def test_event_recovers_send_timeout(self):
        # Matrix PUT timed out client-side (delivery_failed) but the child received it and answered.
        self.CP.L.x("UPDATE external_delegation_tasks SET status='delivery_failed' WHERE id=?", (self.t["id"],))
        for st in ("delivered", "running"):
            self.assertEqual(self.CP.apply_event(self.ev(type=C.MSG_STATUS, status=st), "t"), "applied")
        self.assertEqual(self.CP.apply_event(self.ev(), "t"), "applied")
        self.assertEqual(self.CP.L.task(self.t["id"])["status"], "under_review")

    def test_event_while_send_in_flight(self):
        self.CP.L.x("UPDATE external_delegation_tasks SET status='policy_checked' WHERE id=?", (self.t["id"],))
        self.assertEqual(self.CP.apply_event(self.ev(type=C.MSG_STATUS, status="running"), "t"), "applied")
        self.assertEqual(self.CP.L.task(self.t["id"])["status"], "running")

    def test_send_retries_same_txn(self):
        calls = []
        def flaky(room, content, txn_id=None):
            calls.append(txn_id)
            if len(calls) < 2:
                raise RuntimeError("The read operation timed out")
            return "$ev"
        self.CP.MX.send = flaky
        self.CP.L.transition(self.t["id"], "cancelled")
        self.CP.L.x("UPDATE external_subagent_bindings SET matrix_room_id='!r:hs'")
        t, _ = self.CP.create_delegation({"parent_agent_ref": P, "child_agent_ref": CH, "objective": "Đếm card",
                                          "acceptance_criteria": ["x"], "capabilities": ["deck:read"]})
        self.assertEqual(t["status"], "sent")
        self.assertEqual(len(set(calls)), 1)

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


class RegistryDrift(unittest.TestCase):
    def test_missing_identity_and_stale_registry(self):
        d = tempfile.mkdtemp(); reg = os.path.join(d, "registry.json")
        json.dump([{"agent_ref": CH, "matrix_user_id": "@gm-default:hs"}, {"agent_ref": "agent://core/ken", "matrix_user_id": None}], open(reg, "w"))
        CP = _cp(d, A2A_REGISTRY_FILE=reg, A2A_REGISTRY_STALE_SECONDS="60")
        r = CP.registry_status()
        self.assertEqual((r["stale"], r["missing_matrix_identity"]), (False, ["agent://core/ken"]))
        self.assertIn("agentteams_identity_missing:agent://core/ken", CP.health()["reasons"])
        os.utime(reg, (time.time() - 3600, time.time() - 3600))
        self.assertTrue(any(x.startswith("agentteams_registry_stale") for x in CP.health()["reasons"]))


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
        def api(method, path, body=None, timeout=60, **kw):
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


class McpReviewErrors(unittest.TestCase):
    def test_review_error_not_swallowed(self):
        import importlib, mcp_server as M
        importlib.reload(M)
        M.api = lambda *a, **k: {"error": {"error": "not found: 'xdlg_bad'"}, "http_status": 404}
        out = M.call_tool("agent360_review_external_delegation", {"task_id": "xdlg_bad", "decision": "accept"})
        self.assertIn("error", out)
        self.assertEqual(out["http_status"], 404)
        M.api = lambda *a, **k: {"id": "x", "status": "accepted"}
        self.assertEqual(M.call_tool("agent360_review_external_delegation",
                                     {"task_id": "x", "decision": "accept"})["status"], "accepted")


class Transport(unittest.TestCase):
    def setUp(self):
        self.hs = FakeHS()

    def test_keepalive_reuses_socket(self):
        mx = C.MatrixClient(self.hs.url, "tok-cp", "@agent360-a2a:hs")
        mx.whoami(); s1 = mx._local.public.sock; mx.whoami()
        self.assertIs(mx._local.public.sock, s1)
        self.assertEqual(mx.stats["public_ok"], 2)

    def test_origin_pin_direct_then_fallback(self):
        port = self.hs.srv.server_port
        mx = C.MatrixClient(f"http://localhost:{port}", "tok-cp", "@agent360-a2a:hs", origin_ip="127.0.0.1")
        self.assertEqual(mx.whoami()["user_id"], "@agent360-a2a:hs")
        self.assertEqual(mx.stats["last_path"], "direct")
        dead = C.MatrixClient(f"http://localhost:{port}", "tok-cp", "@agent360-a2a:hs", origin_ip="127.0.0.9")
        dead._port = port
        # direct IP refuses -> public hostname answers; circuit opens after 2 direct failures
        self.assertEqual(dead.whoami()["user_id"], "@agent360-a2a:hs")
        self.assertEqual(dead.stats["last_path"], "public")
        self.assertGreater(dead._direct_down_until, time.time())
        self.assertEqual(dead._paths(), ["public", "public"])

    def test_retryable_cdn_status(self):
        calls = []
        class H(BaseHTTPRequestHandler):
            def log_message(self, *a): pass
            def do_GET(self):
                calls.append(1)
                code, body = (525, b"cf") if len(calls) == 1 else (200, b'{"user_id": "@x:hs"}')
                self.send_response(code); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
        srv = ThreadingHTTPServer(("127.0.0.1", 0), H); threading.Thread(target=srv.serve_forever, daemon=True).start()
        mx = C.MatrixClient(f"http://127.0.0.1:{srv.server_port}", "t", "@x:hs")
        self.assertEqual(mx.whoami()["user_id"], "@x:hs")
        self.assertEqual(len(calls), 2)

    def test_send_reliable_backoff_same_txn(self):
        txns = []
        def flaky(room, content, txn_id=None):
            txns.append(txn_id)
            if len(txns) < 3:
                raise RuntimeError("timeout")
            return "$ok"
        self.assertEqual(C.send_reliable(flaky, "!r", {}, "t1", 30), "$ok")
        self.assertEqual(set(txns), {"t1"})


class WorkerOutbox(unittest.TestCase):
    def test_failed_emit_is_redelivered(self):
        import importlib, worker_adapter as W
        importlib.reload(W)
        tmp = tempfile.mkdtemp()
        w = W.Worker.__new__(W.Worker)
        w.agent_ref, w.ledger, w.flush_now = CH, C.Ledger(os.path.join(tmp, "w.db")), threading.Event()
        sent, fail = [], {"on": True}
        class MX:
            def send(self, room, content, txn_id=None):
                if fail["on"]:
                    raise RuntimeError("handshake timed out")
                sent.append((txn_id, content["status"]))
                return "$e"
        w.mx = MX()
        orig = W.send_reliable
        W.send_reliable = lambda send, r, c, t, b: send(r, c, txn_id=t)
        try:
            self.assertIsNone(w.emit("!r", C.MSG_RESULT, {"correlation_id": "c1", "attempt": 1}, status="completed", output="x"))
            self.assertEqual(len(w.ledger.q("SELECT k FROM a2a_kv WHERE k LIKE 'outbox:%'")), 1)
            fail["on"] = False
            W.STOP.clear()
            th = threading.Thread(target=w.outbox_loop, daemon=True); th.start(); w.flush_now.set(); time.sleep(0.5)
            W.STOP.set(); w.flush_now.set(); th.join(2); W.STOP.clear()
        finally:
            W.send_reliable = orig
        self.assertEqual(sent, [("w-c1_1_agent360.delegation.result.v1_completed", "completed")])
        self.assertEqual(w.ledger.q("SELECT k FROM a2a_kv WHERE k LIKE 'outbox:%'"), [])


class McpTransientErrors(unittest.TestCase):
    def test_delegate_sends_correlation_id_and_wait_survives_502(self):
        import importlib, mcp_server as M
        importlib.reload(M)
        calls = {"get": 0}; sent = []
        def api(method, path, body=None, timeout=60):
            if path.endswith("/external-subagents"):
                return []
            if method == "POST":
                sent.append(body); return {"id": "t1", "correlation_id": body["correlation_id"], "status": "sent"}
            calls["get"] += 1
            if calls["get"] == 1:
                return {"error": "HTTP 502", "http_status": 502}
            return {"id": "t1", "correlation_id": sent[0]["correlation_id"], "status": "under_review", "output": "ok"}
        M.api = api
        orig_sleep = M.time.sleep
        M.time.sleep = lambda s: None
        try:
            out = M.call_tool("agent360_delegate_external", {"child_agent_ref": CH, "objective": "o",
                                                             "acceptance_criteria": ["a"], "wait_seconds": 30})
        finally:
            M.time.sleep = orig_sleep
        self.assertTrue(sent[0]["correlation_id"].startswith("a2a_"))
        self.assertEqual(out["status"], "under_review")


class McpInternalRoute(unittest.TestCase):
    def test_origin_ip_preferred_then_public_fallback(self):
        import importlib, mcp_server as M
        hits = []
        class H(BaseHTTPRequestHandler):
            def log_message(self, *a): pass
            def do_GET(self):
                hits.append(self.headers.get("Host")); b = b'{"ok": true}'
                self.send_response(200); self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)
        srv = ThreadingHTTPServer(("127.0.0.1", 0), H); threading.Thread(target=srv.serve_forever, daemon=True).start()
        os.environ["A2A_API_URL"] = f"http://cp.example:{srv.server_port}"
        os.environ["A2A_API_ORIGIN_IP"] = "127.0.0.1"
        try:
            importlib.reload(M)
            self.assertEqual(M.api("GET", "/api/v1/a2a/health"), {"ok": True})
            self.assertEqual(M.TRANSPORT["internal_ok"], 1)
            self.assertEqual(hits, [f"cp.example:{srv.server_port}"])
            os.environ["A2A_API_ORIGIN_IP"] = "127.0.0.9"; os.environ["A2A_API_URL"] = f"http://127.0.0.1:{srv.server_port}"
            importlib.reload(M)
            self.assertEqual(M.api("GET", "/api/v1/a2a/health"), {"ok": True})
            self.assertEqual((M.TRANSPORT["internal_fail"], M.TRANSPORT["public_ok"]), (1, 1))
        finally:
            os.environ.pop("A2A_API_ORIGIN_IP", None); os.environ.pop("A2A_API_URL", None); importlib.reload(M)


class MultiTurn(unittest.TestCase):
    """v2: child asks -> parent answers -> result -> follow-up, same QwenPaw session, real Matrix fake."""

    def setUp(self):
        self.hs = FakeHS()
        self.qp = FakeQwenPaw("unused", script=[
            'Cần biết board.\nA2A_ASK {"question":"Board nào?","options":["Ops","Sales"],"blocking":true,"why":"ambiguous"}',
            'Board Ops có 5 card.\nA2A_RESULT {"status":"completed","summary":"Ops: 5","self_check":[{"criterion":"có tổng số","result":"pass"}]}',
            'Trong 5 card, 2 card quá hạn.\nA2A_RESULT {"status":"completed","summary":"2 overdue"}',
        ])
        d = self.d = tempfile.mkdtemp()
        json.dump({"homeserver": self.hs.url, "access_token": "tok-cp", "user_id": "@agent360-a2a:hs"}, open(f"{d}/cp.json", "w"))
        json.dump({"homeserver": self.hs.url, "access_token": "tok-w", "user_id": "@gm-default:hs"}, open(f"{d}/w.json", "w"))
        os.environ.update({"A2A_DB": f"{d}/cp.db", "A2A_MATRIX_CRED": f"{d}/cp.json", "A2A_WAKE_PARENT": "false",
                           "A2A_STATIC_AGENTS": json.dumps([{"agent_ref": CH, "id": "ext_gm_default", "display_name": "GM",
                               "runtime": "qwenpaw", "instance_id": "qwenpaw-gm", "node": "6th", "agentteams_worker_id": "gm-default",
                               "matrix_user_id": "@gm-default:hs", "capabilities": ["deck"]}]),
                           "A2A_QWENPAW_INSTANCES": json.dumps({"gm": self.qp.url})})
        import importlib, control_plane as CP, worker_adapter as W
        importlib.reload(CP)
        CP.L = C.Ledger(CP.CFG["db"]); CP.MX = C.MatrixClient(self.hs.url, "tok-cp", "@agent360-a2a:hs"); CP.discover()
        threading.Thread(target=CP.matrix_loop, daemon=True).start()
        os.environ.update({"A2A_MATRIX_CRED": f"{d}/w.json", "A2A_AGENT_REF": CH, "A2A_QWENPAW_URL": self.qp.url,
                           "A2A_QWENPAW_AGENT": "default", "A2A_ALLOWED_SENDERS": "@agent360-a2a:hs", "A2A_STATE_DIR": d})
        importlib.reload(W); self.W = W; self.w = W.Worker(); threading.Thread(target=self.w.loop, daemon=True).start()
        CP.L.upsert_binding({"parent_agent_ref": P, "child_agent_ref": CH, "timeout_seconds": 60})
        CP.binding_room(CP.L.binding(P, CH)); time.sleep(1.5)
        self.CP = CP

    def tearDown(self):
        os.environ.pop("A2A_WAKE_PARENT", None)

    def wait_status(self, tid, st, n=60):
        for _ in range(n):
            t = self.CP.L.task(tid)
            if t["status"] == st:
                return t
            time.sleep(0.25)
        self.fail(f"status {self.CP.L.task(tid)['status']} != {st}")

    def test_question_answer_result_followup(self):
        CP = self.CP
        t, _ = CP.create_delegation({"parent_agent_ref": "mons", "child_agent_ref": CH, "objective": "Đếm card",
                                     "constraints": ["Read-only"], "acceptance_criteria": ["có tổng số"],
                                     "correlation_id": "a2a_mt_1"})
        t = self.wait_status(t["id"], "awaiting_parent")
        q = CP.L.open_question("a2a_mt_1")
        self.assertEqual((q["text"], q["options"], q["blocking"]), ("Board nào?", ["Ops", "Sales"], True))
        self.assertEqual([i["task_id"] for i in CP.inbox(P)], [t["id"]])
        self.assertIsNone(self.w.parked("a2a_mt_0"))
        self.assertIsNotNone(self.w.parked("a2a_mt_1"))           # parked, no slot held
        # follow-up is not allowed while a question is open
        with self.assertRaises(ValueError):
            CP.post_message(t["id"], {"kind": "followup", "text": "x"})
        r = CP.post_message(t["id"], {"kind": "answer", "text": "Ops", "message_id": "m1"}, "agent:mons")
        self.assertEqual(r["task"]["status"], "running")
        self.assertTrue(CP.post_message(t["id"], {"kind": "answer", "text": "Ops", "message_id": "m1"}, "agent:mons").get("duplicate"))
        t = self.wait_status(t["id"], "under_review")
        self.assertIn("Ops có 5 card", t["output"])
        r = CP.post_message(t["id"], {"kind": "followup", "text": "Bao nhiêu card quá hạn? (read-only)"}, "agent:mons")
        t = self.wait_status(t["id"], "under_review")
        self.assertIn("2 card quá hạn", t["output"])
        self.assertEqual(t["attempt"], 1)                           # follow-up != reject
        sessions = {s for s, _ in self.qp.prompts}
        self.assertEqual(sessions, {"a2a-a2a_mt_1"})                # one child session for the whole thread
        self.assertIn("[Parent answer to turn", self.qp.prompts[1][1])
        self.assertIn("[Parent follow-up", self.qp.prompts[2][1])
        kinds = [(x["role"], x["kind"]) for x in CP.L.turns("a2a_mt_1")]
        self.assertEqual(kinds, [("parent", "request"), ("child", "question"), ("parent", "answer"),
                                 ("child", "result"), ("parent", "followup"), ("child", "result")])
        self.assertEqual(CP.review(t["id"], "accept")["status"], "accepted")
        m = CP.metrics()
        self.assertGreaterEqual(m["conversation"]["multi_turn_tasks"], 1)
        self.assertIsNotNone(m["conversation"]["question_answer_seconds"]["p50"])

    def test_write_followup_denied_and_operator_only_approval(self):
        CP = self.CP
        self.qp.script = ['Cần chọn.\nA2A_ASK {"question":"Board?","blocking":true}']
        t, _ = CP.create_delegation({"parent_agent_ref": "mons", "child_agent_ref": CH, "objective": "Đếm card",
                                     "constraints": ["Read-only"], "acceptance_criteria": ["x"], "correlation_id": "a2a_mt_2"})
        t = self.wait_status(t["id"], "awaiting_parent")
        self.assertEqual(CP.post_message(t["id"], {"kind": "redirect", "text": "Xóa hết card cũ"})["error"], "policy_denied")
        self.assertEqual(CP.post_message(t["id"], {"kind": "approval", "text": "ok"}, "agent:mons")["error"], "policy_denied")
        r = CP.post_message(t["id"], {"kind": "escalation", "text": "needs a human"}, "agent:mons")
        self.assertTrue(r["escalated"])
        self.assertEqual(CP.L.task(t["id"])["status"], "awaiting_parent")     # still waiting, now for the operator
        self.assertIsNotNone(CP.L.open_question("a2a_mt_2"))
        self.assertEqual(len(self.qp.prompts), 1)                              # child was not resumed

    def test_question_timeout_proceeds_for_read_only(self):
        CP = self.CP
        self.qp.script = ['A2A_ASK {"question":"Board?","blocking":true}', 'Giả định Ops: 5.\nA2A_RESULT {"status":"completed","summary":"assumed Ops"}']
        t, _ = CP.create_delegation({"parent_agent_ref": "mons", "child_agent_ref": CH, "objective": "Đếm card",
                                     "constraints": ["Read-only"], "acceptance_criteria": ["x"], "correlation_id": "a2a_mt_3",
                                     "conversation": {"question_timeout_seconds": 1}})
        t = self.wait_status(t["id"], "awaiting_parent")
        CP.L.x("UPDATE external_delegation_tasks SET question_deadline_at='2000-01-01T00:00:00Z' WHERE id=?", (t["id"],))
        CP.question_timeout(CP.L.task(t["id"]))
        t = self.wait_status(t["id"], "under_review")
        self.assertIn("Giả định Ops", t["output"])
        self.assertEqual(CP.L.turns("a2a_mt_3")[2]["role"], "system")

    def test_v1_child_without_ask_unchanged(self):
        CP = self.CP
        self.qp.script = ['Có 3.\nA2A_RESULT {"status":"completed","summary":"3"}']
        t, _ = CP.create_delegation({"parent_agent_ref": "mons", "child_agent_ref": CH, "objective": "Đếm card",
                                     "constraints": ["Read-only"], "acceptance_criteria": ["x"], "correlation_id": "a2a_mt_4"})
        t = self.wait_status(t["id"], "under_review")
        self.assertEqual([x["kind"] for x in CP.L.turns("a2a_mt_4")], ["request", "result"])


class AskParsing(unittest.TestCase):
    def test_parse_ask_variants(self):
        import importlib, worker_adapter as W
        importlib.reload(W)
        a = W.parse_ask('Cần chọn.\nA2A_ASK {"question":"Q?","options":["a","b"],"blocking":false}')
        self.assertEqual((a["kind"], a["text"], a["options"], a["blocking"], a["preamble"]), ("question", "Q?", ["a", "b"], False, "Cần chọn."))
        self.assertEqual(W.parse_ask('A2A_APPROVAL {"question":"Xóa 3 card?"}')["kind"], "approval_request")
        self.assertIsNone(W.parse_ask('xong\nA2A_RESULT {"status":"completed"}'))
        self.assertIsNone(W.parse_ask('A2A_ASK {"question":""}'))
        self.assertIsNone(W.parse_ask('A2A_ASK {bad json'))
        req = {"correlation_id": "c", "parent_agent_ref": P, "protocol": 2, "conversation": {"max_questions": 3, "question_count": 3}}
        self.assertIn("may not ask further", W.child_prompt(req))
        self.assertNotIn("A2A_ASK", W.child_prompt({**req, "protocol": 1}))


class McpNextStep(unittest.TestCase):
    def test_under_review_requires_explicit_review(self):
        import importlib, mcp_server as M
        importlib.reload(M)
        M.api = lambda *a, **k: {"id": "t1", "correlation_id": "c1", "status": "under_review", "turn_count": 1}
        out = M.call_tool("agent360_check_external_delegation", {"task_id": "t1"})
        self.assertIn("NOT reviewed", out["next_step"])


class McpHandshakeFast(unittest.TestCase):
    def test_tools_list_does_not_retry_when_api_down(self):
        import importlib, mcp_server as M
        os.environ["A2A_API_URL"] = "http://127.0.0.1:9"
        try:
            importlib.reload(M)
            t0 = time.time()
            tools = M.tools_list()
            self.assertLess(time.time() - t0, 5)
            self.assertTrue(any(t["name"] == "agent360_reply_external_delegation" for t in tools))
        finally:
            os.environ.pop("A2A_API_URL", None); importlib.reload(M)


class WorkerAdmission(unittest.TestCase):
    def setUp(self):
        import worker_adapter as W
        self.W = W
        self.L = C.Ledger(os.path.join(tempfile.mkdtemp(), "w.db"))
        self.req = {"correlation_id": "c", "root_run_id": "r", "parent_agent_ref": "agent://gm/team",
                    "child_agent_ref": "agent://gm/default", "delegation_depth": 1,
                    "call_chain": ["agent://gm/team", "agent://gm/default"]}

    def test_depth_cycle_and_circuit(self):
        self.assertIsNone(self.W.admission_error(self.req, "agent://gm/default", self.L, now=100))
        self.assertIn("cycle", self.W.admission_error({**self.req, "delegation_depth": 2,
            "call_chain": ["agent://gm/default", "agent://gm/team", "agent://gm/default"]}, "agent://gm/default", self.L))
        self.assertIn("depth", self.W.admission_error({**self.req, "delegation_depth": 3,
            "call_chain": ["a", "b", "c", "agent://gm/default"]}, "agent://gm/default", self.L))
        self.assertIsNone(self.W.admission_error({**self.req, "correlation_id": "c2"}, "agent://gm/default", self.L, now=101))
        self.assertIsNone(self.W.admission_error({**self.req, "correlation_id": "c3"}, "agent://gm/default", self.L, now=102))
        self.assertIn("circuit open", self.W.admission_error({**self.req, "correlation_id": "c4"}, "agent://gm/default", self.L, now=103))


class WorkerMeshRender(unittest.TestCase):
    def test_render_isolated_mesh(self):
        import importlib.util
        path = os.path.join(os.path.dirname(__file__), "..", "..", "scripts", "render_worker_mesh.py")
        spec = importlib.util.spec_from_file_location("render_worker_mesh", path)
        mod = importlib.util.module_from_spec(spec); spec.loader.exec_module(mod)
        out = mod.render("gm", ["default", "team"], "qp", "net")
        self.assertEqual(out.count("container_name:"), 2)
        self.assertIn('A2A_STATE_DIR: "/state/gm-team"', out)
        self.assertIn("@gm-default:agentteams-gateway.hmz.one", out)
        self.assertIn('A2A_CIRCUIT_MAX: "3"', out)
        with self.assertRaises(ValueError):
            mod.render("gm", ["default", "default"], "qp", "net")


class WorkerHttpErrors(unittest.TestCase):
    def test_disabled_agent_error_is_actionable(self):
        import io, urllib.error, worker_adapter as W
        orig = W.urllib.request.urlopen
        def deny(*a, **k):
            raise urllib.error.HTTPError("http://qp", 403, "Forbidden", {}, io.BytesIO(b'{"detail":"Agent \'supplier\' is disabled"}'))
        W.urllib.request.urlopen = deny
        try:
            with self.assertRaisesRegex(RuntimeError, "Agent 'supplier' is disabled"):
                W.run_qwenpaw("http://qp", "supplier", {"correlation_id": "c", "parent_agent_ref": P}, time.time() + 60, text="ping")
        finally:
            W.urllib.request.urlopen = orig


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
