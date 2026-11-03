# ADR 0003: การแยก Windows Agent เป็น `Agent.Service` และ `Agent.Interactive`

- **สถานะ:** Accepted
- **วันที่:** 2026-11-03

## บริบท (Context)
บนระบบปฏิบัติการ Windows โปรแกรมที่รันแบบ Windows Service จะทำงานอยู่ใน **Session 0 (Session 0 Isolation)** ซึ่งไม่มีสิทธิ์เข้าถึง Desktop ของผู้ใช้ที่ล็อกอินอยู่โดยตรง และไม่ควรทำการจับภาพหน้าจอหรือจำลองการกดเมาส์/คีย์บอร์ดจากภายใน Session 0

## การตัดสินใจ (Decision)
แยกโครงสร้างของ Windows Agent ออกเป็น 2 โปรเซสที่สื่อสารกันผ่าน IPC ที่จำกัดสิทธิ์:
1. **`RemoteDesk.Agent.Service` (Windows Service ใน Session 0):**
   - เริ่มทำงานอัตโนมัติพร้อม Windows
   - ดูแล Device Identity (UUID + Key), ลงทะเบียนเครื่อง และส่ง Heartbeat
   - ตรวจสอบคำสั่งและดูแลการเปิด/ปิด `Agent.Interactive` อย่างปลอดภัย
2. **`RemoteDesk.Agent.Interactive` (User Session Worker):**
   - ทำงานใน Windows User Session ของผู้ใช้ปัจจุบัน
   - ทำหน้าที่จับภาพหน้าจอ (Screen Capture) และรับคำสั่ง Mouse / Keyboard (`SendInput`)
   - แสดงแถบสถานะการถูกควบคุม (Remote Session Banner) และปุ่มกดตัดการเชื่อมต่อ (Disconnect) ให้ผู้ใช้ปลายทางเห็นชัดเจนเสมอ

## ผลที่ตามมา (Consequences)
- **ข้อดี:** ถูกต้องตามหลักความปลอดภัยของ Windows OS, แยกส่วนการทำงานชัดเจน และผู้ใช้ปลายทางรับรู้สถานะการควบคุมเสมอ
- **ข้อควรระวัง:** ต้องจัดการช่องทาง IPC และตรวจสอบสิทธิ์ของ Session ก่อนส่งคำสั่งควบคุมทุกครั้ง

