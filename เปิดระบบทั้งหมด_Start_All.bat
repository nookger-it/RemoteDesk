@echo off
chcp 65001 >nul
title RemoteDesk V1 — All-in-One Launcher (LAN-First)
echo ============================================================================
echo   RemoteDesk V1 (LAN-First Remote Desktop) — Starting Full Stack...
echo ============================================================================
echo.

cd /d "d:\remote desktop"

echo [1/5] Starting Control Server API (Port 5100 on all LAN interfaces 0.0.0.0:5100)...
start "RemoteDesk Control API (5100)" cmd /k "cd /d ""d:\remote desktop\src\backend\RemoteDesk.Api"" && dotnet run --urls ""http://0.0.0.0:5100"""

echo [2/5] Starting Local Relay Service (Port 52100)...
start "RemoteDesk Local Relay (52100)" cmd /k "cd /d ""d:\remote desktop\src\backend\RemoteDesk.Relay"" && dotnet run"

timeout /t 3 /nobreak >nul

echo [3/5] Starting Windows Agent Service + Interactive Desktop Host (Port 52000)...
start "RemoteDesk Agent Interactive (52000)" "d:\remote desktop\artifacts\agent-interactive\RemoteDesk.Agent.Interactive.exe"
start "RemoteDesk Agent Service" "d:\remote desktop\artifacts\agent-service\RemoteDesk.Agent.Service.exe"

echo [4/5] Starting Web Admin Dashboard (http://localhost:3000)...
start "RemoteDesk Web Dashboard (3000)" cmd /k "cd /d ""d:\remote desktop\src\dashboard\remotedesk-dashboard"" && npm run dev"

timeout /t 3 /nobreak >nul
start "" "http://localhost:3000"

echo [5/5] Launching Windows Operator Client...
start "" "d:\remote desktop\artifacts\operator-client\RemoteDesk.Operator.Windows.exe"

echo.
echo All services started successfully!
timeout /t 3
