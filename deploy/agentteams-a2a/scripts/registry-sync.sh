#!/bin/bash
# Cron on the AgentTeams node (every 5 min): export the secret-free registry and push it to the
# Agent360 control plane. Health turns `degraded` when this stops (agentteams_registry_stale) or when
# any worker/edge loses its Matrix identity (AppService drift -> agentteams_identity_missing).
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
CP_SSH=${A2A_CP_SSH:-root@100.109.210.72}
OUT=/opt/agent360-a2a/registry.json
"$HERE/export_registry.sh" "$OUT" >/dev/null
scp -q -o BatchMode=yes "$OUT" "$CP_SSH:/opt/agent360-a2a/state/registry.json.new"
ssh -o BatchMode=yes "$CP_SSH" 'mv /opt/agent360-a2a/state/registry.json.new /opt/agent360-a2a/state/registry.json'
# Manager Matrix token guard: alert + self-heal a revoked token (never fails the registry sync).
"$HERE/manager_matrix_guard.sh" || true
