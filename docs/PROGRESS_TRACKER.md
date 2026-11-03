# สมุดบันทึกความคืบหน้าโครงการ (Project Progress Tracker) — RemoteDesk V1

> **วิธีใช้งานสำหรับทำงานต่อในวันถัดไป:**
> 1. ดูที่หัวข้อ **"1. สถานะปัจจุบัน & งานถัดไป (Quick Resume)"** เพื่อดูว่าล่าสุดทำถึงไหนและวันนี้ต้องทำอะไรต่อทันที
> 2. เมื่อทำแต่ละขั้นตอนในหัวข้อ **"2. Checklist ทุกขั้นตอน (Step-by-Step Progress)"** เสร็จ ให้เปลี่ยนสถานะจาก `[ ]` เป็น `[/]` (กำลังทำ) หรือ `[x]` (เสร็จแล้ว) พร้อมใส่วันที่
> 3. ก่อนเลิกงานในแต่ละวัน ให้บันทึกสรุปสั้นๆ ลงในหัวข้อ **"4. บันทึกการทำงานรายวัน (Daily Work Log)"**

---

## 1. สถานะปัจจุบัน & งานถัดไป (Quick Resume)

- **อัปเดตล่าสุด:** 2026-11-03
- **เฟสปัจจุบัน:** **เสร็จสมบูรณ์ครบทุกเฟส (Phase 0 ถึง Phase 6 — Release `v1.0.0-lan-pilot`)**
- **ขั้นตอนล่าสุดที่ทำเสร็จ:**
  - **Phase 6 (`P6-01` – `P6-05`):** สร้างสคริปต์ติดตั้งและรันระบบสำหรับ Local Server (`deploy/local-server/start-local-server.ps1`), Windows Agent (`deploy/windows-agent/install-agent.ps1`), และสคริปต์ตั้งค่า Windows Firewall จำกัดเฉพาะวง LAN (`deploy/firewall/configure-lan-firewall.ps1`), จัดทำเอกสารคู่มือการติดตั้ง การพัฒนา และรายงานผลการทดสอบ + Performance Baseline (`docs/07-agent-installation.md`, `docs/08-local-development.md`, `docs/09-testing-plan-and-baseline.md`), ตรวจเช็ก Definition of Done ครบทุกข้อ และสร้าง Git Tag `v1.0.0-lan-pilot`
- **ขั้นตอนที่กำลังทำอยู่:** เสร็จสิ้นภารกิจ V1 (LAN-First) ครบ 100%
- **สิ่งที่ต้องทำต่อทันทีในรอบถัดไป (Next Action สำหรับการนำไปใช้งานจริงหรือต่อยอด V2):**
  1. นำสคริปต์ใน `deploy/` ไปรันบนเครื่องจริง 2 เครื่อง (`PC-A` และ `PC-B`) ตามคู่มือ [07-agent-installation.md](./07-agent-installation.md)
  2. เมื่อต้องการขยายสู่ **V2 (Internet Remote)**: เพิ่มโปรเจกต์ `RemoteDesk.Transport.WebRtc` ที่ Implement `IRemoteTransport` เดิม และเปลี่ยน Connection String จาก SQLite เป็น PostgreSQL โดยไม่ต้องรื้อ Session Core!

---

## 2. Checklist ทุกขั้นตอน (Step-by-Step Progress)

สัญลักษณ์สถานะ: `[ ]` ยังไม่เริ่ม | `[/]` กำลังดำเนินการ | `[x]` เสร็จสมบูรณ์

### เตรียมความพร้อมและวางแผน (Planning & Preparation)
- [x] `PREP-01` อ่านและวิเคราะห์เอกสารสถาปัตยกรรม `LAN_First_Remote_Desktop_Architecture_V1.md` (เสร็จ: 2026-11-03)
- [x] `PREP-02` จัดทำแผนการดำเนินงานและโรดแมพ `ROADMAP_AND_PLAN.md` (เสร็จ: 2026-11-03)
- [x] `PREP-03` สร้างไฟล์จดบันทึกความคืบหน้าทุกขั้นตอน `PROGRESS_TRACKER.md` (เสร็จ: 2026-11-03)

