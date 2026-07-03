using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory; // 🌟 เพิ่มสำหรับการใช้งาน IMemoryCache
using Npgsql;
using System.Text.Json;
using TicketBookingPoc.Models; 

namespace TicketBookingPoc.Controllers
{
    [ApiController]
    [Route("api/ticket")]
    public class TicketController : ControllerBase
    {
        private readonly NpgsqlConnection _db;
        private readonly IMemoryCache _cache; // 🌟 ฉีด IMemoryCache เข้ามาใช้งาน
        private readonly ILogger<TicketController> _logger;

        public TicketController(NpgsqlConnection db, IMemoryCache cache, ILogger<TicketController> logger)
        {
            _db = db;
            _cache = cache; // 🌟 กำหนดค่าเริ่มต้น
            _logger = logger;
        }

        // ==========================================
        // ขา QUERY: ดึงข้อมูลสาขา (ใช้โค้ดเดิม)
        // ==========================================
        [HttpGet("branches")]
        public async Task<IActionResult> GetBranches()
        {
            var branches = new List<Branch>();
            await _db.OpenAsync();
            try
            {
                using var cmd = new NpgsqlCommand("SELECT id, name FROM branches", _db);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    branches.Add(new Branch { Id = reader.GetInt32(0), Name = reader.GetString(1) });
                }
                return Ok(branches);
            }
            finally
            {
                await _db.CloseAsync();
            }
        }

