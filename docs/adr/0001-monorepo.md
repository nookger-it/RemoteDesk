# ADR 0001: โครงสร้าง Monorepo สำหรับ RemoteDesk Platform

- **สถานะ:** Accepted
- **วันที่:** 2026-11-03

## บริบท (Context)
ระบบ Remote Desktop ประกอบด้วยหลายส่วนที่ต้องทำงานสอดคล้องกันอย่างใกล้ชิด ได้แก่ Backend API, Signaling, Local Relay, Windows Agent (`Agent.Service` + `Agent.Interactive`), Windows Operator Client, Transport Layers และ Web Dashboard หากแยก Repository ตั้งแต่วันแรก การแก้ไข Shared Contracts หรือ Protocol จะทำให้เกิดความซับซ้อนในการซิงค์เวอร์ชันระหว่างโปรเจกต์

## การตัดสินใจ (Decision)
ใช้โครงสร้าง **Monorepo** เก็บทุกส่วนไว้ใน Repository เดียวกัน โดยแบ่งโฟลเดอร์หลักเป็น:
- `src/backend/`
- `src/transports/`
- `src/agent/`
- `src/operator/`
- `src/dashboard/`
- `tests/`

พร้อมบังคับใช้กฎทิศทางการอ้างอิง (Clean Architecture Dependency Rules) ผ่าน Project References อย่างเคร่งครัด

## ผลที่ตามมา (Consequences)
- **ข้อดี:** แก้ไข DTO, Protocol Contracts และ Transport Interfaces พร้อมกันได้ใน Pull Request เดียว และทดสอบ Integration ครบวงจรได้ทันที
- **ข้อควรระวัง:** ต้องแยก CI Workflows ตามส่วนที่เปลี่ยนแปลง (`backend-ci.yml`, `agent-ci.yml`, `dashboard-ci.yml`) เพื่อไม่ให้ Build ช้าเกินความจำเป็น

