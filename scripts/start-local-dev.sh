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
echo "Backend API:   http://127.0.0.1:5290 (Health: /health)"
echo "Frontend SPA:  http://127.0.0.1:3000 (Proxy: /api -> 5290)"
echo "=========================================================="

# 1. Dọn dẹp tiến trình cũ trên cổng 5290 nếu có
echo "[1/4] Checking and freeing port 5290..."
if command -v cmd.exe >/dev/null 2>&1; then
  PIDS_5290=$(cmd.exe /c "netstat -ano | findstr 5290" 2>/dev/null | awk '{print $5}' | sort -u || true)
  for pid in ${PIDS_5290}; do
    if [ -n "${pid}" ] && [ "${pid}" != "0" ]; then
      echo "  Stopping process on 5290 (PID ${pid})..."
      cmd.exe /c "taskkill /F /PID ${pid}" >/dev/null 2>&1 || true
    fi
  done
fi

# 2. Khởi chạy Backend .NET 10 API
echo "[2/4] Starting Backend .NET 10 API..."
cd "${BACKEND_DIR}"
BIN_DIR="${BACKEND_DIR}/bin/Debug/net10.0"

if [ -f "${BIN_DIR}/Agent360.API.exe" ]; then
  # Chạy trực tiếp từ thư mục chứa binary để giữ tiến trình background ổn định
  (cd "${BIN_DIR}" && ./Agent360.API.exe --urls "http://0.0.0.0:5290" > "${LOG_DIR}/local-backend.log" 2>&1) &
else
  (cd "${BACKEND_DIR}" && dotnet run --no-restore --launch-profile http --urls "http://0.0.0.0:5290" > "${LOG_DIR}/local-backend.log" 2>&1) &
fi

echo "  Backend process started. Monitoring health..."
for i in $(seq 1 20); do
  if /mnt/c/nvm4w/nodejs/node.exe -e "fetch('http://127.0.0.1:5290/health').then(r=>{if(!r.ok)process.exit(1);process.exit(0)}).catch(()=>process.exit(1))" 2>/dev/null; then
    echo "  ✅ Backend API is ONLINE and HEALTHY (http://127.0.0.1:5290/health)"
    break
  fi
  sleep 1
done

# 3. Đảm bảo Frontend Vite SPA trên cổng 3000 đang hoạt động
echo "[3/4] Checking Frontend Vite server on port 3000..."
if ! curl -sf http://127.0.0.1:3000/ >/dev/null 2>&1; then
  echo "  Starting Frontend Vite on port 3000..."
  cd "${FRONTEND_DIR}"
  if [ -f "/home/dangm/.tmp/agent360-budget-qa/node_modules/.bin/vite" ]; then
    nohup /home/dangm/.tmp/agent360-budget-qa/node_modules/.bin/vite preview --host 0.0.0.0 --port 3000 --strictPort > "${LOG_DIR}/local-frontend.log" 2>&1 &
  else
    nohup ./node_modules/.bin/vite --host 0.0.0.0 --port 3000 > "${LOG_DIR}/local-frontend.log" 2>&1 &
  fi
  sleep 2
fi

# 4. Xác nhận toàn bộ dịch vụ cục bộ
echo "[4/4] Final Verification..."
if curl -sf http://127.0.0.1:3000/ >/dev/null 2>&1; then
  echo "  ✅ Frontend SPA is ONLINE (http://127.0.0.1:3000/)"
fi

echo "=========================================================="
echo "  🎉 Skyer Local Dev Environment is Ready!"
echo "  👉 Landing Page: http://127.0.0.1:3000/"
echo "  👉 App Workspace: http://127.0.0.1:3000/app"
echo "  👉 Telemetry:    http://127.0.0.1:3000/analytics"
echo "  👉 Backend API:  http://127.0.0.1:5290/health"
echo "=========================================================="
