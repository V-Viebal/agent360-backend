p="/opt/agentteams/scripts/init/start-tuwunel.sh"
s=open(p).read()
lines=[l for l in s.splitlines() if "agent360-a2a" not in l and "a2a-reset-admin" not in l]
s="\n".join(lines)+"\n"
block='''# agent360-a2a: one-shot Matrix admin password reconcile (secret read from env, never persisted)
if [ -n "${AGENTTEAMS_ADMIN_PASSWORD:-}" ] && [ -f /data/.a2a-reset-admin ]; then
  export CONDUWUIT_ADMIN_EXECUTE="[\\"users reset-password admin ${AGENTTEAMS_ADMIN_PASSWORD}\\"]"
  rm -f /data/.a2a-reset-admin
fi
'''
s=s.replace("exec tuwunel\n", block+"exec tuwunel\n")
open(p,"w").write(s)
print(s[-420:].replace(__import__("os").environ.get("AGENTTEAMS_ADMIN_PASSWORD","~"),"<redacted>"))
