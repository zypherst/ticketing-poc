🔄 ขั้นตอนการทำงานของระบบ (End-to-End Workflow)
ผู้ใช้ทำรายการ (User Action): ผู้ใช้กดเลือกที่นั่ง, ล็อค (Lock), หรือชำระเงิน (Paid) ผ่านหน้าเว็บ (React)

บันทึกข้อมูล (Write to DB): C# Backend (TicketController) รับคำสั่งและทำ Database Transaction เพื่ออัปเดตตาราง seats และบันทึกประวัติลงตาราง outbox_events (แนบ Timestamp และ UUID กันข้อมูลซ้ำ)

สูบข้อมูล (CDC): Sequin ตรวจพบแถวใหม่ในตาราง outbox_events จึงดูดข้อมูลออกมาแล้วยิงเข้า Stream ของ NATS อย่างปลอดภัย

ประมวลผล (Worker Processing): Go Worker ดึงข้อมูลจาก NATS Stream มาตรวจสอบลำดับเวลา (Timestamp) และป้องกันการเขียนชนกัน (CAS Revision) จากนั้นอัปเดตสถานะล่าสุดลง NATS KV Store

อัปเดตแคช (In-Memory Projection): C# NatsWatcherService ดักจับการเปลี่ยนแปลงแบบ Real-time จาก NATS KV แล้วนำมาเขียนอัปเดตทับใน IMemoryCache (RAM) ทันที เพื่อให้พร้อมตอบกลับเมื่อมีคนรีเฟรชหน้าเว็บ

รวมกลุ่ม (Event Batching): C# หน่วงเวลารอ 0.2 วินาที เพื่อรวบรวม Event ที่นั่งที่ถูกกดพร้อมๆ กัน มัดรวมเป็นก้อนเดียว (Array)

กระจายข้อมูล (Broadcasting): C# ส่งข้อมูลก้อนนั้นผ่าน SignalR (WebSocket) กลับไปหาผู้ใช้ทุกคนที่ดูรอบฉายเดียวกัน

แสดงผล (UI Update): หน้าเว็บ React รับ Array ของเก้าอี้ไปอัปเดต State ทำให้สีของเก้าอี้เปลี่ยนพร้อมกันอย่างลื่นไหลและไม่กระตุก



