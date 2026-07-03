package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"os"
	"strings"
	"time"

	"github.com/nats-io/nats.go"
	"github.com/nats-io/nats.go/jetstream"
)

type SequinEnvelope struct {
	Record struct {
		Payload SeatData `json:"payload"`
	} `json:"record"`
}

type SeatData struct {
	ShowtimeID int    `json:"showtime_id"`
	SeatCode   string `json:"seat_code"`
	Status     string `json:"status"`
	Timestamp  int64  `json:"timestamp"` // รับ Ticks แบบเต็มเม็ดเต็มหน่วย
}

func main() {
	natsURL := os.Getenv("NATS_URL")
	if natsURL == "" {
		natsURL = "nats://haproxy-nats:4222"
	}

	nc, err := nats.Connect(natsURL, nats.MaxReconnects(-1))
	if err != nil {
		log.Fatalf("❌ Error connecting to NATS: %v", err)
	}
	defer nc.Close()

	js, err := jetstream.New(nc)
	if err != nil {
		log.Fatalf("❌ Error creating JetStream context: %v", err)
	}

	ctx := context.Background()

	kv, err := js.KeyValue(ctx, "seatKV")
	if err != nil {
		log.Fatalf("❌ Error binding to KV store: %v", err)
	}

	cons, err := js.Consumer(ctx, "SEAT_STREAM", "seat_worker")
	if err != nil {
		log.Fatalf("❌ Error getting consumer: %v", err)
	}

	log.Println("🚀 Worker started. Listening for seat events...")

	// 🌟 1. เปลี่ยนมาใช้ Consume (Callback) แก้ปัญหา iter.Next() ค้างเวลา NATS กระพริบ
	cc, err := cons.Consume(func(msg jetstream.Msg) {
		var envelope SequinEnvelope
		if err := json.Unmarshal(msg.Data(), &envelope); err != nil {
			msg.Term()
			return
		}

		seat := envelope.Record.Payload
		if seat.ShowtimeID == 0 || seat.SeatCode == "" {
			msg.Term()
			return
		}

		cleanKey := fmt.Sprintf("showtime.%d.seat.%s", seat.ShowtimeID, strings.TrimSpace(seat.SeatCode))

		// 🌟 2. ใส่ Timeout (3 วินาที) ป้องกัน Worker ค้างเวลา KV Store โหลดไม่เสร็จ
		kvCtx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cancel()

		entry, err := kv.Get(kvCtx, cleanKey)
		var lastRev uint64 = 0
		var shouldIgnore bool = false

		if err == nil && entry != nil {
			lastRev = entry.Revision()
			
			// 🌟 3. อ่านกลับเป็น Struct (SeatData) ตรงๆ เพื่อป้องกัน Float64 Precision Loss
			var existingData SeatData 
			if err := json.Unmarshal(entry.Value(), &existingData); err == nil {
				if seat.Timestamp < existingData.Timestamp {
					shouldIgnore = true
				}
			}
		} else if err != jetstream.ErrKeyNotFound {
			// ถ้าระบบ KV พังหรือ Timeout ให้ NAK โยนกลับคิวแล้วลองใหม่
			log.Printf("⚠️ KV System Error (รอคลัสเตอร์ฟื้นตัว): %v | Key: %s", err, cleanKey)
			msg.NakWithDelay(2 * time.Second)
			return
		}

		if shouldIgnore {
			log.Printf("⏩ ข้าม Event เก่า | Key: %s | สถานะ: %s", cleanKey, seat.Status)
			msg.Ack()
			return
		}

		// 4. แปลงกลับเพื่อใช้กับ Frontend C# (คง seat_id ไว้)
		kvData, _ := json.Marshal(map[string]interface{}{
			"showtime_id": seat.ShowtimeID,
			"seat_id":     seat.SeatCode, 
			"status":      seat.Status,
			"timestamp":   seat.Timestamp,
		})

		if lastRev == 0 {
			_, err = kv.Create(kvCtx, cleanKey, kvData)
		} else {
			_, err = kv.Update(kvCtx, cleanKey, kvData, lastRev)
		}

		if err != nil {
			log.Printf("⚠️ Race Condition ถูกตีตก! โยนกลับเข้าคิว | Key: %s", cleanKey)
			msg.NakWithDelay(100 * time.Millisecond)
			return
		}

		log.Printf("✅ อัปเดต KV สำเร็จ | Key: %s | Status: %s", cleanKey, seat.Status)
		msg.Ack()
	})

	if err != nil {
		log.Fatalf("❌ Error starting consumer: %v", err)
	}
	defer cc.Stop()

	// ทำให้โปรแกรมรันค้างไว้ไม่ปิดตัวเอง
	select {}
}