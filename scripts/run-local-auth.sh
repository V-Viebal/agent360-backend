#!/usr/bin/env bash
# Run Agent360 locally with the real Skyer sign-in (Logto) and admin role, like production.
#
#   backend  http://127.0.0.1:5290  (Development, validates Skyer tokens: issuer auth-skyer, audience api-agent360)
#   frontend http://localhost:5173  (Vite; /api -> local backend; signs in with the skyer-local-app SPA client)
#
# Admin access comes from the Logto "admin" role (scope admin:all) or Agent360:Security:AdminSubjects.
# Secrets are never stored here: pass them through a private env file (chmod 600) that defines at least
#   ConnectionStrings__DefaultConnection   (e.g. the staging DB through an SSH tunnel)
#   Agent360__Security__OperatorToken, QwenPaw__Instances__<key>__Username/Password/InternalUrl ...
# Usage: scripts/run-local-auth.sh /path/to/private.env [frontend-dir]
set -euo pipefail
ENV_FILE="${1:?private env file required}"
FRONTEND_DIR="${2:-$(cd "$(dirname "$0")/../../frontend" 2>/dev/null && pwd || true)}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LOG="${LOG_DIR:-$ROOT/.tmp}"; mkdir -p "$LOG"

set -a
. "$ENV_FILE"
ASPNETCORE_ENVIRONMENT=Development
ASPNETCORE_URLS=http://127.0.0.1:5290
Logto__Authority="${Logto__Authority:-https://auth-skyer.hmz.one/oidc}"
Logto__Audience="${Logto__Audience:-https://api-agent360.antares.vn}"
Agent360__Security__RequireAuthForSensitiveReads=true
Agent360__Security__ProtectRuntimeProxyWrites=true
Cors__AllowedOrigins="http://localhost:5173;http://localhost:3000"
set +a

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET=dotnet
export DOTNET_CLI_TELEMETRY_OPTOUT=1
ldconfig -p 2>/dev/null | grep -q libicu || export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
"$DOTNET" build "$ROOT/src/Agent360.API/Agent360.API.csproj" -c Debug -v q -nologo
(cd "$ROOT/src/Agent360.API" && nohup "$DOTNET" bin/Debug/net10.0/Agent360.API.dll > "$LOG/local-backend.log" 2>&1 &)
for i in $(seq 1 30); do curl -sf -o /dev/null http://127.0.0.1:5290/api/v1/agents/mons/browser/health && break; sleep 1; done
echo "backend: $(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:5290/api/v1/agents/mons/browser/health) (health), $(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:5290/api/v1/agents) (agents without token, expect 401)"

if [ -n "$FRONTEND_DIR" ] && [ -d "$FRONTEND_DIR" ]; then
  (cd "$FRONTEND_DIR" && AGENT360_API_TARGET=http://127.0.0.1:5290 nohup ./node_modules/.bin/vite --host 127.0.0.1 --port 5173 --strictPort > "$LOG/local-frontend.log" 2>&1 &)
  sleep 5
  echo "frontend: http://localhost:5173/app (sign in with a Skyer admin account)"
fi