postgres-master		PostgreSQL 17.6		ฐานข้อมูลหลัก (Master): เป็น Node เดียวที่รับคำสั่งเขียน (Write) และอ่าน (Read) เก็บข้อมูลหลักและบันทึก Outbox Events  
postgres-replica-1	PostgreSQL 17.6		ฐานข้อมูลสำรอง (Replica): คอยซิงค์ข้อมูลจาก Master และเปิดรับเฉพาะคำสั่งอ่าน (Read-only) เพื่อช่วยกระจายโหลดดึงข้อมูลผังที่นั่ง  
postgres-replica-2	PostgreSQL 17.6		ฐานข้อมูลสำรอง (Replica): คอยซิงค์ข้อมูลจาก Master และเปิดรับเฉพาะคำสั่งอ่าน (Read-only) เพื่อช่วยกระจายโหลดดึงข้อมูลผังที่นั่ง  
pgpool-1		Pgpool-II		Database Connection Manager: จัดการ Connection Pool, ตรวจสอบสุขภาพ DB และช่วยกระจายโหลด (Load Balance) ไปยังโหนด DB ต่างๆ  
pgpool-2		Pgpool-II		Database Connection Manager: จัดการ Connection Pool, ตรวจสอบสุขภาพ DB และช่วยกระจายโหลด (Load Balance) ไปยังโหนด DB ต่างๆ  
haproxy-db		HAProxyProxy		สำหรับ Database: ทำหน้าที่เป็น Load Balancer คอยกระจายทราฟฟิกขาเข้าให้วิ่งไปหา pgpool-1 และ pgpool-2  
sequin-redis		Redis 7			หน่วยความจำสำหรับ CDC: เป็นตัวช่วยจำสถานะและคิวการทำงานของ Sequin (มี Volume เก็บข้อมูลถาวร) เพื่อให้ทำงานต่อได้ถ้าระบบรีสตาร์ท  
sequin			Sequin			Change Data Capture (CDC): คอยอ่านข้อมูลตาราง outbox จาก postgres-master แล้วส่งต่อเข้าสู่ระบบคิว (NATS)  
nats-1			NATS JetStream 2.10	Message Broker & KV Cluster: คลัสเตอร์จัดการคิวและเก็บสถานะเก้าอี้ (Key-Value) แบบ High Availability กระจายข้อมูลถึงกันทั้งหมด  
nats-2			NATS JetStream 2.10	Message Broker & KV Cluster: คลัสเตอร์จัดการคิวและเก็บสถานะเก้าอี้ (Key-Value) แบบ High Availability กระจายข้อมูลถึงกันทั้งหมด  
nats-3			NATS JetStream 2.10	Message Broker & KV Cluster: คลัสเตอร์จัดการคิวและเก็บสถานะเก้าอี้ (Key-Value) แบบ High Availability กระจายข้อมูลถึงกันทั้งหมด  
haproxy-nats		HAProxyProxy 		สำหรับ NATS: กระจายทราฟฟิกแบบ TCP ให้กับ NATS Cluster ทำให้ Client มองเห็น NATS เป็นจุดเชื่อมต่อเดียว  
nats-setup		NATS CLI		ตัวตั้งค่าเริ่มต้น (Init Container): สคริปต์ทำงานครั้งเดียวตอนระบบเริ่ม เพื่อสร้าง KV Bucket (seatKV), Stream (SEAT_STREAM) และ Consumer (seat_worker)  
worker-1		Golang			กลุ่มกรรมกร (Go Workers): ดึง Event จาก NATS มาคัดกรองข้อมูล ป้องกันการทำงานซ้ำซ้อน แล้วบันทึกสถานะล่าสุดกลับลง NATS KV
worker-2		Golang			กลุ่มกรรมกร (Go Workers): ดึง Event จาก NATS มาคัดกรองข้อมูล ป้องกันการทำงานซ้ำซ้อน แล้วบันทึกสถานะล่าสุดกลับลง NATS KV
worker-3		Golang			กลุ่มกรรมกร (Go Workers): ดึง Event จาก NATS มาคัดกรองข้อมูล ป้องกันการทำงานซ้ำซ้อน แล้วบันทึกสถานะล่าสุดกลับลง NATS KV  
backend-1		C# / ASP.NET		กลุ่ม API และ SignalR: รับคำสั่งจากหน้าเว็บ เชื่อมต่อ DB ผ่าน haproxy-db และคอยดักฟัง NATS KV เพื่อส่ง SignalR อัปเดตผังที่นั่ง
backend-2		C# / ASP.NET		กลุ่ม API และ SignalR: รับคำสั่งจากหน้าเว็บ เชื่อมต่อ DB ผ่าน haproxy-db และคอยดักฟัง NATS KV เพื่อส่ง SignalR อัปเดตผังที่นั่ง  
haproxy-backend		HAProxy			Proxy สำหรับ Backend: กระจายโหลด (Load Balance) ทราฟฟิก HTTP/WebSocket จาก Frontend ให้วิ่งกระจายไปที่ backend-1 และ backend-2  
frontend-1		React			กลุ่ม Web UI: เซิร์ฟเวอร์แสดงผลหน้าเว็บ ให้ผู้ใช้กดจองที่นั่งและรับข้อมูล Real-time 
frontend-2		React			กลุ่ม Web UI: เซิร์ฟเวอร์แสดงผลหน้าเว็บ ให้ผู้ใช้กดจองที่นั่งและรับข้อมูล Real-time  
haproxy-frontend	HAProxyProxy 		สำหรับ Frontend: ประตูด่านแรกสุดที่รับ Request จากผู้ใช้ภายนอก (พอร์ต 80) แล้วกระจายโหลดไปที่ frontend-1 และ frontend-2  
