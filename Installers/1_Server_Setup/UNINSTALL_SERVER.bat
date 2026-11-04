@echo off
title RemoteDesk V1 - Uninstall Server
echo Stopping RemoteDesk Server and Relay...
taskkill /F /IM RemoteDesk.Api.exe >nul 2>&1
taskkill /F /IM RemoteDesk.Relay.exe >nul 2>&1
del /F /Q "%USERPROFILE%\Desktop\RemoteDesk Server.lnk" >nul 2>&1
del /F /Q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\RemoteDesk Server.lnk" >nul 2>&1
echo Removed shortcuts and stopped Server processes.
pause

