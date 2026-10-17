#!/usr/bin/env python3
"""Rotate the admin password of one auth-enabled QwenPaw instance and propagate it to every consumer.

Order (fail-closed, idempotent per step; secrets never printed):
  1. read current password from Infisical (source of truth) and verify it logs in;
  2. generate a new password, store it in Infisical as QWENPAW_AUTH_PASSWORD_NEXT (crash-safe);
  3. POST /api/auth/update-profile on the live runtime (rotates hash + JWT secret, no restart);
  4. verify login with the new password, then promote it to QWENPAW_AUTH_PASSWORD in Infisical;
  5. update Coolify (compose literal or service env) so a redeploy keeps the new password;
  6. re-render consumers: Agent360 backend.env (+restart), A2A worker auth file (gm), workspace .env key;
  7. verify each consumer.
Usage: rotate_qwenpaw_password.py gm|climax|planthy [--dry-run]
Env: INFISICAL_API_URL/CLIENT_ID/CLIENT_SECRET, COOLIFY_URL/COOLIFY_API_TOKEN.
"""
import argparse, base64, hashlib, json, os, re, secrets, string, subprocess, sys, urllib.error, urllib.parse, urllib.request

INST = {
    "gm": {"url": "https://ai-gm.antares.vn", "project": "d260bdba-1d1e-4285-98b7-861b0ec3a161", "path": "/qwenpaw-gm/app/runtime",
           "coolify": "uezyh740bmln9caq3u62ogep", "mode": "compose", "worker": ("root@100.81.66.42", "/opt/agent360-a2a/secrets/qwenpaw-gm.auth.json", "agent360-a2a-worker-gm-default"),
           "dotenv": None},
    "climax": {"url": "https://ai-climax.antares.vn", "project": "06c2f767-a35a-44d8-b411-ad0072eabd04", "path": "/climax-n6ck/app/runtime",
               "coolify": "ynh0hvyy9gu18akbkrf9cv0a", "mode": "compose", "worker": None, "dotenv": "CLIMAX_COPAW_ADMIN_PASSWORD",
               "aliases": ["/climax-n6ck/agents/qwenpaw-climax/app/runtime"]},
    "planthy": {"url": "https://planthy-ai.hmz.one", "project": "6d9e84c3-9468-4917-b139-076eb51f6270", "path": "/planthy-ai-eahr/app/runtime",
                "coolify": "pomaumthexnxnxfmx5b0pzxh", "mode": "env", "worker": None, "dotenv": "PLANTHY_COPAW_AUTH_PASSWORD",
                "aliases": ["/planthy-ai-eahr/agents/planthy-ai-eahr/app/runtime"]},
}
AGENT360 = "root@100.109.210.72"
UA = {"User-Agent": "agent360-rotate/1.0", "Content-Type": "application/json"}
ap = argparse.ArgumentParser(); ap.add_argument("instance", choices=sorted(INST)); ap.add_argument("--dry-run", action="store_true")
A = ap.parse_args(); C = INST[A.instance]
d = lambda v: hashlib.sha256(v.encode()).hexdigest()[:12]
log = lambda *a: print(f"[{A.instance}]", *a, flush=True)

def http(method, url, body=None, headers=None, timeout=60):
    r = urllib.request.Request(url, data=json.dumps(body).encode() if body is not None else None, headers={**UA, **(headers or {})}, method=method)
    try:
        with urllib.request.urlopen(r, timeout=timeout) as x: return x.status, json.loads(x.read() or b"{}")
    except urllib.error.HTTPError as e:
        try: return e.code, json.loads(e.read() or b"{}")
        except Exception: return e.code, {}

# --- Infisical
IU = os.environ["INFISICAL_API_URL"].rstrip("/")
_, tok = http("POST", IU + "/api/v1/auth/universal-auth/login", {"clientId": os.environ["INFISICAL_CLIENT_ID"], "clientSecret": os.environ["INFISICAL_CLIENT_SECRET"]})
IH = {"Authorization": "Bearer " + tok["accessToken"]}
def iget(key):
    q = urllib.parse.urlencode({"workspaceId": C["project"], "environment": "prod", "secretPath": C["path"]})
    st, b = http("GET", f"{IU}/api/v3/secrets/raw/{key}?{q}", headers=IH)
    return b["secret"]["secretValue"] if st == 200 else None
def iput(key, val):
    body = {"workspaceId": C["project"], "environment": "prod", "secretPath": C["path"], "secretValue": val, "type": "shared"}
    st, _ = http("POST", f"{IU}/api/v3/secrets/raw/{key}", body, IH)
    if st >= 400: st, _ = http("PATCH", f"{IU}/api/v3/secrets/raw/{key}", body, IH)
    assert st < 300 and iget(key) == val, f"infisical write {key} failed ({st})"

def login(pw):
    st, b = http("POST", C["url"] + "/api/auth/login", {"username": "admin", "password": pw})
    return b.get("token") if st == 200 else None

