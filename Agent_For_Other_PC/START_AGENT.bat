@echo off
cd /d "%~dp0"
title RemoteDesk V1 - Agent Launcher (Remote PC)
echo ============================================================================
echo   RemoteDesk V1 - Starting LAN Agent (Self-Contained Win64)
echo ============================================================================
echo.

REM ============================================================================
REM Server IP Address (Control API + Local Relay)
REM Change 192.168.1.110 below if your Server PC has a different LAN IP
REM ============================================================================
set REMOTEDESK_SERVER_IP=192.168.1.110
set REMOTEDESK_API_URL=http://%REMOTEDESK_SERVER_IP%:5100
set REMOTEDESK_RELAY_HOST=%REMOTEDESK_SERVER_IP%
set REMOTEDESK_RELAY_PORT=52100
set REMOTEDESK_DIRECT_PORT=52000
REM Quality profile: ultra (native 95%, 45fps) / sharp / balanced / fast (60fps)
REM The Operator can also switch quality live from the Operator Client toolbar.
set REMOTEDESK_QUALITY_PROFILE=ultra

echo [Configuration]
echo   - Control Server API : %REMOTEDESK_API_URL%
echo   - Direct LAN Port    : %REMOTEDESK_DIRECT_PORT%
echo   - Local Relay        : %REMOTEDESK_RELAY_HOST%:%REMOTEDESK_RELAY_PORT%
echo.

REM Allow inbound TCP 52000 if run as Administrator
netsh advfirewall firewall add rule name="RemoteDesk Agent Direct LAN (52000)" dir=in action=allow protocol=TCP localport=52000 >nul 2>&1

echo [1/2] Starting Interactive Screen Capture Host (Port 52000)...
start "RemoteDesk Agent Interactive (52000)" cmd /k "cd /d ""%~dp0agent-interactive"" && RemoteDesk.Agent.Interactive.exe"

timeout /t 2 /nobreak >nul

echo [2/2] Starting Device Enrollment & Heartbeat Service...
start "RemoteDesk Agent Service" cmd /k "cd /d ""%~dp0agent-service"" && RemoteDesk.Agent.Service.exe"

echo.
echo [OK] Both Agent windows have been launched!
echo Keep the two black Agent windows open while remote control is active.
echo.
pause

