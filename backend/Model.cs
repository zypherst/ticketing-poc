using System.Text.Json.Serialization;

namespace TicketBookingPoc.Models
{
    public class Branch { public int Id { get; set; } public string Name { get; set; } = string.Empty; }
    
    public class Showtime 
    { 
        public int Id { get; set; } 
        public int CinemaId { get; set; } 
        public string CinemaName { get; set; } = string.Empty; 
        public string MovieTitle { get; set; } = string.Empty; 
        public DateTime ShowTime { get; set; } 
    }

    public class Seat 
    { 
        public string SeatCode { get; set; } = string.Empty; 
        public string Status { get; set; } = string.Empty; 
        public DateTime? PaymentTime { get; set; }
    }

    public class SeatPlanDto
    {
        public int ShowtimeId { get; set; }
        public List<Seat> Seats { get; set; } = new();
    }

    public class SeatDto
    {
public string seatCode { get; set; } = string.Empty;
        public string status { get; set; } = string.Empty;
    }

    public class UpdateSeatsRequest
    {
        public int ShowtimeId { get; set; }
        public List<string> SeatCodes { get; set; } = new();
        public string Status { get; set; } = string.Empty;
    }

    public class ActionLog
    {
        public string Timestamp { get; set; } = DateTime.Now.ToString("HH:mm:ss");
        public string Message { get; set; } = string.Empty;
    }

    public class ApiResponse<T>
    {
        public string Source { get; set; } = string.Empty;
        public T Data { get; set; } = default!;
        public List<ActionLog> Logs { get; set; } = new();
    }

    public class SeatEventPayload
    {
        [JsonPropertyName("showtime_id")]
        public int ShowtimeId { get; set; }

        [JsonPropertyName("seat_code")]
        public string SeatCode { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;
    }
}