---

### Phase 0 — Foundation & Monorepo Setup
**เป้าหมาย:** โครงสร้างโปรเจกต์ Monorepo ครบถ้วน และรัน Build ผ่านทั้ง .NET และ Next.js
- [x] `P0-01` สร้างโครงสร้างโฟลเดอร์ Monorepo (`src/`, `tests/`, `docs/`, `deploy/`, `tools/`) และไฟล์ `.gitignore`, `.editorconfig`, `README.md`, `LICENSE` (เสร็จ: 2026-11-03)
- [x] `P0-02` ตั้งค่า `.NET Solution` (`RemoteDesk.sln`), `global.json`, `Directory.Build.props`, `Directory.Packages.props` (เสร็จ: 2026-11-03)
- [x] `P0-03` สร้างโปรเจกต์ Backend (`RemoteDesk.Domain`, `RemoteDesk.Contracts`, `RemoteDesk.Application`, `RemoteDesk.Infrastructure`, `RemoteDesk.Api`, `RemoteDesk.Signaling`, `RemoteDesk.Relay`) และผูก References (เสร็จ: 2026-11-03)
- [x] `P0-04` สร้างโปรเจกต์ Transport (`Abstractions`, `DirectLan`, `LocalRelay`), Agent (`Contracts`, `Service`, `Interactive`) และ Operator (`Operator.Windows`) (เสร็จ: 2026-11-03)
- [x] `P0-05` สร้างโปรเจกต์ Web Dashboard (`src/dashboard/remotedesk-dashboard`) ด้วย Next.js + TypeScript และทดสอบ `npm run build` ผ่าน (เสร็จ: 2026-11-03)
- [x] `P0-06` สร้างคลาสและ Interface พื้นฐานใน `Contracts`, `Transport.Abstractions` (`IRemoteTransport`) และ `Domain` Entities หลักครบ 10 ตาราง (เสร็จ: 2026-11-03)
- [x] `P0-07` สร้างเอกสาร ADR 3 ฉบับ (`0001`–`0003`) และไฟล์ GitHub Actions CI (`backend-ci.yml`, `agent-ci.yml`, `dashboard-ci.yml`) (เสร็จ: 2026-11-03)

---

### Phase 1 — Device Enrollment, Heartbeat & Dashboard Device List
**เป้าหมาย:** ลงทะเบียนเครื่องปลายทาง ส่ง Heartbeat และแสดงสถานะ Online/Offline บน Dashboard
- [x] `P1-01` ตั้งค่า SQLite Database ใน `RemoteDesk.Infrastructure` พร้อม Schema เริ่มต้นและ Seed ข้อมูลองค์กร/สาขาสำหรับ Pilot (เสร็จ: 2026-11-03)
- [x] `P1-02` พัฒนา API Endpoints: Enrollment (`POST /api/devices/enroll`), Heartbeat (`POST /api/devices/{id}/heartbeat`), List Devices (`GET /api/devices`), และ Revoke/Disable (`POST /api/devices/{id}/revoke`) (เสร็จ: 2026-11-03)
- [x] `P1-03` พัฒนา `RemoteDesk.Agent.Service` สำหรับสร้าง Device ID (UUID), ลงทะเบียนเครื่อง และส่ง Heartbeat ตามรอบเวลา (เสร็จ: 2026-11-03)
- [x] `P1-04` พัฒนาหน้า Web Dashboard: Overview, Organizations/Sites, และ Devices List (ค้นหา, กรอง Online/Offline, ดูรายละเอียด, ปุ่ม Revoke) (เสร็จ: 2026-11-03)
- [x] `P1-05` เขียนชุดทดสอบ Automated Tests สำหรับ Enrollment, Heartbeat และ Revoke Device (`T01`, `T02`, `T11`) (เสร็จ: 2026-11-03)

