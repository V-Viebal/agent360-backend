#!/bin/bash
# Cron on the AgentTeams node (every 5 min): export the secret-free registry, push it to the
# Agent360 control plane, ensure all infra agents are joined to the Antares Fleet Room, and broadcast Matrix online presence.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
CP_SSH=${A2A_CP_SSH:-root@100.109.210.72}
OUT=/opt/agent360-a2a/registry.json
"$HERE/export_registry.sh" "$OUT" >/dev/null
scp -q -o BatchMode=yes "$OUT" "$CP_SSH:/opt/agent360-a2a/state/registry.json.new"
ssh -o BatchMode=yes "$CP_SSH" 'mv /opt/agent360-a2a/state/registry.json.new /opt/agent360-a2a/state/registry.json'

# Broadcast Matrix online presence & profile display name for all active infra agents
CTRL=${AGENTTEAMS_CONTROLLER:-$(docker ps --format "{{.Names}}" | grep -m1 "^agentteams-controller-")}
docker cp "$OUT" "$CTRL:/tmp/a2a_registry.json"
docker exec -i "$CTRL" python3 - << "PY" || true
import sys, os, json, urllib.request, urllib.parse
reg = json.load(open("/tmp/a2a_registry.json"))
as_tok = os.environ.get("AGENTTEAMS_MATRIX_APPSERVICE_AS_TOKEN", "").strip()
domain = os.environ.get("AGENTTEAMS_MATRIX_DOMAIN", "agentteams-gateway.hmz.one").strip()
if not as_tok:
    sys.exit(0)
headers = {"Authorization": f"Bearer {as_tok}", "Content-Type": "application/json"}
base = "http://127.0.0.1:6167/_matrix/client/v3"

roster_room = f"!JhOONvI3ghX7eKEJZE:{domain}"
enc_r = urllib.parse.quote(roster_room)
mgr_enc = urllib.parse.quote(f"@manager:{domain}")

extra = [
    {"matrix_user_id": f"@admin:{domain}", "display_name": "Admin Operator", "health": "online"},
    {"matrix_user_id": f"@manager:{domain}", "display_name": "AgentTeams Manager", "health": "online"},
    {"matrix_user_id": f"@mon:{domain}", "display_name": "Mons (Admin)", "health": "online"},
    {"matrix_user_id": f"@default-core:{domain}", "display_name": "Core Skyer Default", "health": "online"},
]

for item in (reg + extra):
    mxid = item.get("matrix_user_id")
    if not mxid or item.get("health") != "online":
        continue
    enc_u = urllib.parse.quote(mxid)
    # Ensure joined to Antares shared room so Tuwunel user directory & presence visibility work
    try:
        jr = json.loads(urllib.request.urlopen(urllib.request.Request(f"{base}/joined_rooms?user_id={enc_u}", headers=headers), timeout=5).read()).get("joined_rooms", [])
        if roster_room not in jr:
            try:
                urllib.request.urlopen(urllib.request.Request(f"{base}/rooms/{enc_r}/invite?user_id={mgr_enc}", data=json.dumps({"user_id": mxid}).encode(), headers=headers, method="POST"), timeout=5)
            except Exception:
                pass
            try:
                urllib.request.urlopen(urllib.request.Request(f"{base}/join/{enc_r}?user_id={enc_u}", data=b"{}", headers=headers, method="POST"), timeout=5)
            except Exception:
                pass
    except Exception:
        pass

    dname = item.get("display_name")
    if dname:
        try:
            req_p = urllib.request.Request(f"{base}/profile/{enc_u}/displayname?user_id={enc_u}", data=json.dumps({"displayname": dname}).encode(), headers=headers, method="PUT")
            urllib.request.urlopen(req_p, timeout=5)
        except Exception:
            pass
    try:
        req_s = urllib.request.Request(f"{base}/presence/{enc_u}/status?user_id={enc_u}", data=json.dumps({"presence": "online", "status_msg": "Active on Skyer Fleet"}).encode(), headers=headers, method="PUT")
        urllib.request.urlopen(req_s, timeout=5)
    except Exception:
        pass
PY

# Manager Matrix token guard: alert + self-heal a revoked token (never fails the registry sync).
"$HERE/manager_matrix_guard.sh" || true
