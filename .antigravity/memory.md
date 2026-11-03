# Project Memory — RemoteDesk V1 (LAN-First Remote Desktop)

> **สถานะโปรเจกต์ปัจจุบัน:** เสร็จสมบูรณ์ครบทุกเฟส Phase 0 ถึง Phase 6 — Release Tag `v1.0.0-lan-pilot` (ผ่านชุดทดสอบ `T01`–`T12` ครบ 100%, `dotnet test` 8/8 Passed, `npm run build` ผ่าน)

---

## 1. Tech Stack และ Libraries หลักที่ใช้

- **Backend & Services:** C# / ASP.NET Core (.NET LTS), REST API + WebSocket Signaling
- **Windows Agent:** C# / .NET Worker Service (`Agent.Service` ใน Session 0) + User-session Worker (`Agent.Interactive` + Win32 APIs / Desktop Capture / `SendInput`) สื่อสารผ่าน IPC
- **Windows Operator Client:** C# / WPF หรือ WinUI 3 (Remote Viewer & Input Client)
- **Web Dashboard:** Next.js + TypeScript (ติดต่อผ่าน Backend API เท่านั้น)
- **Transport Layer:** Hybrid LAN — Encrypted TCP Stream (Direct LAN พอร์ต `52000–52010`) + Local Relay Service (พอร์ต `52100`); เตรียมขยาย WebRTC ใน V2
- **Database:** SQLite (สำหรับ Pilot V1) → PostgreSQL (ระยะขยาย) + Redis (เมื่อขยายหลาย Instance)
- **CI/CD:** GitHub Actions (`backend-ci.yml`, `agent-ci.yml`, `dashboard-ci.yml`)

---

## 2. แผนผังโฟลเดอร์สำคัญ (Folder Structure) และหน้าที่

```text
d:\remote desktop\
├─ .antigravity/
│  └─ memory.md                          # ไฟล์ความจำบริบทโปรเจกต์
├─ docs/                                 # เอกสารสเปก คู่มือ และ ADR (0001-0003)
├─ src/
│  ├─ backend/
│  │  ├─ RemoteDesk.Domain/              # Entities & Business Rules (ไม่พึ่งพา Infra/UI)
│  │  ├─ RemoteDesk.Contracts/           # Shared DTOs, Interfaces, Protocol Contracts
│  │  ├─ RemoteDesk.Application/         # Use Cases, Session Coordinator, Orchestration
│  │  ├─ RemoteDesk.Infrastructure/      # SQLite/PostgreSQL, EF Core, Logging, Config
│  │  ├─ RemoteDesk.Api/                 # REST API, Authentication, RBAC
│  │  ├─ RemoteDesk.Signaling/           # WebSocket Presence, Negotiation, Session Ticket
│  │  └─ RemoteDesk.Relay/               # Encrypted Stream Relay Server (Port 52100)
│  ├─ transports/
│  │  ├─ RemoteDesk.Transport.Abstractions/ # IRemoteTransport Contract กลาง
│  │  ├─ RemoteDesk.Transport.DirectLan/    # Direct LAN Encrypted TCP Implementation
│  │  └─ RemoteDesk.Transport.LocalRelay/   # Local Relay Transport Implementation
│  ├─ agent/
│  │  ├─ RemoteDesk.Agent.Contracts/     # IPC & Agent Contracts
│  │  ├─ RemoteDesk.Agent.Service/       # Windows Service (Identity, Heartbeat, Policy)
│  │  └─ RemoteDesk.Agent.Interactive/   # User Session Worker (Screen Capture, Input)
│  ├─ operator/
│  │  └─ RemoteDesk.Operator.Windows/    # โปรแกรมเจ้าหน้าที่สำหรับดูและควบคุมหน้าจอ
│  └─ dashboard/
│     └─ remotedesk-dashboard/           # Next.js Web UI สำหรับจัดการองค์กร/เครื่อง/สิทธิ์
├─ tests/                                # unit/, integration/, e2e/lan-two-pc/ (T01-T12)
├─ deploy/                               # สคริปต์ติดตั้ง local-server, windows-agent, firewall
└─ tools/                                # diagnostics และ test-stream-generator
```