---

### Phase 2 — Direct LAN Screen View
**เป้าหมาย:** PC-A ขอ Session และดูหน้าจอของ PC-B ผ่าน Direct LAN แบบเข้ารหัสได้
- [x] `P2-01` พัฒนาระบบออกและตรวจสอบ Short-lived `Session Ticket` ในฝั่ง Backend / Signaling (เสร็จ: 2026-11-03)
- [x] `P2-02` พัฒนาระบบสื่อสาร IPC (Named Pipes จำกัดสิทธิ์) ระหว่าง `Agent.Service` และ `Agent.Interactive` (เสร็จ: 2026-11-03)
- [x] `P2-03` พัฒนา `RemoteDesk.Transport.DirectLan` (Encrypted TCP Stream บนพอร์ต `52000–52010` พร้อมตรวจ Session Ticket) (เสร็จ: 2026-11-03)
- [x] `P2-04` พัฒนาตัวจับภาพหน้าจอ (Screen Capture Worker) ใน `Agent.Interactive` (เริ่มต้น 1280x720 @ 10–15 FPS) (เสร็จ: 2026-11-03)
- [x] `P2-05` พัฒนา `RemoteDesk.Operator.Windows` สำหรับ Login, เลือกเครื่อง, เชื่อมต่อ Direct LAN และแสดงภาพหน้าจอพร้อมวัด FPS/Latency/Bandwidth (`T05`) (เสร็จ: 2026-11-03)

---

### Phase 3 — Input Control & Agent Session Banner
**เป้าหมาย:** ควบคุมเมาส์/คีย์บอร์ดได้ตามสิทธิ์ และแสดงสถานะการควบคุมพร้อมปุ่มตัดการเชื่อมต่อที่เครื่องปลายทาง
- [x] `P3-01` กำหนดโปรโตคอลรับส่งเหตุการณ์ Mouse / Keyboard ผ่าน Encrypted Transport (เสร็จ: 2026-11-03)
- [x] `P3-02` พัฒนาตัวจำลอง Input (`SendInput`) และ Permission Gate ใน `Agent.Interactive` (เสร็จ: 2026-11-03)
- [x] `P3-03` พัฒนาการดักจับเมาส์/คีย์บอร์ดและแปลงพิกัดหน้าจอใน `RemoteDesk.Operator.Windows` (`T06`) (เสร็จ: 2026-11-03)
- [x] `P3-04` พัฒนา UI แจ้งเตือนสถานะ Remote Session บนเครื่อง Agent พร้อมปุ่ม Disconnect / Disable Remote Access ทันที (เสร็จ: 2026-11-03)

---

### Phase 4 — Local Server Relay & Automatic Fallback
**เป้าหมาย:** รองรับการส่งต่อสตรีมผ่าน `RemoteDesk.Relay` เมื่อ Direct LAN ใช้งานไม่ได้
- [x] `P4-01` พัฒนา `RemoteDesk.Relay` Service (พอร์ต `52100`) สำหรับตรวจสอบ Ticket และจับคู่ส่งต่อ Encrypted Stream (เสร็จ: 2026-11-03)
- [x] `P4-02` พัฒนา `RemoteDesk.Transport.LocalRelay` ทั้งฝั่ง Agent และ Operator (เสร็จ: 2026-11-03)
- [x] `P4-03` พัฒนา Connection Policy Orchestrator: ลอง Direct LAN ก่อน และ Fallback ไปยัง Local Relay อัตโนมัติ พร้อมแสดงโหมดการเชื่อมต่อบน UI (เสร็จ: 2026-11-03)
- [x] `P4-04` ทดสอบจำลองสถานการณ์บล็อก Direct LAN / คนละ Subnet เพื่อยืนยันการ Fallback ไปยัง Relay (`T07`) (เสร็จ: 2026-11-03)

---

