🔄 ขั้นตอนการทำงานของระบบ (End-to-End Workflow)
ผู้ใช้ทำรายการ (User Action): ผู้ใช้กดเลือกที่นั่ง, ล็อค (Lock), หรือชำระเงิน (Paid) ผ่านหน้าเว็บ (React)

บันทึกข้อมูล (Write to DB): C# Backend (TicketController) รับคำสั่งและทำ Database Transaction เพื่ออัปเดตตาราง seats และบันทึกประวัติลงตาราง outbox_events (แนบ Timestamp และ UUID กันข้อมูลซ้ำ)

สูบข้อมูล (CDC): Sequin ตรวจพบแถวใหม่ในตาราง outbox_events จึงดูดข้อมูลออกมาแล้วยิงเข้า Stream ของ NATS อย่างปลอดภัย

ประมวลผล (Worker Processing): Go Worker ดึงข้อมูลจาก NATS Stream มาตรวจสอบลำดับเวลา (Timestamp) และป้องกันการเขียนชนกัน (CAS Revision) จากนั้นอัปเดตสถานะล่าสุดลง NATS KV Store

อัปเดตแคช (In-Memory Projection): C# NatsWatcherService ดักจับการเปลี่ยนแปลงแบบ Real-time จาก NATS KV แล้วนำมาเขียนอัปเดตทับใน IMemoryCache (RAM) ทันที เพื่อให้พร้อมตอบกลับเมื่อมีคนรีเฟรชหน้าเว็บ

รวมกลุ่ม (Event Batching): C# หน่วงเวลารอ 0.2 วินาที เพื่อรวบรวม Event ที่นั่งที่ถูกกดพร้อมๆ กัน มัดรวมเป็นก้อนเดียว (Array)

กระจายข้อมูล (Broadcasting): C# ส่งข้อมูลก้อนนั้นผ่าน SignalR (WebSocket) กลับไปหาผู้ใช้ทุกคนที่ดูรอบฉายเดียวกัน

แสดงผล (UI Update): หน้าเว็บ React รับ Array ของเก้าอี้ไปอัปเดต State ทำให้สีของเก้าอี้เปลี่ยนพร้อมกันอย่างลื่นไหลและไม่กระตุก
