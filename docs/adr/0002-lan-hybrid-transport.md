# ADR 0002: สถาปัตยกรรม Hybrid LAN Transport (Direct LAN + Local Relay) และ `IRemoteTransport`

- **สถานะ:** Accepted
- **วันที่:** 2026-11-03

## บริบท (Context)
ในสภาพแวดล้อมองค์กรแบบ LAN เครื่องของเจ้าหน้าที่ (Operator) และเครื่องปลายทาง (Agent) อาจอยู่ใน Subnet เดียวกันที่เชื่อมต่อถึงกันได้โดยตรง หรืออาจอยู่คนละ VLAN/Subnet ที่มี Firewall กั้น นอกจากนี้ในอนาคต (V2) ระบบต้องขยายไปรองรับการควบคุมผ่านอินเทอร์เน็ต (WebRTC / TURN) โดยไม่รื้อระบบจัดการ Session หลัก

## การตัดสินใจ (Decision)
1. กำหนด `IRemoteTransport` ใน `RemoteDesk.Transport.Abstractions` เป็นสัญญาการเชื่อมต่อกลาง
2. ใน V1 รองรับโหมด **Hybrid LAN**:
   - **Mode A (Direct LAN):** เชื่อมต่อตรงผ่าน Encrypted TCP Stream (พอร์ต `52000–52010`) เพื่อลดภาระของ Server และได้ Latency ต่ำที่สุด
   - **Mode B (Local Server Relay):** ส่งต่อ Encrypted Stream ผ่าน `RemoteDesk.Relay` (พอร์ต `52100`) เมื่อ Direct LAN เชื่อมต่อไม่ได้
3. ทุกการเชื่อมต่อต้องขอ `Short-lived Session Ticket` จาก API ก่อนเสมอ ลอง Direct LAN ก่อน และ Fallback ไปยัง Local Relay อัตโนมัติ

## ผลที่ตามมา (Consequences)
- **ข้อดี:** ได้ทั้งความเร็วสูงสุดในวง LAN เดียวกันและความเสถียรเมื่อข้าม Subnet รวมถึงพร้อมเพิ่ม `WebRtcTransport` ใน V2 โดยไม่ต้องแก้ Session Core
- **ข้อควรระวัง:** ทั้ง Direct LAN และ Relay ต้องใช้มาตรฐาน Authentication, Authorization และ Encryption เดียวกันทั้งหมด