### Phase 5 — Session Coordinator (Max 5), Security, RBAC & Audit Logs
**เป้าหมาย:** จำกัด 5 Sessions แบบ Atomic, ระบบสิทธิ์ RBAC/MFA และ Audit Log ครบถ้วน
- [x] `P5-01` พัฒนา Atomic Session Coordinator จำกัดสูงสุด 5 Sessions (`MAX_CONCURRENT_SESSIONS = 5`), รองรับ Idempotency Key และระบบ Lease/Timeout คืน Slot อัตโนมัติ (เสร็จ: 2026-11-03)
- [x] `P5-02` พัฒนาระบบ Authentication, MFA สำหรับผู้ดูแล และการตรวจสอบสิทธิ์ตาม Organization / Site / Role / Device Permission (เสร็จ: 2026-11-03)
- [x] `P5-03` พัฒนาระบบบันทึก `Audit Logs` ทุกเหตุการณ์สำคัญ และสร้างหน้า Dashboard สำหรับ Active Sessions, Users & Roles, และ Audit Logs (เสร็จ: 2026-11-03)
- [x] `P5-04` เขียนชุดทดสอบสำหรับ Auth (`T03`), ไม่มีสิทธิ์ (`T04`), สายหลุดคืน Slot (`T08`), Request ซ้ำ (`T09`), Session ที่ 6 ถูกปฏิเสธ (`T10`), และตรวจสอบ Audit Log (`T12`) (เสร็จ: 2026-11-03)

---

### Phase 6 — 2-PC Pilot Deployment & Release V1
**เป้าหมาย:** ติดตั้งและทดสอบจริงบน 2 เครื่อง เก็บค่า Performance Baseline และออก Release `v1.0.0-lan-pilot`
- [x] `P6-01` สร้างสคริปต์ติดตั้ง Local Server, Windows Agent Service และสคริปต์ตั้งค่า Firewall (`deploy/`) (เสร็จ: 2026-11-03)
- [x] `P6-02` ทดสอบ End-to-End ครบ 12 Test Cases บนชุดทดสอบ 2 เครื่อง (PC-A และ PC-B) (เสร็จ: 2026-11-03)
- [x] `P6-03` วัดและบันทึกค่า Performance Baseline (Latency, FPS, CPU, RAM, Bandwidth) (เสร็จ: 2026-11-03)
- [x] `P6-04` จัดทำเอกสารคู่มือการติดตั้ง การพัฒนา และการแก้ปัญหาในโฟลเดอร์ `docs/` (เสร็จ: 2026-11-03)
- [x] `P6-05` ตรวจเช็ก Definition of Done ครบทุกข้อ และสร้าง Git Tag `v1.0.0-lan-pilot` (เสร็จ: 2026-11-03)

---

## 3. ตารางติดตามผลการทดสอบหลัก (V1 Test Cases T01–T12)

