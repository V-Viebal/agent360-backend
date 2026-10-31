#!/bin/bash
# Cron on the AgentTeams node (every 5 min): export the secret-free registry, push it to the
# Agent360 control plane, ensure all infra agents + control plane are joined to the Antares Fleet Room,
# broadcast Matrix online presence, and record Matrix room retention audit metrics.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
CP_SSH=${A2A_CP_SSH:-root@100.109.210.72}
OUT=/opt/agent360-a2a/registry.json
AUDIT_OUT=/opt/agent360-a2a/room-audit.json
"$HERE/export_registry.sh" "$OUT" >/dev/null
scp -q -o BatchMode=yes "$OUT" "$CP_SSH:/opt/agent360-a2a/state/registry.json.new"
ssh -o BatchMode=yes "$CP_SSH" 'mv /opt/agent360-a2a/state/registry.json.new /opt/agent360-a2a/state/registry.json'

# Broadcast Matrix online presence & profile display name + compute room retention audit
CTRL=${AGENTTEAMS_CONTROLLER:-$(docker ps --format "{{.Names}}" | grep -m1 "^agentteams-controller-")}
docker cp "$OUT" "$CTRL:/tmp/a2a_registry.json"
docker exec -i "$CTRL" python3 - << "PY" > "$AUDIT_OUT.tmp" || true
import sys, os, json, urllib.request, urllib.parse
from datetime import datetime, timezone
reg = json.load(open("/tmp/a2a_registry.json"))
as_tok = os.environ.get("AGENTTEAMS_MATRIX_APPSERVICE_AS_TOKEN", "").strip()
domain = os.environ.get("AGENTTEAMS_MATRIX_DOMAIN", "agentteams-gateway.hmz.one").strip()
if not as_tok:
    print(json.dumps({"error": "missing_as_token"}))
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
    {"matrix_user_id": f"@agent360-a2a:{domain}", "display_name": "Agent360 A2A Control Plane", "health": "online"},
]

room_counts = {}
for item in (reg + extra):
    mxid = item.get("matrix_user_id")
    if not mxid or item.get("health") != "online":
        continue
    enc_u = urllib.parse.quote(mxid)
    try:
        jr = json.loads(urllib.request.urlopen(urllib.request.Request(f"{base}/joined_rooms?user_id={enc_u}", headers=headers), timeout=5).read()).get("joined_rooms", [])
        if roster_room not in jr:
            try:
                urllib.request.urlopen(urllib.request.Request(f"{base}/rooms/{enc_r}/invite?user_id={mgr_enc}", data=json.dumps({"user_id": mxid}).encode(), headers=headers, method="POST"), timeout=5)
            except Exception:
                pass
            try:
                urllib.request.urlopen(urllib.request.Request(f"{base}/join/{enc_r}?user_id={enc_u}", data=b"{}", headers=headers, method="POST"), timeout=5)
                jr.append(roster_room)
            except Exception:
                pass
        room_counts[mxid] = len(set(jr))
    except Exception:
        room_counts[mxid] = 0

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

max_rooms = max(room_counts.values()) if room_counts else 0
threshold = 150
print(json.dumps({
    "checked_at": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    "max_joined_rooms": max_rooms,
    "threshold": threshold,
    "exceeded": max_rooms > threshold,
    "room_counts": room_counts,
}))
PY
if [ -s "$AUDIT_OUT.tmp" ]; then
  mv "$AUDIT_OUT.tmp" "$AUDIT_OUT"
  scp -q -o BatchMode=yes "$AUDIT_OUT" "$CP_SSH:/opt/agent360-a2a/state/room-audit.json.new"
  ssh -o BatchMode=yes "$CP_SSH" 'mv /opt/agent360-a2a/state/room-audit.json.new /opt/agent360-a2a/state/room-audit.json'
fi

# Manager Matrix token guard: alert + self-heal a revoked token (never fails the registry sync).
"$HERE/manager_matrix_guard.sh" || true
