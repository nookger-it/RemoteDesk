# RemoteDesk Platform — LAN-First Remote Desktop (V1)

ระบบ Remote Desktop แบบ **LAN-First (Hybrid: Direct LAN + Local Server Relay)** ออกแบบด้วยสถาปัตยกรรม Monorepo และ Clean Architecture เพื่อรองรับการขยายสู่ Internet Remote (WebRTC) ใน V2 โดยไม่ต้องรื้อโครงสร้างหลัก

## โครงสร้างโปรเจกต์ (Monorepo Structure)

```text
remote-desktop-platform/
├─ docs/                 # เอกสารสถาปัตยกรรม, โรดแมพ, สมุดติดตามงาน (PROGRESS_TRACKER.md) และ ADR
├─ src/
│  ├─ backend/           # ASP.NET Core API, Domain, Application, Infrastructure, Signaling, Relay
│  ├─ transports/        # IRemoteTransport Abstractions, DirectLan, LocalRelay
│  ├─ agent/             # Windows Agent (Agent.Service + Agent.Interactive + Agent.Contracts)
│  ├─ operator/          # Windows Operator Client (WPF)
│  └─ dashboard/         # Web Dashboard (Next.js + TypeScript)
├─ tests/                # Unit, Integration, และ E2E Tests (T01–T12)
├─ deploy/               # สคริปต์ติดตั้ง Local Server, Windows Agent และ Firewall
└─ tools/                # เครื่องมือทดสอบและวินิจฉัยระบบ
```

## ข้อกำหนดสภาพแวดล้อม (Prerequisites)

- **.NET SDK:** 10.0 LTS (`10.0.302` หรือใหม่กว่าบนสาย 10.0)
- **Node.js:** v22 LTS + npm 10+
- **OS สำหรับ Agent & Operator Client:** Windows 10 / 11 หรือ Windows Server

## คำสั่งเริ่มต้น (Quick Start)

### 1. Build ฝั่ง .NET (Backend, Transports, Agent, Operator, Tests)
```powershell
dotnet build RemoteDesk.sln
dotnet test RemoteDesk.sln
```

### 2. Build ฝั่ง Web Dashboard
```powershell
cd src/dashboard/remotedesk-dashboard
npm run build
```

## เอกสารสำคัญ
- [สถาปัตยกรรมระบบ V1](./docs/LAN_First_Remote_Desktop_Architecture_V1.md)
- [แผนการดำเนินงานและโรดแมพ](./docs/ROADMAP_AND_PLAN.md)
- [สมุดบันทึกความคืบหน้าโครงการ (Progress Tracker)](./docs/PROGRESS_TRACKER.md)