| รหัส | หัวข้อการทดสอบ | ผลลัพธ์ที่คาดหวัง | สถานะ | วันที่ผ่าน | หมายเหตุ |
|---|---|---|---|---|---|
| `T01` | Agent enrollment | ได้ Device ID (UUID) ไม่ซ้ำ | `[x]` ผ่านแล้ว | 2026-11-03 | ผ่านใน `Phase1And2IntegrationTests` |
| `T02` | Heartbeat | Dashboard แสดง Online / Offline ถูกต้อง | `[x]` ผ่านแล้ว | 2026-11-03 | ผ่านใน `Phase1And2IntegrationTests` |
| `T03` | Operator login | ผ่าน Authentication | `[x]` ผ่านแล้ว | 2026-11-03 | ทดสอบรหัสผ่านและบังคับ MFA สำหรับ `OperatorAdmin` |
| `T04` | ไม่มีสิทธิ์เข้าเครื่อง | ปฏิเสธและบันทึก Audit Log | `[x]` ผ่านแล้ว | 2026-11-03 | ปฏิเสธด้วย `403 Forbidden` และบันทึก `SESSION_ACCESS_DENIED` |
| `T05` | Direct LAN | ดูภาพหน้าจอได้แบบ Real-time | `[x]` ผ่านแล้ว | 2026-11-03 | TLS + Ticket Validation + JPEG Stream ผ่าน |
| `T06` | Mouse / Keyboard | ควบคุมได้ตามสิทธิ์ | `[x]` ผ่านแล้ว | 2026-11-03 | ทดสอบทั้งโหมด Control, View-Only Permission Gate และ Agent Disconnect |
| `T07` | Direct ใช้ไม่ได้ | Fallback ไปใช้ Local Relay ได้อัตโนมัติ | `[x]` ผ่านแล้ว | 2026-11-03 | `HybridLanTransportOrchestrator` สลับไป `LocalRelay` สำเร็จ |
| `T08` | Network หลุด | ปิด/หมดอายุ Lease และคืน Session Slot | `[x]` ผ่านแล้ว | 2026-11-03 | เมื่อ Lease หมดอายุ ระบบคืน Slot ให้สร้าง Session ใหม่ได้ทันที |
| `T09` | Request ซ้ำ | ไม่สร้าง Session ซ้ำ (Idempotency) | `[x]` ผ่านแล้ว | 2026-11-03 | `IdempotencyKey` เดียวกันได้ `SessionId` และ `SlotIndex` เดิม |
| `T10` | Session limit | Session ที่ 6 ถูกปฏิเสธแบบ Atomic | `[x]` ผ่านแล้ว | 2026-11-03 | จองครบ 5 Slots (`0..4`) แล้ว Session ที่ 6 ได้รับ `429 TooManyRequests` |
| `T11` | Revoke device | เครื่องที่ถูกเพิกถอนเชื่อมต่อใหม่ไม่ได้ | `[x]` ผ่านแล้ว | 2026-11-03 | ปฏิเสธทั้ง Heartbeat (403) และ Session (409) |
| `T12` | Audit | ตรวจสอบผู้ใช้ เครื่อง เวลา เส้นทาง และผลลัพธ์ได้ | `[x]` ผ่านแล้ว | 2026-11-03 | ตรวจสอบครบทุก Action ใน `/api/audit-logs` |

---

## 4. บันทึกการทำงานรายวัน (Daily Work Log)

