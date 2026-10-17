@echo off
echo Stopping Skyer local services...
taskkill /F /IM Agent360.API.exe >nul 2>&1
for /f "tokens=5" %%a in ('netstat -ano ^| findstr 3000') do (
    taskkill /F /PID %%a >nul 2>&1
)
for /f "tokens=5" %%a in ('netstat -ano ^| findstr 5290') do (
    taskkill /F /PID %%a >nul 2>&1
)
echo All Skyer local services stopped.
pause
