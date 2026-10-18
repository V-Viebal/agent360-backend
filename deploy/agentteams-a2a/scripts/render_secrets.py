#!/usr/bin/env python3
"""Render external-A2A runtime secret files from Infisical (source of truth) to the node that uses them.

Usage (from the workspace, INFISICAL_API_URL/CLIENT_ID/CLIENT_SECRET in env):
  render_secrets.py control-plane --ssh root@<agent360-node>            # -> /opt/agent360-a2a/secrets/cp.env
  render_secrets.py worker --ssh root@<child-node> --instance gm          # -> /opt/agent360-a2a/secrets/qwenpaw-gm.auth.json
  render_secrets.py backend --ssh root@<agent360-node>                  # -> /opt/agent360/deploy/backend.env (env_file of agent360-backend)
  add --check to compare digests only (no write).

Paths (canonical-service-paths-v1):
  control plane : core/prod:/agent360-a2a/app/runtime  A2A_SERVICE_TOKEN, A2A_CALLBACK_SECRET, A2A_OPERATOR_TOKEN
  child runtime : <child project>/prod:/qwenpaw-<instance>/app/runtime  QWENPAW_AUTH_PASSWORD (username admin)
  backend       : core/prod:/agent360-backend/app/backend ConnectionStrings__DefaultConnection
                  + core/prod:/agent360-a2a/app/runtime A2A_OPERATOR_TOKEN -> Agent360__Security__OperatorToken
                  + QwenPaw__Instances__{gm,climax,planthy}__Username/Password from each instance runtime path
Secret values never touch stdout or local disk; files are written 0600 over ssh stdin.
"""
import argparse, hashlib, json, os, subprocess, sys, urllib.error, urllib.parse, urllib.request

PROJECTS = {"core": "ed243bea-df8c-4835-bad0-d6a291274729", "gm": "d260bdba-1d1e-4285-98b7-861b0ec3a161",
            "climax": "06c2f767-a35a-44d8-b411-ad0072eabd04", "planthy": "6d9e84c3-9468-4917-b139-076eb51f6270"}
# Agent360 backend fleet routing: instance key -> (Infisical project, runtime path)
FLEET = {"gm": ("gm", "/qwenpaw-gm/app/runtime"), "climax": ("climax", "/climax-n6ck/app/runtime"),
         "planthy": ("planthy", "/planthy-ai-eahr/app/runtime")}
ap = argparse.ArgumentParser()
ap.add_argument("role", choices=["control-plane", "worker", "backend"])
ap.add_argument("--ssh", required=True)
ap.add_argument("--instance", default="gm")
ap.add_argument("--child-project", default=None, help="Infisical project key of the child (default = instance)")
ap.add_argument("--username", default="admin")
ap.add_argument("--check", action="store_true")
A = ap.parse_args()

U = os.environ["INFISICAL_API_URL"].rstrip("/")
H = {"Content-Type": "application/json", "User-Agent": "curl/8"}
r = urllib.request.Request(U + "/api/v1/auth/universal-auth/login", json.dumps(
    {"clientId": os.environ["INFISICAL_CLIENT_ID"], "clientSecret": os.environ["INFISICAL_CLIENT_SECRET"]}).encode(), H)
H["Authorization"] = "Bearer " + json.load(urllib.request.urlopen(r, timeout=30))["accessToken"]


def get(ws, path, key):
    q = urllib.parse.urlencode({"workspaceId": ws, "environment": "prod", "secretPath": path})
    return json.load(urllib.request.urlopen(urllib.request.Request(f"{U}/api/v3/secrets/raw/{key}?{q}", headers=H),
                                            timeout=30))["secret"]["secretValue"]


def digest(v):
    return hashlib.sha256(v.encode()).hexdigest()[:12]


if A.role == "control-plane":
    keys = ["A2A_SERVICE_TOKEN", "A2A_CALLBACK_SECRET", "A2A_OPERATOR_TOKEN"]
    vals = {k: get(PROJECTS["core"], "/agent360-a2a/app/runtime", k) for k in keys}
    content = "".join(f"{k}={v}\n" for k, v in vals.items())
    target = "/opt/agent360-a2a/secrets/cp.env"
    want = {k: digest(v) for k, v in vals.items()}
    remote = "python3 -c \"import hashlib,sys;d=dict(l.strip().split('=',1) for l in open('%s') if '=' in l);print(' '.join(k+':'+hashlib.sha256(v.encode()).hexdigest()[:12] for k,v in sorted(d.items())))\"" % target
elif A.role == "backend":
    vals = {"ConnectionStrings__DefaultConnection": get(PROJECTS["core"], "/agent360-backend/app/backend", "ConnectionStrings__DefaultConnection"),
            # Same operator credential as the A2A control plane; guards mutating sub-agent/delegation routes.
            "Agent360__Security__OperatorToken": get(PROJECTS["core"], "/agent360-a2a/app/runtime", "A2A_OPERATOR_TOKEN")}
    for key, (proj, path) in FLEET.items():
        vals[f"QwenPaw__Instances__{key}__Username"] = "admin"
        vals[f"QwenPaw__Instances__{key}__Password"] = get(PROJECTS[proj], path, "QWENPAW_AUTH_PASSWORD")
    # Non-secret routing: agentteams-manager binds 127.0.0.1:18888 on node 4th; a tailnet-only socat
    # (systemd agentteams-manager-tailnet.service) exposes it at 100.77.205.126:18889 for Agent360.
    vals["QwenPaw__Instances__agentteams__InternalUrl"] = "http://100.77.205.126:18889"
    # Sub-agent registry/ledger lives in PostgreSQL (schema agent360_subagents) next to the app DB.
    vals["Agent360__SubAgents__Storage"] = "postgres"
    content = "".join(f"{k}={v}\n" for k, v in vals.items())
    target = "/opt/agent360/deploy/backend.env"
    want = {k: digest(v) for k, v in vals.items()}
    remote = "python3 -c \"import hashlib,sys;d=dict(l.rstrip('\\n').split('=',1) for l in open('%s') if '=' in l);print(' '.join(k+':'+hashlib.sha256(v.encode()).hexdigest()[:12] for k,v in sorted(d.items())))\"" % target
else:
    proj = PROJECTS[A.child_project or A.instance]
    pw = get(proj, f"/qwenpaw-{A.instance}/app/runtime", "QWENPAW_AUTH_PASSWORD")
    content = json.dumps({"username": A.username, "password": pw})
    target = f"/opt/agent360-a2a/secrets/qwenpaw-{A.instance}.auth.json"
    want = {"password": digest(pw)}
    remote = "python3 -c \"import hashlib,json;d=json.load(open('%s'));print('password:'+hashlib.sha256(d['password'].encode()).hexdigest()[:12])\"" % target

cur = subprocess.run(["ssh", A.ssh, remote], capture_output=True, text=True, timeout=60).stdout.strip()
expected = " ".join(f"{k}:{v}" for k, v in sorted(want.items()))
print("remote ", cur or "(missing)"); print("infisical", expected)
if A.check:
    sys.exit(0 if cur == expected else 1)
if cur != expected:
    subprocess.run(["ssh", A.ssh, f"umask 077; mkdir -p $(dirname {target}); cat > {target}.new && chmod 600 {target}.new && mv {target}.new {target}"],
                   input=content, text=True, check=True, timeout=60)
    cur = subprocess.run(["ssh", A.ssh, remote], capture_output=True, text=True, timeout=60).stdout.strip()
    print("written", cur == expected, "-> restart the consuming container")
else:
    print("in sync")
sys.exit(0 if cur == expected else 1)
