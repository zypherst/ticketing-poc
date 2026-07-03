// Program.cs
using TicketBookingPoc.Services;
using NATS.Client.Core;
using Npgsql;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection; // 🌟 เผื่อเรียกใช้ Services เพิ่มเติม

var builder = WebApplication.CreateBuilder(args);

// 1. Command Side: เชื่อมต่อ PostgreSQL ผ่าน haproxy-db
var dbConnectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? "Host=haproxy-db;Port=9999;Database=ticketdb;Username=postgres;Password=postgres;";
builder.Services.AddScoped((sp) => new NpgsqlConnection(dbConnectionString));

// 2. Query Side: เชื่อมต่อ NATS ผ่าน haproxy-nats (ไม่ต้องพึ่ง Dapr)
var natsUrl = builder.Configuration["NATS_URL"] ?? "nats://haproxy-nats:4222";
builder.Services.AddSingleton<NatsConnection>(sp => 
{
    var opts = NatsOpts.Default with { Url = natsUrl };
    return new NatsConnection(opts);
});

builder.Services.AddControllers();
builder.Services.AddSignalR(); 

// 🌟 3. เปิดใช้งาน IMemoryCache เพื่อให้ Backend เก็บผังที่นั่งไว้ใน RAM ได้
builder.Services.AddMemoryCache(); 

// 4. ลงทะเบียน Watcher ที่จะคอยอัปเดตข้อมูลใน RAM และส่ง SignalR
builder.Services.AddHostedService<NatsWatcherService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", builder =>
        builder.SetIsOriginAllowed(_ => true)
               .AllowAnyMethod()
               .AllowAnyHeader()
               .AllowCredentials());
});

var app = builder.Build();

app.UseCors("AllowAll");
app.UseRouting();
app.MapControllers();
app.MapHub<TicketHub>("/ticketHub"); 

app.Run();