# คู่มือการติดตั้ง Windows Agent & Local Server (2-PC Pilot Guide)

เอกสารนี้อธิบายขั้นตอนการติดตั้งและทดสอบระบบ **RemoteDesk V1 (LAN-First)** บนชุดทดสอบ 2 เครื่อง (`PC-A` และ `PC-B`)

---

## 1. บทบาทของเครื่องในชุดทดสอบ 2 เครื่อง

| เครื่อง | บทบาท | โปรแกรมที่รัน | พอร์ตที่เปิดรับ (เฉพาะ LAN) |
|---|---|---|---|
| **PC-A** | Local Server + Operator Client | `RemoteDesk.Api`, `RemoteDesk.Relay`, `remotedesk-dashboard`, `RemoteDesk.Operator.Windows` | `5100` (API), `52100` (Relay), `3000` (Web UI) |
| **PC-B** | เครื่องปลายทาง (Target Agent PC) | `RemoteDesk.Agent.Service` (Session 0) + `RemoteDesk.Agent.Interactive` (User Session) | `52000–52010` (Direct LAN TLS Stream) |

---

## 2. ขั้นตอนตั้งค่า Firewall (ทั้ง PC-A และ PC-B)

เปิด PowerShell ในฐานะ **Administrator** แล้วรันสคริปต์:

```powershell
# ตรวจสอบแผนพอร์ตก่อน
.\deploy\firewall\configure-lan-firewall.ps1

# สั่งเปิดพอร์ตเฉพาะวง LAN (Profile: Private/Domain, RemoteAddress: LocalSubnet)
.\deploy\firewall\configure-lan-firewall.ps1 -ApplyRules
```

> **ข้อควรระวังด้านความปลอดภัย:** ห้ามทำ Port Forwarding บน Router ออกสู่อินเทอร์เน็ตสาธารณะใน V1 เด็ดขาด

---

## 3. ขั้นตอนเริ่มระบบบน PC-A (Local Server + Operator)

1. รันสคริปต์เริ่ม `RemoteDesk.Api` และ `RemoteDesk.Relay`:
   ```powershell
   .\deploy\local-server\start-local-server.ps1 -ApiUrls "http://0.0.0.0:5100" -RelayPort 52100
   ```
2. (ทางเลือก) เปิด Web Dashboard:
   ```powershell
   cd src/dashboard/remotedesk-dashboard
   npm run dev
   ```
   เปิดเบราว์เซอร์ไปที่ `http://localhost:3000`
3. เปิดโปรแกรม **Windows Operator Client (WPF)**:
   ```powershell
   dotnet run --project src/operator/RemoteDesk.Operator.Windows/RemoteDesk.Operator.Windows.csproj
   ```

---

## 4. ขั้นตอนติดตั้งและเริ่ม Agent บน PC-B (Windows Agent)

บนเครื่อง `PC-B` ให้ระบุ IP ของเครื่อง `PC-A` (เช่น `192.168.1.10`):

```powershell
# รันโหมด Pilot Testing (เปิดทั้ง Agent.Interactive และ Agent.Service)
.\deploy\windows-agent\install-agent.ps1 -ApiBaseUrl "http://192.168.1.10:5100" -DirectLanPort 52000

# หรือลงทะเบียนเป็น Windows Service ถาวร (ต้องรันด้วยสิทธิ์ Administrator)
.\deploy\windows-agent\install-agent.ps1 -ApiBaseUrl "http://192.168.1.10:5100" -DirectLanPort 52000 -RegisterWindowsService
```

---

## 5. การเพิกถอนเครื่อง (Revoke) หรือตัดการเชื่อมต่อจากเครื่องปลายทาง

- **ผู้ดูแลระบบ (บน Web Dashboard):** กดปุ่ม **Disable** หรือ **Revoke** ที่แถวของเครื่อง `PC-B` เพื่อตัด Session ทันทีและบล็อกไม่ให้ `PC-B` ส่ง Heartbeat หรือรับการเชื่อมต่อใหม่ได้ (`T11`)
- **ผู้ใช้ที่เครื่องปลายทาง (PC-B):** เมื่อมีการควบคุมหน้าจอ แถบสถานะ `AgentSessionBannerManager` จะแสดงชื่อผู้ควบคุมและโหมดการควบคุมเสมอ ผู้ใช้ปลายทางสามารถสั่งตัดการเชื่อมต่อ (`DisconnectByLocalUserAsync`) ได้ทันที