---

## 3. ตำแหน่งไฟล์สำคัญ (Key File Paths)

### ไฟล์ที่มีอยู่ปัจจุบัน
- **กฎการทำงานหลักของ AI:** `.antigravityrules`
- **ไฟล์ตั้งค่า Ignore:** `.antigravityignore`
- **ไฟล์ความจำบริบทโปรเจกต์:** `.antigravity/memory.md`
- **เอกสารสถาปัตยกรรมหลัก:** `docs/LAN_First_Remote_Desktop_Architecture_V1.md`
- **แผนงานและโรดแมพ (Phase 0–6):** `docs/ROADMAP_AND_PLAN.md`
- **สมุดเช็กสถานะงานรายวัน:** `docs/PROGRESS_TRACKER.md`

### ตำแหน่งไฟล์ระบบหลักตามสถาปัตยกรรม (Target Paths เมื่อขึ้นโครง Phase 0+)
- **Build & Package Config:** `Directory.Build.props`, `Directory.Packages.props`, `global.json`
- **Database & Schema (10 ตารางหลัก):** `src/backend/RemoteDesk.Infrastructure/` (SQLite/EF Core สำหรับ `organizations`, `sites`, `users`, `roles`, `devices`, `device_endpoints`, `device_permissions`, `sessions`, `audit_logs`)
- **Authentication, RBAC & MFA:** `src/backend/RemoteDesk.Api/` และ `src/backend/RemoteDesk.Application/`
- **API Routes & Signaling:** `src/backend/RemoteDesk.Api/` (REST) และ `src/backend/RemoteDesk.Signaling/` (WebSocket)
- **Session State & Concurrency (Max 5 Sessions):** Atomic Session Coordinator ใน `src/backend/RemoteDesk.Application/` + Database Transaction/Lease
- **Transport Abstraction:** `src/transports/RemoteDesk.Transport.Abstractions/IRemoteTransport.cs`
- **Frontend State & API Client:** `src/dashboard/remotedesk-dashboard/src/` (ห้ามต่อ DB ตรง ต้องผ่าน `RemoteDesk.Api` เท่านั้น)

---

## 4. รูปแบบการเขียนโค้ด (Coding Patterns & Architectural Rules)

1. **Clean Architecture & Strict Dependency Direction:**
   - `Domain` ห้ามอ้างอิง `Infrastructure` หรือ `UI`
   - `Application` อ้างอิงได้เฉพาะ `Domain` และ `Contracts`
   - `Dashboard` เรียกใช้ `RemoteDesk.Api` เท่านั้น ห้ามเชื่อม Database โดยตรง
2. **Transport Plug-in Pattern (`IRemoteTransport`):**
   - แยก Remote Desktop Core ออกจากระบบเครือข่าย ทุกช่องทาง (`DirectLan`, `LocalRelay`, และ `WebRtc` ใน V2) ต้อง Implement `IRemoteTransport`
   - ลำดับการเชื่อมต่อ: ขอ Session จาก API → จอง Slot → ลอง Direct LAN → Fallback ไป Local Relay อัตโนมัติหากเชื่อมต่อตรงไม่ได้
3. **Process Isolation สำหรับ Windows Agent:**
   - แยก `Agent.Service` (รันใน Session 0 ดูแล Device Identity/Heartbeat) ออกจาก `Agent.Interactive` (รันใน User Session ดูแล Screen Capture & Mouse/Keyboard Input) และคุยกันผ่าน IPC ที่จำกัดสิทธิ์
4. **Server-Side Atomic State & Security:**
   - จำกัด `MAX_CONCURRENT_SESSIONS = 5` แบบ Atomic ฝั่ง Backend พร้อมระบบ Lease/Heartbeat Timeout และ `Idempotency Key` (ห้ามนับจำนวนจากฝั่ง Client/Dashboard)
   - ใช้ `Device ID (UUID)` เป็นตัวตนเครื่อง (ไม่ใช้ IP), บังคับตรวจ Organization Scope + Permission ทุก API, ใช้ Short-lived Session Ticket และต้องแสดงสถานะการควบคุมบนเครื่องปลายทางเสมอ (ห้ามทำ Hidden Mode)

