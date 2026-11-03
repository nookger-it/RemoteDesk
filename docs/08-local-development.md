# คู่มือการพัฒนาในเครื่อง (Local Development Guide)

## 1. โครงสร้างการพึ่งพาระหว่างโปรเจกต์ (Clean Architecture Dependency Rules)

- `RemoteDesk.Domain`: เก็บ Entities หลัก 10 ตารางและ Business Constants (`MaxConcurrentSessions = 5`) ไม่พึ่งพาไลบรารีภายนอก
- `RemoteDesk.Contracts`: เก็บ DTOs และสัญญาข้อมูลระหว่างโมดูล
- `RemoteDesk.Application`: เก็บ Use Case Interfaces (`IRemoteDeskManagementService`) และ `SessionTicketService` (HMAC-SHA256)
- `RemoteDesk.Infrastructure`: เก็บ `RemoteDeskDbContext` (SQLite) และ `RemoteDeskManagementService`
- `RemoteDesk.Transport.Abstractions`: เก็บ `IRemoteTransport` และ `HybridLanTransportOrchestrator` (Direct LAN → Local Relay Fallback)
- `RemoteDesk.Transport.DirectLan`: เก็บ `DirectLanTransport` และ `DirectLanStreamHost` (TCP + TLS `SslStream`)
- `RemoteDesk.Transport.LocalRelay`: เก็บ `LocalRelayServer`, `LocalRelayTransport`, และ `LocalRelayAgentSessionBridge`

## 2. คำสั่งที่ใช้บ่อยในการพัฒนา

### Build และรันชุดทดสอบทั้งหมด (.NET 10 LTS)
```powershell
dotnet build RemoteDesk.sln
dotnet test RemoteDesk.sln --verbosity normal
```

### Build และตรวจสอบ Type ของ Web Dashboard (Next.js + TypeScript)
```powershell
cd src/dashboard/remotedesk-dashboard
npm run build
```

## 3. บัญชีทดสอบเริ่มต้น (Seeded Pilot Accounts)

เมื่อ `RemoteDesk.Api` เริ่มทำงานครั้งแรก ระบบจะสร้างข้อมูลเริ่มต้นให้อัตโนมัติใน SQLite:
- **Organization ID:** `11111111-1111-1111-1111-111111111111` (`RemoteDesk Pilot Organization`)
- **HQ Site ID:** `22222222-2222-2222-2222-222222222221` (`Headquarters LAN (HQ)`)
- **Operator Admin Account:**
  - User ID: `33333333-3333-3333-3333-333333333331`
  - Email: `operator@remotedesk.local`
  - Password: `PilotPassword!2026`
  - MFA Code: `123456`
- **Restricted Operator Account (ต้องได้รับ `DevicePermission` ก่อนเข้าเครื่อง):**
  - User ID: `33333333-3333-3333-3333-333333333332`
  - Email: `restricted@remotedesk.local`
  - Password: `PilotPassword!2026`

