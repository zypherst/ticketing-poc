// Controllers/TicketHub.cs
using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

public class TicketHub : Hub
{
    // เมื่อ Frontend เปิดหน้ารอบฉาย จะเรียกใช้ฟังก์ชันนี้
    public async Task JoinShowtimeGroup(string showtimeId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, showtimeId);
    }

    // เมื่อ Frontend กดย้อนกลับ หรือเปลี่ยนหน้ารอบฉาย
    public async Task LeaveShowtimeGroup(string showtimeId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, showtimeId);
    }
}