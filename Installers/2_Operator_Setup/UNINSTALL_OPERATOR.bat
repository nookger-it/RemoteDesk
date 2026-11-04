@echo off
title RemoteDesk V1 - Uninstall Operator Client
taskkill /F /IM RemoteDesk.Operator.Windows.exe >nul 2>&1
del /F /Q "%USERPROFILE%\Desktop\RemoteDesk Operator.lnk" >nul 2>&1
echo Operator Client shortcut removed.
pause

