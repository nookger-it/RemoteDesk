@echo off
title RemoteDesk V1 - Uninstall Remote Agent
taskkill /F /IM RemoteDesk.Agent.Service.exe >nul 2>&1
taskkill /F /IM RemoteDesk.Agent.Interactive.exe >nul 2>&1
del /F /Q "%USERPROFILE%\Desktop\RemoteDesk Agent.lnk" >nul 2>&1
del /F /Q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\RemoteDesk Agent.lnk" >nul 2>&1
echo RemoteDesk Agent stopped and shortcuts removed.
pause

