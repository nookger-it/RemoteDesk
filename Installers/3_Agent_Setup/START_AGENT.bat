@echo off
title RemoteDesk V1 - Start Remote Agent
cd /d "%~dp0"

set "SERVER_IP=192.168.1.110"
if exist "%~dp0server_ip.txt" (
    set /p SERVER_IP=<"%~dp0server_ip.txt"
)

set "REMOTEDESK_API_URL=http://%SERVER_IP%:5100"
set "REMOTEDESK_RELAY_HOST=%SERVER_IP%"
set "REMOTEDESK_RELAY_PORT=52100"
set "REMOTEDESK_DIRECT_PORT=52000"

netsh advfirewall firewall add rule name="RemoteDesk Agent Direct LAN 52000" dir=in action=allow protocol=TCP localport=52000 >nul 2>&1

taskkill /F /IM RemoteDesk.Agent.Service.exe >nul 2>&1
taskkill /F /IM RemoteDesk.Agent.Interactive.exe >nul 2>&1

echo Starting RemoteDesk Agent (%COMPUTERNAME%) -> Server %REMOTEDESK_API_URL% ...
start "RemoteDesk.Agent.Interactive (Port 52000)" /MIN /D "%~dp0agent-interactive" "%~dp0agent-interactive\RemoteDesk.Agent.Interactive.exe"
start "RemoteDesk.Agent.Service (Heartbeat)" /MIN /D "%~dp0agent-service" "%~dp0agent-service\RemoteDesk.Agent.Service.exe"
echo RemoteDesk Agent is now online and ready for remote control!

