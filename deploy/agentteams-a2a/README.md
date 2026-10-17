# Agent360 external A2A (AgentTeams transport)

The control plane, worker adapter and MCP server let a parent QwenPaw agent delegate tasks to agents on other
QwenPaw instances through AgentTeams (Matrix). The Agent360 UI lives in the frontend repo
(`src/components/ExternalSubAgentsTab.tsx`, `src/services/a2aApi.ts`).

| path | runs on | purpose |
|---|---|---|
| `adapter/control_plane.py` | Agent360 node (`agent360-a2a`, :8095) | `/api/v1/a2a/*`, ledger, policy, health/metrics |
| `adapter/worker_adapter.py` | child QwenPaw node | executes delegations on the local QwenPaw |
| `adapter/mcp_server.py` | parent QwenPaw | MCP `agent360-a2a` (list/delegate/check/review) |
| `templates/` | — | compose files, the nginx location block, the MCP client |
| `scripts/render_secrets.py` | operator workstation | renders `cp.env`, worker auth and `backend.env` from Infisical |

## Tests

```bash
cd adapter && python3 -m unittest tests.test_a2a
```

## Live acceptance

* `A2A_TOKEN=<operator> scripts/live_smoke.py --api https://agent360.antares.vn/api --parent mons --child agent://gm/default`
* `scripts/chat_e2e.py --api https://agent360.antares.vn/api --parent mons`

The full runbook lives in KB (agents-core hub, under "Runbook: Agent360 external A2A sub-agents over AgentTeams") and in
`.agent/skills/infrastructure/init-agentteams/external-a2a/README.md`.
