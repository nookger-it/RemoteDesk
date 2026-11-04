@echo off
title RemoteDesk V1 - Start Server
cd /d "%~dp0"

echo Starting RemoteDesk Control API (http://0.0.0.0:5100) and Local Relay (port 52100)...
start "RemoteDesk Control API (Port 5100)" /D "%~dp0api" "%~dp0api\RemoteDesk.Api.exe" --urls "http://0.0.0.0:5100"
start "RemoteDesk Local Relay (Port 52100)" /D "%~dp0relay" "%~dp0relay\RemoteDesk.Relay.exe"
echo Server and Relay are now running!

