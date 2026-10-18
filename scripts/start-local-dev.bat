@echo off
REM ==============================================================================
REM Script khởi chạy 1-click trên Windows cho Skyer / Agent360 với HOT-RELOAD
REM ==============================================================================

set PROJECT_ROOT=%~dp0..
set BACKEND_DIR=%PROJECT_ROOT%\backend\src\Agent360.API
set FRONTEND_DIR=%PROJECT_ROOT%\frontend

echo ==========================================================
echo    Starting Skyer / Agent360 Local Dev (Hot-Reload Windows)
echo ==========================================================

echo [1/3] Terminating existing processes on port 5290 and 3000...
for /f "tokens=5" %%a in ('netstat -ano ^| findstr 5290') do (
    taskkill /F /PID %%a >nul 2>&1
)
for /f "tokens=5" %%a in ('netstat -ano ^| findstr 3000') do (
    taskkill /F /PID %%a >nul 2>&1
)

echo [2/3] Starting Backend API with dotnet watch on http://127.0.0.1:5290...
start "Skyer Backend (Hot-Reload)" /d "%BACKEND_DIR%" dotnet watch run --no-launch-profile --urls "http://0.0.0.0:5290"

echo [3/3] Starting Frontend SPA with Vite HMR on http://127.0.0.1:3000...
start "Skyer Frontend (HMR)" /d "%FRONTEND_DIR%" npx vite --host 0.0.0.0 --port 3000

echo ==========================================================
echo    Local Stack started with Hot-Reload in separate windows!
echo    Frontend SPA: http://127.0.0.1:3000
echo    Backend Docs: http://127.0.0.1:5290/scalar/v1
echo ==========================================================
