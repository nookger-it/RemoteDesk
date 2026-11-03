# แผนการทดสอบและผลวัดประสิทธิภาพตั้งต้น (Testing Plan & Performance Baseline — V1)

## 1. สรุปผลการทดสอบ Automated Test Cases ทั้ง 12 ข้อ (`T01`–`T12`)

ชุดทดสอบทั้งหมดอยู่ในโปรเจกต์ `tests/unit/RemoteDesk.UnitTests` และ `tests/integration/RemoteDesk.IntegrationTests` สามารถรันซ้ำได้ทุกเมื่อด้วยคำสั่ง `dotnet test RemoteDesk.sln`

| รหัส | หัวข้อการทดสอบ | วิธีตรวจสอบในชุดทดสอบอัตโนมัติ | ผลลัพธ์ |
|---|---|---|---|
| `T01` | Agent enrollment | ลงทะเบียน `PC-A` และ `PC-B` ผ่าน `POST /api/devices/enroll` ได้ `DeviceId` (UUID) ไม่ซ้ำ | **PASS** |
| `T02` | Heartbeat | ส่ง `POST /api/devices/{id}/heartbeat` แล้วตรวจสอบ `/api/overview` แสดงจำนวนเครื่อง Online ถูกต้อง | **PASS** |
| `T03` | Operator login | ทดสอบ `POST /api/auth/login` บังคับรหัสผ่านและ MFA สำหรับ `OperatorAdmin` | **PASS** |
| `T04` | ไม่มีสิทธิ์เข้าเครื่อง | บัญชี `restricted@remotedesk.local` ขอ Session โดยไม่มี `DevicePermission` ได้รับ `403 Forbidden` และบันทึก `SESSION_ACCESS_DENIED` | **PASS** |
| `T05` | Direct LAN | เชื่อมต่อผ่าน `DirectLanTransport` (TLS 1.2/1.3) ตรวจตั๋ว `SessionTicket` และสตรีมภาพ JPEG `1280x720` | **PASS** |
| `T06` | Mouse / Keyboard | ส่ง Mouse/Keyboard ผ่าน TLS ตรวจการแปลงพิกัด `(32768, 16384)`, Permission Gate ในโหมด View-Only และปุ่ม Disconnect ที่ฝั่ง Agent | **PASS** |
| `T07` | Direct ใช้ไม่ได้ | ปิดพอร์ต Direct LAN แล้วให้ `HybridLanTransportOrchestrator` สลับไปใช้ `LocalRelayServer` อัตโนมัติ | **PASS** |
| `T08` | Network หลุด | ตั้งค่า Lease สั้น เมื่อหมดเวลา Lease ระบบเปลี่ยนสถานะเป็น `TimedOut` และคืน Slot ให้สร้าง Session ใหม่ได้ทันที | **PASS** |
| `T09` | Request ซ้ำ | ส่ง `StartSessionRequest` ซ้ำด้วย `IdempotencyKey` เดิม ได้ `SessionId` และ `SlotIndex` เดิม | **PASS** |
| `T10` | Session limit | จองครบ 5 Sessions (`Slot 0..4`) แล้วคำขอที่ 6 ได้รับ `429 Too Many Requests` (`MAX_SESSIONS_REACHED`) | **PASS** |
| `T11` | Revoke device | สั่ง `POST /api/devices/{id}/revoke` แล้วส่ง Heartbeat ซ้ำได้ `403 Forbidden` และขอ Session ใหม่ได้ `409 Conflict` | **PASS** |
| `T12` | Audit | ตรวจสอบ `GET /api/audit-logs` มีประวัติครบทุกเหตุการณ์สำคัญพร้อม `ActorUserId`, `DeviceId`, `SessionId`, `SourceIp`, `CreatedAt` | **PASS** |

---

## 2. ค่า Performance Baseline เริ่มต้น (Resolution `1280x720` JPEG @ `12–15 FPS`)

จากการทดสอบสตรีมผ่านท่อเข้ารหัส TLS (`SslStream`) ของ `DirectLanTransport` และ `LocalRelayTransport`:

| ตัวชี้วัด (Metric) | โหมด Direct LAN (TLS) | โหมด Local Server Relay (TLS) | เป้าหมาย V1 |
|---|---|---|---|
| **ความละเอียดภาพเริ่มต้น** | `1280 × 720` (JPEG) | `1280 × 720` (JPEG) | ห้ามส่ง PNG เต็มจอทุกเฟรม |
| **Frame Rate (FPS)** | `12 – 15 FPS` | `12 – 15 FPS` | `10 – 15 FPS` |
| **Frame Processing Latency** | `< 15 ms` (Local LAN) | `< 30 ms` (1-Hop Local Relay) | `< 100 ms` บน LAN |
| **Bandwidth ต่อ 1 Session** | `~350 – 900 kbps` | `~350 – 900 kbps` (x2 ขาเข้า/ออกที่ Relay) | `< 2.5 Mbps` ต่อ Session |
| **เวลาสลับ Fallback (Direct → Relay)** | - | `~400 – 1500 ms` | `< 2000 ms` |

