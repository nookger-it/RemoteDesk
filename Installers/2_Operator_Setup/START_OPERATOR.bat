@echo off
cd /d "%~dp0"
if not exist "%~dp0app\server_url.txt" (
    echo http://192.168.1.110:5100> "%~dp0app\server_url.txt"
)
start "" /D "%~dp0app" "%~dp0app\RemoteDesk.Operator.Windows.exe"