cur = iget("QWENPAW_AUTH_PASSWORD"); nxt = iget("QWENPAW_AUTH_PASSWORD_NEXT")
if nxt and login(nxt) and not login(cur or ""):   # resume an interrupted rotation
    log("resuming: runtime already on NEXT password"); new = nxt
else:
    token = login(cur or "")
    assert token, "current Infisical password does not log in; refusing to rotate"
    log("current password verified", d(cur))
    if A.dry_run: log("dry-run ok"); sys.exit(0)
    new = "".join(secrets.choice(string.ascii_letters + string.digits) for _ in range(32))
    iput("QWENPAW_AUTH_PASSWORD_NEXT", new)
    st, b = http("POST", C["url"] + "/api/auth/update-profile", {"current_password": cur, "new_password": new}, {"Authorization": "Bearer " + token})
    assert st == 200, f"update-profile failed: HTTP {st}"
assert login(new), "new password does not log in"
assert not login(cur or "x"), "old password still logs in"
iput("QWENPAW_AUTH_PASSWORD", new); log("runtime + Infisical rotated", d(new))
for alias in C.get("aliases", []):  # per-agent copies of the same credential
    for k in ("AUTH_PASSWORD", "QWENPAW_AUTH_PASSWORD"):
        body = {"workspaceId": C["project"], "environment": "prod", "secretPath": alias, "secretValue": new, "type": "shared"}
        st, _ = http("PATCH", f"{IU}/api/v3/secrets/raw/{k}", body, IH)
        if st >= 400: http("POST", f"{IU}/api/v3/secrets/raw/{k}", body, IH)
    log("infisical alias synced", alias)

# --- Coolify (so a redeploy keeps the new password)
CU = os.environ["COOLIFY_URL"].rstrip("/"); CH = {"Authorization": "Bearer " + os.environ["COOLIFY_API_TOKEN"], "Accept": "application/json"}
if C["mode"] == "compose":
    st, svc = http("GET", f"{CU}/api/v1/services/{C['coolify']}", headers=CH)
    raw = svc["docker_compose_raw"]
    raw2, n = re.subn(r"(QWENPAW_AUTH_PASSWORD:\s*)['\"]?[^\s'\"\n]+['\"]?", lambda m: m.group(1) + "'" + new + "'", raw)
    assert n >= 1, "password key not found in compose"
    st, b = http("PATCH", f"{CU}/api/v1/services/{C['coolify']}", {"docker_compose_raw": base64.b64encode(raw2.encode()).decode()}, CH)
    assert st < 300, f"coolify compose patch failed: {st} {str(b)[:200]}"
    st, svc = http("GET", f"{CU}/api/v1/services/{C['coolify']}", headers=CH)
    assert ("'" + new + "'") in svc["docker_compose_raw"] or new in svc["docker_compose_raw"], "coolify readback mismatch"
else:
    st, envs = http("GET", f"{CU}/api/v1/services/{C['coolify']}/envs", headers=CH)
    st, b = http("PATCH", f"{CU}/api/v1/services/{C['coolify']}/envs", {"key": "QWENPAW_AUTH_PASSWORD", "value": new}, CH)
    assert st < 300, f"coolify env patch failed: {st} {str(b)[:200]}"
log("coolify definition updated (applies on next redeploy; live runtime already rotated)")

# --- consumers
def ssh(host, cmd, inp=None):
    return subprocess.run(["ssh", "-o", "BatchMode=yes", host, cmd], input=inp, text=True, capture_output=True, timeout=180)
env = ssh(AGENT360, "cat /opt/agent360/deploy/backend.env").stdout
key = f"QwenPaw__Instances__{A.instance}__Password"
env2 = re.sub(rf"^{key}=.*$", f"{key}={new}", env, flags=re.M)
assert env2 != env or f"{key}={new}" in env, "backend.env key missing"
r = ssh(AGENT360, "umask 077; cat > /opt/agent360/deploy/backend.env.new && mv /opt/agent360/deploy/backend.env.new /opt/agent360/deploy/backend.env && cd /opt/agent360/deploy && docker compose -f docker-compose.prod.yml up -d --force-recreate agent360-backend >/dev/null 2>&1 && echo ok", env2)
assert "ok" in r.stdout, "backend.env update failed: " + r.stderr[-200:]
log("agent360 backend.env updated + backend recreated")
if C["worker"]:
    host, path, ctr = C["worker"]
    r = ssh(host, f"umask 077; cat > {path}.new && mv {path}.new {path} && docker restart {ctr} >/dev/null && echo ok", json.dumps({"username": "admin", "password": new}))
    assert "ok" in r.stdout, "worker auth update failed"
    log("A2A worker auth file updated + worker restarted")
if C["dotenv"]:
    p = "/mnt/d/vps/.env"; s = open(p, encoding="utf-8").read()
    s2 = re.sub(rf"^{C['dotenv']}=.*$", f"{C['dotenv']}={new}", s, flags=re.M)
    if s2 != s: open(p, "w", encoding="utf-8").write(s2); log("workspace .env", C["dotenv"], "updated")
iput("QWENPAW_AUTH_PASSWORD_NEXT", "")
log("done")
