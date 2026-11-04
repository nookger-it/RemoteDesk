@echo off
chcp 65001 >nul
title RemoteDesk V1 — Push to GitHub (nookger-it/RemoteDesk)
echo ============================================================================
echo   กำลังอัปโหลดโค้ด RemoteDesk V1 ขึ้น https://github.com/nookger-it/RemoteDesk.git
echo   บัญชี: nookger-it (nookger.yt@gmail.com)
echo ============================================================================
echo.
echo หากมีหน้าต่าง GitHub Sign-in เด้งขึ้นมา กรุณากด "Sign in with your browser"
echo เพื่อยืนยันสิทธิ์ของบัญชี nookger-it ครับ
echo.

cd /d "d:\remote desktop"
git config user.name "nookger-it"
git config user.email "nookger.yt@gmail.com"
git config credential.https://github.com.username "nookger-it"
git remote set-url origin https://github.com/nookger-it/RemoteDesk.git

git push -u origin main --tags

echo.
echo ============================================================================
echo   อัปโหลดเสร็จสมบูรณ์! สามารถตรวจสอบได้ที่ https://github.com/nookger-it/RemoteDesk
echo ============================================================================
pause

