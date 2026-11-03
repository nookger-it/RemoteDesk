@echo off
chcp 65001 >nul
title RemoteDesk V1 — Agent Launcher (เครื่องปลายทางในวง LAN)
echo ============================================================================
echo   RemoteDesk V1 — กำลังเริ่มทำงาน Agent สำหรับเครื่องปลายทางในวง LAN
echo ============================================================================
echo.

REM ============================================================================
REM ตั้งค่า IP ของเครื่องแม่ข่าย (Control Server + Relay)
REM ค่าเริ่มต้นชี้ไปที่เครื่องแม่ข่าย 192.168.1.110 (สามารถแก้ไขได้หาก IP เปลี่ยน)
REM ============================================================================
set REMOTEDESK_SERVER_IP=192.168.1.110
set REMOTEDESK_API_URL=http://%REMOTEDESK_SERVER_IP%:5100
set REMOTEDESK_RELAY_HOST=%REMOTEDESK_SERVER_IP%
set REMOTEDESK_RELAY_PORT=52100
set REMOTEDESK_DIRECT_PORT=52000
set REMOTEDESK_CAPTURE_WIDTH=1280
set REMOTEDESK_CAPTURE_HEIGHT=720
set REMOTEDESK_TARGET_FPS=15

echo [ข้อมูลการเชื่อมต่อ]
echo   - เครื่องแม่ข่าย (Control API) : %REMOTEDESK_API_URL%
echo   - พอร์ต Direct LAN TLS      : %REMOTEDESK_DIRECT_PORT%
echo   - พอร์ต Local Relay         : %REMOTEDESK_RELAY_HOST%:%REMOTEDESK_RELAY_PORT%
echo.

REM เปิด Firewall พอร์ต 52000 สำหรับ Direct LAN (หากรันด้วยสิทธิ์ Admin)
netsh advfirewall firewall add rule name="RemoteDesk Agent Direct LAN (52000)" dir=in action=allow protocol=TCP localport=52000 >nul 2>&1

echo [1/2] กำลังเปิดบริการจับภาพหน้าจอและรับคำสั่งควบคุม (Agent.Interactive)...
start "RemoteDesk Agent Interactive (52000)" "%~dp0agent-interactive\RemoteDesk.Agent.Interactive.exe"

timeout /t 2 /nobreak >nul

echo [2/2] กำลังลงทะเบียนเครื่องเข้ากับแม่ข่ายและส่งสัญญาณ Heartbeat (Agent.Service)...
start "RemoteDesk Agent Service" "%~dp0agent-service\RemoteDesk.Agent.Service.exe"

echo.
echo [สำเร็จ] เครื่องนี้พร้อมให้รีโมทจากเครื่องแม่ข่าย (%REMOTEDESK_SERVER_IP%) แล้วครับ!
echo หมายเหตุ: กรุณาเปิดหน้าต่างสีดำทั้ง 2 หน้าต่างค้างไว้ระหว่างใช้งานรีโมท
echo.
pause

