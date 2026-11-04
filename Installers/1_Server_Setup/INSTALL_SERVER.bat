@echo off
title RemoteDesk V1 - Server Installer (Control API + Local Relay)
color 0B
cd /d "%~dp0"

echo ============================================================================
echo   RemoteDesk V1 - Control Server ^& Local Relay Installer
echo   Target Directory: C:\RemoteDesk\Server
echo ============================================================================
echo.

set "INSTALL_DIR=C:\RemoteDesk\Server"

echo [1/4] Stopping any running Server / Relay processes...
taskkill /F /IM RemoteDesk.Api.exe >nul 2>&1
taskkill /F /IM RemoteDesk.Relay.exe >nul 2>&1

echo [2/4] Copying Server and Relay files to %INSTALL_DIR% ...
if not exist "%INSTALL_DIR%\api" mkdir "%INSTALL_DIR%\api"
if not exist "%INSTALL_DIR%\relay" mkdir "%INSTALL_DIR%\relay"
xcopy /E /I /Y "%~dp0api\*" "%INSTALL_DIR%\api\" >nul
xcopy /E /I /Y "%~dp0relay\*" "%INSTALL_DIR%\relay\" >nul
copy /Y "%~dp0START_SERVER.bat" "%INSTALL_DIR%\START_SERVER.bat" >nul
copy /Y "%~dp0UNINSTALL_SERVER.bat" "%INSTALL_DIR%\UNINSTALL_SERVER.bat" >nul

echo [3/4] Opening Windows Firewall ports 5100 (API) and 52100 (Relay)...
netsh advfirewall firewall delete rule name="RemoteDesk Server Ports" >nul 2>&1
netsh advfirewall firewall add rule name="RemoteDesk Server Ports" dir=in action=allow protocol=TCP localport=5100,52100 >nul 2>&1

echo [4/4] Creating Desktop and Windows Startup shortcuts...
powershell -NoProfile -Command "$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut([Environment]::GetFolderPath('Desktop') + '\RemoteDesk Server.lnk'); $s.TargetPath = '%INSTALL_DIR%\START_SERVER.bat'; $s.WorkingDirectory = '%INSTALL_DIR%'; $s.Save(); $st = $ws.CreateShortcut([Environment]::GetFolderPath('Startup') + '\RemoteDesk Server.lnk'); $st.TargetPath = '%INSTALL_DIR%\START_SERVER.bat'; $st.WorkingDirectory = '%INSTALL_DIR%'; $st.Save();"

echo.
echo ============================================================================
echo   SUCCESS! RemoteDesk Control Server installed to %INSTALL_DIR%
echo   Starting Control Server (port 5100) and Local Relay (port 52100) now...
echo ============================================================================
call "%INSTALL_DIR%\START_SERVER.bat"
pause

