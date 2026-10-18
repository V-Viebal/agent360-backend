#!/usr/bin/env bash
# ==============================================================================
# Script khởi chạy 1-click cho Agent360 / Skyer (Local Fullstack Environment)
# Khởi chạy song song Backend .NET 10 API (Port 5290) & Frontend Vite SPA (Port 3000)
# ==============================================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
BACKEND_DIR="${PROJECT_ROOT}/backend/src/Agent360.API"
FRONTEND_DIR="${PROJECT_ROOT}/frontend"
LOG_DIR="${PROJECT_ROOT}/.tmp"

mkdir -p "${LOG_DIR}"

echo "=========================================================="
echo "   🚀 Starting Skyer / Agent360 Local Development Stack    "
echo "=========================================================="
echo "Project Root:  ${PROJECT_ROOT}"
echo "Backend API:   http://127.0.0.1:5290 (Scalar Docs: /scalar/v1)"
echo "Frontend SPA:  http://127.0.0.1:3000 (HMR Dev Server: /app)"
echo "=========================================================="

# 1. Dọn dẹp tiến trình cũ trên cổng 5290 và 3000
echo "[1/4] Stopping existing dev processes on 5290 & 3000..."
pkill -f "Agent360.API" 2>/dev/null || true
pkill -f "dotnet.*Agent360" 2>/dev/null || true
pkill -f "dotnet watch" 2>/dev/null || true
pkill -f "vite.*3000" 2>/dev/null || true

if command -v cmd.exe >/dev/null 2>&1; then
  cmd.exe /c "taskkill /F /IM Agent360.API.exe" >/dev/null 2>&1 || true
  for port in 5290 3000; do
    PIDS=$(cmd.exe /c "netstat -ano | findstr :${port}" 2>/dev/null | awk '{print $5}' | sort -u || true)
    for pid in ${PIDS}; do
      if [ -n "${pid}" ] && [ "${pid}" != "0" ]; then
        cmd.exe /c "taskkill /F /PID ${pid}" >/dev/null 2>&1 || true
      fi
    done
  done
fi

# 2. Khởi chạy Backend .NET 10 API
echo "[2/4] Starting Backend .NET 10 API (Port 5290)..."
cd "${BACKEND_DIR}"
export ASPNETCORE_ENVIRONMENT=Development
setsid dotnet run --no-restore --launch-profile http --urls "http://0.0.0.0:5290" > "${LOG_DIR}/local-backend.log" 2>&1 &
echo "  Backend process started in background."

# 3. Khởi chạy Frontend Vite Dev Server (HMR Hot-Reload trên Port 3000)
echo "[3/4] Starting Frontend Vite dev server (HMR Port 3000)..."
cd "${FRONTEND_DIR}"
if [ -d "/tmp/a360fe-br2/node_modules" ]; then
  cd /tmp/a360fe-br2
  setsid ./node_modules/.bin/vite --host 0.0.0.0 --port 3000 > "${LOG_DIR}/local-frontend.log" 2>&1 &
elif [ -f "${FRONTEND_DIR}/node_modules/.bin/vite" ]; then
  setsid "${FRONTEND_DIR}/node_modules/.bin/vite" --host 0.0.0.0 --port 3000 > "${LOG_DIR}/local-frontend.log" 2>&1 &
else
  setsid npx vite --host 0.0.0.0 --port 3000 > "${LOG_DIR}/local-frontend.log" 2>&1 &
fi
echo "  Frontend HMR server started in background."

# 4. Xác nhận trạng thái
echo "[4/4] Verifying local services..."
for i in $(seq 1 15); do
  if powershell.exe -Command "Invoke-WebRequest -Uri http://localhost:5290/scalar/v1 -UseBasicParsing" >/dev/null 2>&1; then
    echo "  ✅ Backend API is ONLINE and HEALTHY (http://localhost:5290/scalar/v1)"
    break
  fi
  sleep 1
done

if powershell.exe -Command "Invoke-WebRequest -Uri http://localhost:3000 -UseBasicParsing" >/dev/null 2>&1; then
  echo "  ✅ Frontend SPA is ONLINE (http://localhost:3000/app)"
fi

echo "=========================================================="
echo "  🎉 Skyer Local Dev Stack is Ready!"
echo "  👉 Frontend SPA (HMR): http://localhost:3000"
echo "  👉 App Workspace:      http://localhost:3000/app"
echo "  👉 Backend API Docs:   http://localhost:5290/scalar/v1"
echo "=========================================================="