        // ==========================================
        // ขา QUERY: ดึงข้อมูลรอบฉายตามสาขา (ใช้โค้ดเดิม)
        // ==========================================
        [HttpGet("showtimes/{branchId}")]
        public async Task<IActionResult> GetShowtimes(int branchId)
        {
            var showtimes = new List<Showtime>();
            await _db.OpenAsync();
            try
            {
                string sql = @"
                    SELECT s.id, c.id, c.name, s.movie_title, s.show_time 
                    FROM showtimes s
                    JOIN cinemas c ON s.cinema_id = c.id
                    WHERE c.branch_id = @branchId
                    ORDER BY s.show_time ASC";

                using var cmd = new NpgsqlCommand(sql, _db);
                cmd.Parameters.AddWithValue("branchId", branchId);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    showtimes.Add(new Showtime
                    {
                        Id = reader.GetInt32(0),
                        CinemaId = reader.GetInt32(1),
                        CinemaName = reader.GetString(2),
                        MovieTitle = reader.GetString(3),
                        ShowTime = reader.GetDateTime(4)
                    });
                }
                return Ok(showtimes);
            }
            finally
            {
                await _db.CloseAsync();
            }
        }

        // ==========================================
        // ขา COMMAND: รับคำสั่งจอง/ยกเลิก/จ่ายเงิน
        // ==========================================
        [HttpPost("seats/update-status")]
        public async Task<IActionResult> UpdateStatus([FromBody] UpdateSeatsRequest req)
        {
            if (req.SeatCodes == null || !req.SeatCodes.Any())
            {
                return BadRequest(new { message = "กรุณาระบุที่นั่ง" });
            }
            var responseLogs = new List<object>();
            void AddLog(string msg) => responseLogs.Add(new { timestamp = DateTime.Now.ToString("HH:mm:ss"), message = msg });

            AddLog($"📥 Backend ได้รับคำสั่งเปลี่ยนสถานะเป็น '{req.Status}' สำหรับที่นั่ง {string.Join(", ", req.SeatCodes)} (รอบฉาย {req.ShowtimeId})");

            await _db.OpenAsync();
            
            using var tx = await _db.BeginTransactionAsync();
            try
            {
                foreach (var seatCode in req.SeatCodes)
                {
                    string condition = "";
                    if (req.Status == "Lock") condition = "AND status = ''"; 
                    else if (req.Status == "Paid") condition = "AND status = 'Lock'"; 
                    else if (req.Status == "") condition = "AND status = 'Lock'"; 

                    string updateSql = $@"
                        UPDATE seats 
                        SET status = @status, 
                            payment_time = {(req.Status == "Paid" ? "NOW()" : "NULL")}
                        WHERE showtime_id = @showtimeId 
                        AND seat_code = @seatCode 
                        {condition}";

                    using var updateCmd = new NpgsqlCommand(updateSql, _db, tx);
                    updateCmd.Parameters.AddWithValue("status", req.Status);
                    updateCmd.Parameters.AddWithValue("showtimeId", req.ShowtimeId);
                    updateCmd.Parameters.AddWithValue("seatCode", seatCode);

                    var rowsAffected = await updateCmd.ExecuteNonQueryAsync();

                    if (rowsAffected == 0)
                    {
                        await tx.RollbackAsync(); 
                        AddLog($"❌ การจองล้มเหลว: ที่นั่ง {seatCode} ไม่พร้อมทำรายการ (อาจถูกผู้อื่นทำรายการไปแล้ว)");
                        _logger.LogWarning($"Concurrency hit: Seat {seatCode} for showtime {req.ShowtimeId} could not be updated.");
                        return BadRequest(new { logs = responseLogs });
                    }

                    var payloadObj = new
                    {
                        showtime_id = req.ShowtimeId,
                        seat_code = seatCode,
                        status = req.Status,
                        timestamp = DateTime.UtcNow.Ticks
                    };
                    string payloadJson = JsonSerializer.Serialize(payloadObj);
                    
                    // 🌟 ปรับ Key เป็น Dot Notation เพื่อให้สอดคล้องกับ NATS และ Watcher
                    string aggregateId = $"showtime.{req.ShowtimeId}.seat.{seatCode}-{Guid.NewGuid()}";

                    string outboxSql = @"
    INSERT INTO outbox_events (aggregate_type, aggregate_id, event_type, payload) 
    VALUES (@aggType, @aggId, @evType, @payload::jsonb)";

                    using var outboxCmd = new NpgsqlCommand(outboxSql, _db, tx);
                    outboxCmd.Parameters.AddWithValue("aggType", "seat");
                    outboxCmd.Parameters.AddWithValue("aggId", aggregateId);
                    outboxCmd.Parameters.AddWithValue("evType", "SeatStatusUpdated");
                    outboxCmd.Parameters.AddWithValue("payload", payloadJson);

                    await outboxCmd.ExecuteNonQueryAsync();
                    
                    AddLog($"📝 บันทึกข้อมูลที่นั่ง {seatCode} ลงตารางหลักและ Outbox สำเร็จ");
                }

                await tx.CommitAsync();
                AddLog("✅ Transaction Commit สำเร็จ! รอ Sequin ดึงข้อมูลเข้า NATS...");

                return Ok(new { 
                    message = "อัปเดตสถานะสำเร็จ", 
                    logs = responseLogs 
                });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                _logger.LogError($"Database Error: {ex.Message}");
                AddLog($"🔴 ระบบฐานข้อมูลขัดข้อง: {ex.Message}");
                return StatusCode(500, new { logs = responseLogs });
            }
            finally
            {
                await _db.CloseAsync();
            }
        }

        // ==========================================
        // 🌟 ขา QUERY: ดึงผังที่นั่ง (ปรับเป็น Cache-Aside Pattern)
        // ==========================================
        [HttpGet("seats/{showtimeId}")]
        public async Task<IActionResult> GetSeats(int showtimeId)
        {
            var responseLogs = new List<object>();
            void AddLog(string msg) => responseLogs.Add(new { timestamp = DateTime.Now.ToString("HH:mm:ss"), message = msg });

            string cacheKey = $"seatplan:{showtimeId}";

            // 🌟 1. พยายามดึงข้อมูลจาก IMemoryCache ก่อน (Fast Path)
            if (_cache.TryGetValue(cacheKey, out List<SeatDto>? cachedSeats) && cachedSeats != null)
            {
                AddLog($"⚡ โหลดข้อมูลผังที่นั่งจาก RAM (Cache Hit) จำนวน {cachedSeats.Count} ที่นั่ง");
                
                return Ok(new {
                    data = new { seats = cachedSeats },
                    source = "MemoryCache",
                    logs = responseLogs
                });
            }

            // 🌟 2. ถ้าใน RAM ไม่มีข้อมูล (Cache Miss) ถึงจะไปดึงจาก Database (Slow Path)
            var dbSeats = new List<SeatDto>();
            await _db.OpenAsync();
            try
            {
                string sql = "SELECT seat_code, status FROM seats WHERE showtime_id = @showtimeId";
                using var cmd = new NpgsqlCommand(sql, _db);
                cmd.Parameters.AddWithValue("showtimeId", showtimeId);
                using var reader = await cmd.ExecuteReaderAsync();
                
                while (await reader.ReadAsync())
                {
                    dbSeats.Add(new SeatDto {
                        seatCode = reader.GetString(0),
                        status = reader.GetString(1)
                    });
                }
                AddLog($"🐢 ไม่พบข้อมูลใน RAM ไปดึงข้อมูลผังตั้งต้นจาก Database จำนวน {dbSeats.Count} ที่นั่ง");

                // 🌟 3. นำข้อมูลที่ได้จาก DB ไปเก็บลง IMemoryCache โดยตั้งเวลาหมดอายุไว้
                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetSlidingExpiration(TimeSpan.FromMinutes(30)); // ถ้าไม่มีคนดึงข้อมูลรอบฉายนี้นานเกิน 30 นาที ให้เตะออกจาก RAM
                
                _cache.Set(cacheKey, dbSeats, cacheOptions);

                return Ok(new {
                    data = new { seats = dbSeats },
                    source = "PostgreSQL (Initial & Cached)",
                    logs = responseLogs
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading seats: {ex.Message}");
                AddLog($"🔴 เกิดข้อผิดพลาดในการโหลดข้อมูล: {ex.Message}");
                return StatusCode(500, new { logs = responseLogs });
            }
            finally
            {
                await _db.CloseAsync();
            }
        }
    }
}