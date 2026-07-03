# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# คัดลอกไฟล์ csproj มา Restore ก่อนเพื่อใช้ประโยชน์จาก Docker Cache
COPY ["TicketBookingPoc.csproj", "./"]
RUN dotnet restore "./TicketBookingPoc.csproj"

# คัดลอกโค้ดทั้งหมดแล้ว Build
COPY . .
RUN dotnet publish "TicketBookingPoc.csproj" -c Release -o /app/publish

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Expose Port (สมมติว่ารันที่พอร์ต 5000 ตามที่ HAProxy คาดหวัง)
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

ENTRYPOINT ["dotnet", "TicketBookingPoc.dll"]

