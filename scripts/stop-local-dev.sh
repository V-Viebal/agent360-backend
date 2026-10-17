#!/usr/bin/env bash
# ==============================================================================
# Script 1-click dừng toàn bộ môi trường local Skyer / Agent360
# ==============================================================================

set -euo pipefail

echo "Stopping Skyer local services..."

# Dừng frontend Vite
pkill -f "vite preview" 2>/dev/null || true
pkill -f "vite --host" 2>/dev/null || true

# Dừng backend dotnet
if command -v cmd.exe >/dev/null 2>&1; then
  cmd.exe /c "taskkill /F /IM Agent360.API.exe" 2>/dev/null || true
fi

echo "✅ All local services on port 5290 and 3000 stopped."