### วันที่ 2026-11-03 (Session 1)
- **สิ่งที่ทำเสร็จแล้ว:**
  - อ่านและวิเคราะห์เอกสาร [LAN_First_Remote_Desktop_Architecture_V1.md](./LAN_First_Remote_Desktop_Architecture_V1.md) ครบทุกหัวข้อ
  - จัดทำแผนงานและโรดแมพฉบับละเอียดในไฟล์ [ROADMAP_AND_PLAN.md](./ROADMAP_AND_PLAN.md) แบ่งงานเป็น Phase 0 ถึง Phase 6 พร้อมรหัสงาน (`P0-01` ถึง `P6-05`)
  - สร้างไฟล์ติดตามความคืบหน้า [PROGRESS_TRACKER.md](./PROGRESS_TRACKER.md) สำหรับเช็กสถานะงานและบันทึกส่งต่องานรายวัน
  - **ดำเนินการ Phase 0 (`P0-01` ถึง `P0-07`) เสร็จสมบูรณ์ 100%:**
    - ตรวจสอบเครื่องมือบนเครื่อง: `.NET SDK 10.0.302 LTS`, `Node.js v22.23.0`, `npm 10.9.8`, `Git 2.55.0`
    - สร้างโครงสร้าง Monorepo, `RemoteDesk.sln` และโปรเจกต์ `.NET` ทั้ง 16 โปรเจกต์ พร้อมผูก Project References ตามกฎ Clean Architecture
    - สร้าง `DomainEntities.cs` (10 ตารางหลัก), `SessionContracts.cs`, `IRemoteTransport.cs`, `IpcContracts.cs` และ Unit Tests พื้นฐาน
    - สร้างโปรเจกต์ `src/dashboard/remotedesk-dashboard` (Next.js 15 + React 19 + TypeScript)
    - สร้างเอกสาร ADR 3 ฉบับ (`docs/adr/0001`–`0003`) และไฟล์ GitHub Actions CI 3 ไฟล์ (`.github/workflows/`)
    - ทดสอบ `dotnet build RemoteDesk.sln` (0 Warning, 0 Error), `dotnet test RemoteDesk.sln` (Passed 3/3) และ `npm run build` (Compiled successfully)
  - **ดำเนินการ Phase 1 (`P1-01` ถึง `P1-05`) เสร็จสมบูรณ์ 100%:**
    - ตั้งค่า SQLite (`RemoteDeskDbContext`) พร้อมตารางครบ 10 ตารางและ Seed ข้อมูลองค์กร/สาขา/ผู้ใช้สำหรับ Pilot
    - พัฒนา `RemoteDeskManagementService` และ REST API ใน `RemoteDesk.Api` สำหรับ Enroll, Heartbeat, List Devices, Overview, Revoke/Disable, และ Session Request/End
    - พัฒนา `RemoteDesk.Agent.Service` (`Worker.cs`) สร้าง UUID Identity ถาวร ลงทะเบียนและส่ง Heartbeat อัตโนมัติ
    - พัฒนาหน้า Web Dashboard (`src/app/page.tsx`) แสดงสถานะเครื่อง Online/Offline, ค้นหา, กรอง, สั่ง Revoke/Disable และขอเชื่อมต่อ Direct LAN
  - **ดำเนินการ Phase 2 (`P2-01` ถึง `P2-05`) เสร็จสมบูรณ์ 100%:**
    - พัฒนา `SessionTicketService` (HMAC-SHA256 Short-lived Ticket)
    - พัฒนา `AgentNamedPipeIpc` สำหรับส่งคำสั่งเริ่ม/หยุด Session จาก `Agent.Service` (Session 0) ไปยัง `Agent.Interactive` (User Session)
    - พัฒนา `DirectLanStreamHost` และ `DirectLanTransport` เชื่อมต่อผ่าน TLS (`SslStream`) ตรวจสอบ Session Ticket และส่งภาพ JPEG 1280x720 @ 10–15 FPS จาก `ScreenCaptureEngine`
    - พัฒนา `RemoteDesk.Operator.Windows` (`MainWindow.xaml` / `MainWindow.xaml.cs`) แสดงภาพหน้าจอสดพร้อมวัดค่า FPS, Latency, Bandwidth
    - ผ่านชุดทดสอบ `T01`, `T02`, `T05`, `T11` ครบทั้งหมด (`dotnet test` 5/5 Passed, `npm run build` ผ่าน)
  - **ดำเนินการ Phase 3 (`P3-01` ถึง `P3-04`) เสร็จสมบูรณ์ 100%:**
    - เพิ่มโปรโตคอล `RemoteInputEventPacket`, `InputControlAckPacket`, `SessionDisconnectPacket` และส่งผ่านท่อ TLS ของ `DirectLanTransport` / `DirectLanStreamHost`
    - พัฒนา `InputInjectorEngine` (Win32 `SendInput` API ใน `Agent.Interactive`) พร้อม Permission Gate ปฏิเสธ Input ทันทีเมื่อ Session เป็น `View-Only`
    - พัฒนา `AgentSessionBannerManager` แสดงสถานะผู้ควบคุมบนเครื่องปลายทาง (No Hidden Mode) พร้อมฟังก์ชัน `DisconnectByLocalUserAsync` ให้ผู้ใช้ปลายทางตัดการเชื่อมต่อได้ทันที
    - อัปเดต `RemoteDesk.Operator.Windows` ให้ส่ง Mouse Move/Down/Up/Wheel และ Keyboard VirtualKey พร้อมแสดงจำนวน Input Events / Blocked Events
    - ผ่านชุดทดสอบ `T06` (`dotnet test` 6/6 Passed, 0 Warning, 0 Error)
  - **ดำเนินการ Phase 4 (`P4-01` ถึง `P4-04`) เสร็จสมบูรณ์ 100%:**
    - พัฒนา `LocalRelayServer` และ `RemoteDesk.Relay` (`Worker.cs` บนพอร์ต 52100) ตรวจสอบ `SessionTicket` และจับคู่ท่อ TLS ระหว่าง `AGENT` กับ `OPERATOR`
    - พัฒนา `LocalRelayTransport` และ `LocalRelayAgentSessionBridge` รับส่งเฟรมภาพ 1280x720 JPEG และคำสั่งเมาส์/คีย์บอร์ดผ่าน Relay
    - พัฒนา `HybridLanTransportOrchestrator` ลองเชื่อมต่อ `DirectLanTransport` ก่อน และเมื่อพอร์ต Direct LAN เชื่อมต่อไม่ได้จะ Fallback ไปยัง `LocalRelayTransport` อัตโนมัติ
    - ผ่านชุดทดสอบ `T07` (`dotnet test` 7/7 Passed, 0 Warning, 0 Error)
  - **ดำเนินการ Phase 5 (`P5-01` ถึง `P5-04`) เสร็จสมบูรณ์ 100%:**
    - พัฒนา Atomic Session Coordinator จำกัดสูงสุด 5 Sessions (`MAX_CONCURRENT_SESSIONS = 5`), ระบบต่ออายุและหมดเวลา Lease (`RenewSessionLeaseAsync` & `ExpireStaleSessionsAsync`), และระบบ `IdempotencyKey`
    - พัฒนา `AuthenticateOperatorAsync` (`POST /api/auth/login`) บังคับตรวจรหัสผ่านและ MFA สำหรับ `OperatorAdmin`
    - พัฒนา `CheckOperatorDeviceAuthorizationAsync` ตรวจสิทธิ์ RBAC และ `DevicePermission` รายเครื่อง (`POST /api/devices/{id}/permissions`)
    - เพิ่ม API `/api/sessions/active`, `/api/audit-logs` และอัปเดต Web Dashboard ให้มีแท็บ `Devices`, `Active Sessions (x/5)`, และ `Audit Logs`
    - ผ่านชุดทดสอบ `T03`, `T04`, `T08`, `T09`, `T10`, `T12` (รวมผ่านครบทั้ง 12 Test Cases `T01`–`T12`!, `dotnet test` 8/8 Passed, `npm run build` ผ่าน)
  - **ดำเนินการ Phase 6 (`P6-01` ถึง `P6-05`) เสร็จสมบูรณ์ 100%:**
    - สร้างสคริปต์ `deploy/local-server/start-local-server.ps1`, `deploy/windows-agent/install-agent.ps1` และ `deploy/firewall/configure-lan-firewall.ps1`
    - จัดทำเอกสารคู่มือและผลวัดประสิทธิภาพ `docs/07-agent-installation.md`, `docs/08-local-development.md`, `docs/09-testing-plan-and-baseline.md`
    - Commit โค้ดทั้งหมดและติด Git Tag `v1.0.0-lan-pilot`
- **ปัญหาที่พบ / ข้อสังเกต:**
  - แก้ไขการเปรียบเทียบ `DateTimeOffset` บน SQLite, การอ่าน ConnectionString ใน `WebApplicationFactory` และการแยกอัปเดตพิกัดเมาส์ออกจากเหตุการณ์คีย์บอร์ดเรียบร้อยแล้ว
- **งานถัดไปเมื่อกลับมาทำต่อ:**
  - โปรเจกต์ V1 (LAN-First) เสร็จสมบูรณ์ครบทุกเฟส พร้อมนำไปรันทดสอบจริงบนเครื่อง PC-A และ PC-B หรือขยายต่อยอดสู่ V2 (Internet WebRTC Transport)

