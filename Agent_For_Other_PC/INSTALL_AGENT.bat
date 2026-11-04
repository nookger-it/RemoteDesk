@echo off
title RemoteDesk V1 - Remote Agent Installer (Target PC)
color 0A
cd /d "%~dp0"

echo ============================================================================
echo   RemoteDesk V1 - Remote Agent Installer (For Target PC to be Controlled)
echo   Target Directory: C:\RemoteDesk\Agent
echo ============================================================================
echo.

set "DEFAULT_IP=192.168.1.110"
set /p SERVER_IP="Enter Control Server IP Address (Press Enter for default %DEFAULT_IP%): "
if "%SERVER_IP%"=="" set "SERVER_IP=%DEFAULT_IP%"

set "INSTALL_DIR=C:\RemoteDesk\Agent"

echo.
echo [1/4] Stopping any running RemoteDesk Agent processes...
taskkill /F /IM RemoteDesk.Agent.Service.exe >nul 2>&1
taskkill /F /IM RemoteDesk.Agent.Interactive.exe >nul 2>&1

echo [2/4] Copying Self-Contained Agent files to %INSTALL_DIR% ...
if not exist "%INSTALL_DIR%\agent-service" mkdir "%INSTALL_DIR%\agent-service"
if not exist "%INSTALL_DIR%\agent-interactive" mkdir "%INSTALL_DIR%\agent-interactive"
xcopy /E /I /Y "%~dp0agent-service\*" "%INSTALL_DIR%\agent-service\" >nul
xcopy /E /I /Y "%~dp0agent-interactive\*" "%INSTALL_DIR%\agent-interactive\" >nul
echo %SERVER_IP%> "%INSTALL_DIR%\server_ip.txt"
copy /Y "%~dp0START_AGENT.bat" "%INSTALL_DIR%\START_AGENT.bat" >nul
copy /Y "%~dp0UNINSTALL_AGENT.bat" "%INSTALL_DIR%\UNINSTALL_AGENT.bat" >nul

echo [3/4] Opening Windows Firewall port 52000 (Direct LAN Streaming)...
netsh advfirewall firewall delete rule name="RemoteDesk Agent Direct LAN 52000" >nul 2>&1
netsh advfirewall firewall add rule name="RemoteDesk Agent Direct LAN 52000" dir=in action=allow protocol=TCP localport=52000 >nul 2>&1

echo [4/4] Registering Auto-Start on Windows Login and Desktop Shortcut...
powershell -NoProfile -Command "$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut([Environment]::GetFolderPath('Desktop') + '\RemoteDesk Agent.lnk'); $s.TargetPath = '%INSTALL_DIR%\START_AGENT.bat'; $s.WorkingDirectory = '%INSTALL_DIR%'; $s.Save(); $st = $ws.CreateShortcut([Environment]::GetFolderPath('Startup') + '\RemoteDesk Agent.lnk'); $st.TargetPath = '%INSTALL_DIR%\START_AGENT.bat'; $st.WorkingDirectory = '%INSTALL_DIR%'; $st.Save();"

echo.
echo ============================================================================
echo   SUCCESS! RemoteDesk Agent installed to %INSTALL_DIR%
echo   Connected to Control Server: http://%SERVER_IP%:5100
echo   Starting Agent now...
echo ============================================================================
call "%INSTALL_DIR%\START_AGENT.bat"
pause
