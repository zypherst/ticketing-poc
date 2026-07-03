using TicketBookingPoc.Models; 
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory; // 🌟 เพิ่มสำหรับ IMemoryCache
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using System.Text.Json;
using System.Threading.Channels; // 🌟 เพิ่มสำหรับทำ Batching

namespace TicketBookingPoc.Services
{
    // คลาสสำหรับรับส่งข้อมูลใน Channel
    public class SeatUpdateEvent
    {
        public string ShowtimeId { get; set; } = string.Empty;
        public string SeatCode { get; set; } = string.Empty;
        public string SeatDataJson { get; set; } = string.Empty;
    }

    public class NatsWatcherService : BackgroundService
    {
        private readonly NatsConnection _nats; 
        private readonly IHubContext<TicketHub> _hubContext;
        private readonly IMemoryCache _cache;
        private readonly ILogger<NatsWatcherService> _logger;
        
        // 🌟 ท่อ (Channel) สำหรับพักข้อมูลก่อนทำ Batching
        private readonly Channel<SeatUpdateEvent> _channel;

        public NatsWatcherService(
            NatsConnection nats, 
            IHubContext<TicketHub> hubContext, 
            IMemoryCache cache, 
            ILogger<NatsWatcherService> logger)
        {
            _nats = nats;
            _hubContext = hubContext;
            _cache = cache;
            _logger = logger;
            // สร้าง Channel แบบ Unbounded (รับข้อมูลได้เรื่อยๆ)
            _channel = Channel.CreateUnbounded<SeatUpdateEvent>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 Starting NATS KV Watcher & Batch Processor...");
            
            // 🌟 แยก Task สำหรับส่ง SignalR แบบ Batching ออกไปทำงานคู่ขนาน
            _ = Task.Run(() => ProcessBatchesAsync(stoppingToken), stoppingToken);

            try
            {
                var js = new NatsJSContext(_nats);
                var kvContext = new NatsKVContext(js);
                var kv = await kvContext.GetStoreAsync("seatKV", cancellationToken: stoppingToken);
                
                // 🌟 1. ใช้ Wildcard ดักฟังเฉพาะ Key ย่อยของที่นั่งทั้งหมด
                var watcher = kv.WatchAsync<byte[]>("showtime.*.seat.*", cancellationToken: stoppingToken);

                await foreach (var entry in watcher)
                {
                    if (entry.Value != null)
                    {
                        var seatDataJson = System.Text.Encoding.UTF8.GetString(entry.Value);
                        
                        // 🌟 2. แกะ Key แบบ Dot Notation (เช่น showtime.1.seat.A1)
                        string[] keyParts = entry.Key.Split('.');
                        if (keyParts.Length == 4) 
                        {
                            string showtimeId = keyParts[1];
                            string seatCode = keyParts[3];

                            // 🌟 3. อัปเดตข้อมูลลง IMemoryCache (RAM) ทันที เพื่อให้ API ขา Read พร้อมใช้งาน
                            UpdateMemoryCache(showtimeId, seatCode, seatDataJson);

                            // 🌟 4. โยน Event ลงตะกร้า (Channel) เพื่อรอรวมร่างก่อนยิง SignalR
                            await _channel.Writer.WriteAsync(new SeatUpdateEvent 
                            { 
                                ShowtimeId = showtimeId, 
                                SeatCode = seatCode, 
                                SeatDataJson = seatDataJson 
                            }, stoppingToken);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("🛑 NATS KV Watcher stopping.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error in NATS KV Watcher: {ex.Message}");
            }
        }

        // ==========================================
        // ฟังก์ชันอัปเดต RAM (IMemoryCache)
        // ==========================================
        private void UpdateMemoryCache(string showtimeId, string seatCode, string json)
        {
            string cacheKey = $"seatplan:{showtimeId}";
            
            // ดึงผังที่นั่งจาก RAM ออกมา (สมมติว่า Controller เก็บเป็น List<SeatDto>)
            if (_cache.TryGetValue(cacheKey, out List<SeatDto>? seats) && seats != null)
            {
                try
                {
                    var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var updatedSeat = JsonSerializer.Deserialize<SeatDto>(json, jsonOptions);
                    
                    lock (seats) 
                    {
                        var target = seats.FirstOrDefault(s => s.seatCode == seatCode);
                        if (target != null && updatedSeat != null)
                        {
                            target.status = updatedSeat.status; 
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"⚠️ Cache Update Error: {ex.Message}");
                }
            }
        }

        // ==========================================
        // ฟังก์ชันรวบรวม Event (Batching) 0.2 วินาที
        // ==========================================
        private async Task ProcessBatchesAsync(CancellationToken token)
        {
            var batch = new List<SeatUpdateEvent>();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    // รอจนกว่าจะมี Event แรกเข้ามา (จุดนี้จะหยุดรอ ไม่กิน CPU)
                    var firstEvent = await _channel.Reader.ReadAsync(token);
                    batch.Add(firstEvent);

                    // 🌟 สร้าง Token สำหรับจับเวลา 200 ms (0.2 วินาที)
                    using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    batchCts.CancelAfter(200);

                    try
                    {
                        // กวาดข้อมูลทั้งหมดที่เข้ามาภายใน 0.2 วินาที
                        while (await _channel.Reader.WaitToReadAsync(batchCts.Token))
                        {
                            while (_channel.Reader.TryRead(out var nextEvent))
                            {
                                batch.Add(nextEvent);
                            }
                        }
                    }
                    catch (OperationCanceledException) 
                    {
                        // หมดเวลา 0.2 วินาที (หมดรอบ Batching) หลุดออกจาก Try เพื่อไปส่งข้อมูล
                    }

                    // 🌟 จัดกลุ่มตามรอบฉาย และส่ง SignalR ครั้งเดียวต่อ 1 กลุ่ม
                    var groupedByShowtime = batch.GroupBy(x => x.ShowtimeId);
                    foreach (var group in groupedByShowtime)
                    {
                        var showtimeId = group.Key;
                        // ส่ง Array ของ JSON กลับไปให้ Frontend (Frontend ต้องปรับให้รับ Array ด้วย)
                        var updates = group.Select(g => JsonSerializer.Deserialize<object>(g.SeatDataJson)).ToList();
                        
                        await _hubContext.Clients.Group(showtimeId)
                            .SendAsync("SeatsBatchUpdated", updates, token);
                        
                        _logger.LogInformation($"🚀 [SignalR] Sent {updates.Count} updates to showtime {showtimeId} in one go!");
                    }

                    batch.Clear(); // ล้างตะกร้าเตรียมรับรอบถัดไป
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}