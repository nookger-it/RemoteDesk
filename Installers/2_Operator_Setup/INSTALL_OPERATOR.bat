@echo off
title RemoteDesk V1 - Operator Client Installer
color 0B
cd /d "%~dp0"

echo ============================================================================
echo   RemoteDesk V1 - Operator Control Client Installer (Windows 10/11 x64)
echo   Target Directory: C:\RemoteDesk\Operator
echo ============================================================================
echo.

set "DEFAULT_IP=192.168.1.110"
set /p SERVER_IP="Enter Control Server IP Address (Press Enter for default %DEFAULT_IP%): "
if "%SERVER_IP%"=="" set "SERVER_IP=%DEFAULT_IP%"

set "INSTALL_DIR=C:\RemoteDesk\Operator"

echo.
echo [1/3] Stopping any running Operator Client...
taskkill /F /IM RemoteDesk.Operator.Windows.exe >nul 2>&1

echo [2/3] Copying Operator Client files to %INSTALL_DIR% ...
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"
xcopy /E /I /Y "%~dp0app\*" "%INSTALL_DIR%\" >nul
echo http://%SERVER_IP%:5100> "%INSTALL_DIR%\server_url.txt"
echo http://%SERVER_IP%:5100> "%~dp0app\server_url.txt"

echo [3/3] Creating Desktop shortcut 'RemoteDesk Operator'...
powershell -NoProfile -Command "$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut([Environment]::GetFolderPath('Desktop') + '\RemoteDesk Operator.lnk'); $s.TargetPath = '%INSTALL_DIR%\RemoteDesk.Operator.Windows.exe'; $s.WorkingDirectory = '%INSTALL_DIR%'; $s.Save();"

echo.
echo ============================================================================
echo   SUCCESS! RemoteDesk Operator Client installed to %INSTALL_DIR%
echo   Configured Server URL: http://%SERVER_IP%:5100
echo   Launching RemoteDesk Operator Client...
echo ============================================================================
start "" /D "%INSTALL_DIR%" "%INSTALL_DIR%\RemoteDesk.Operator.Windows.exe"
pause

