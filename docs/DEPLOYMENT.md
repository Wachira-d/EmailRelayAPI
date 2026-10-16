# การติดตั้งและตั้งค่า

## 1. ความต้องการ

- .NET 8 SDK (build) / ASP.NET Core 8 Runtime หรือ Hosting Bundle (IIS)
- ฐานข้อมูล: SQLite (ไม่ต้องติดตั้งอะไรเพิ่ม) หรือ SQL Server 2016+
- เครือข่ายไปยัง SMTP/provider ที่ใช้ และ AuthenAPI

## 2. Build & publish

```bash
cd EmailRelayAPI
dotnet restore
dotnet build -c Release
dotnet publish -c Release -o ../publish
```

## 3. การตั้งค่า (`appsettings.json` / `appsettings.Production.json` / environment variables)

ค่าทุกตัว override ได้ด้วย environment variable รูปแบบ `Section__Key` เช่น `AuthenApi__ApiKey`, `Database__ConnectionString`

| Section | Key สำคัญ | คำอธิบาย |
|---|---|---|
| `Database` | `Provider` = `Sqlite` / `SqlServer`, `ConnectionString` | SQLite: `Data Source=data/emailrelay.db` · SQL Server: `Server=...;Database=EmailRelay;Trusted_Connection=True;TrustServerCertificate=True` (สร้าง database เปล่าไว้ก่อน ตารางถูกสร้างอัตโนมัติ) |
| `DataProtection` | `KeyPath` | โฟลเดอร์ key ring สำหรับเข้ารหัส secret – ต้องสำรองและคงเดิมเมื่อย้ายเครื่อง |
| `Security` | `ApiKeyHeaderName`, `EnableIpAllowList`, `AllowedIpAddresses`, `KnownProxies`, `GlobalRateLimitPerMinute`, `MaxRequestBodyBytes`, `RequireHttps`, `ApiKeyCacheSeconds` | ดู SECURITY.md |
| `ApiKeys` | `KeyPrefix`, `RotationCheckIntervalMinutes` | นโยบาย rotation เป็นค่าต่อ client |
| `Anomaly` | thresholds, `AutoBlock`, `BlockDurationMinutes`, `RejectDuplicates` | ดู SECURITY.md |
| `Delivery` | `WorkerCount`, `MaxAttempts`, `RetryBaseDelaySeconds`, `RetryMaxDelaySeconds`, `PollIntervalSeconds`, `AttachmentStoragePath`, `MaxAttachmentBytes`, `MaxRecipientsPerMessage`, `MaxBatchSize`, `BlockedAttachmentExtensions`, `ProfileUnhealthyThreshold` | พฤติกรรมคิวส่ง |
| `Retention` | `MessageRetentionDays` (90), `AuditRetentionDays` (365), `RequestLogRetentionDays` (30), `PurgeTime` | purge อัตโนมัติ (เวลาเครื่อง) |
| `Audit` | `EnableFileLogging`, `LogDirectory`, `EnableRequestLogging` | ไฟล์ `logs/audit-yyyyMMdd.jsonl` |
| `Alerts` | `Enabled`, `AdminEmails[]`, `SenderProfileName`, `WebhookUrl`, `ThrottleMinutes` | ใช้ sender profile ที่มีอยู่ส่งอีเมลแจ้งเตือน |
| `AuthenApi` | `BaseUrl` (เช่น `https://authen.company.local/api`), `ApiKey`, `CacheDurationDays`, `AllowOfflineLogin`, `AdminGroups[]`, `ViewerGroups[]`, `MaxFailedAttempts`, `LockoutMinutes`, `SessionTimeoutMinutes`, `LocalAdmin` | login console |
| `Logging` | ระดับ log ของ .NET | |

### ตัวอย่าง `appsettings.Production.json`

```json
{
  "Database": { "Provider": "SqlServer", "ConnectionString": "Server=sql01;Database=EmailRelay;Trusted_Connection=True;TrustServerCertificate=True" },
  "Security": { "RequireHttps": true, "EnableIpAllowList": true, "AllowedIpAddresses": [ "10.0.0.0/8" ], "KnownProxies": [ "10.1.1.10" ] },
  "AuthenApi": {
    "BaseUrl": "https://authen.company.local/api",
    "ApiKey": "<AuthenAPI key>",
    "AdminGroups": [ "IT-EmailRelay-Admins" ],
    "ViewerGroups": [ "IT-Helpdesk" ],
    "LocalAdmin": { "Enabled": false }
  },
  "Alerts": { "Enabled": true, "AdminEmails": [ "it-ops@company.com" ], "SenderProfileName": "Exchange-Internal" }
}
```

### สร้างรหัสผ่าน local admin

```bash
dotnet run -- hash-password "StrongPassword!"
# → pbkdf2$100000$...$...   นำไปใส่ AuthenApi:LocalAdmin:PasswordHash และตั้ง Enabled=true
```

## 4. IIS (Windows)

1. ติดตั้ง **ASP.NET Core Hosting Bundle 8**
2. สร้าง Application Pool (.NET CLR = *No Managed Code*, Identity = service account เฉพาะ) และ Website/Application ชี้ไปที่โฟลเดอร์ publish
3. ให้สิทธิ์ *Modify* แก่ identity ของ app pool บนโฟลเดอร์ `data/` และ `logs/` (ภายในโฟลเดอร์ publish หรือกำหนด path เต็มใน config)
4. ตั้งค่า `web.config` (สร้างให้อัตโนมัติตอน publish) — ถ้าต้องการให้ตั้ง `ASPNETCORE_ENVIRONMENT=Production` ใน `<environmentVariables>`
5. Binding HTTPS ด้วยใบรับรององค์กร; ถ้ามี reverse proxy หน้า IIS ให้ตั้ง `Security:KnownProxies`
6. **Preload/Always running**: ตั้ง app pool `Start Mode = AlwaysRunning`, `Idle Time-out = 0` และเปิด *Application Initialization* เพื่อให้ dispatcher ทำงานตลอด (คิวส่งทำงานใน process ของเว็บ)
7. เปิด `https://host/admin/` login ด้วยบัญชี AD แล้วสร้าง sender profile/client

แนวทางเดียวกับ AuthenAPI `docs/IIS_DEPLOYMENT_GUIDE.md`

## 5. Linux (systemd)

```ini
[Unit]
Description=Email Relay API
After=network.target

[Service]
WorkingDirectory=/opt/emailrelay
ExecStart=/usr/bin/dotnet /opt/emailrelay/EmailRelayAPI.dll
Restart=always
User=emailrelay
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
Environment=AuthenApi__ApiKey=<key>

[Install]
WantedBy=multi-user.target
```

วาง nginx/Apache เป็น TLS terminator และตั้ง `Security:KnownProxies: ["127.0.0.1"]`

## 6. ตรวจสอบหลังติดตั้ง

- `GET /health` → `healthy`; `GET /health/ready` → `ready` (DB เชื่อมต่อได้)
- Console → **Security** → *Authentication backend* ต้องแสดง AuthenAPI reachable
- Console → **Sender Profiles** → *Test connection* / *Send test email*
- Console → **System** → ตรวจ path ไฟล์แนบ, retention, worker

## 7. Backup / การย้ายเครื่อง

สำรอง (1) ฐานข้อมูล (ไฟล์ `data/emailrelay.db` + `-wal` หรือ SQL Server backup) (2) `data/keys` (Data Protection key ring) (3) `data/attachments` ถ้าต้องการเก็บไฟล์แนบ (4) `appsettings*.json` — ถ้าไม่มี key ring เดิม รหัสผ่าน SMTP/API key ของ provider จะต้องใส่ใหม่ใน console

## 8. การปรับขนาด

- เพิ่ม `Delivery:WorkerCount` และ `MaxConcurrency` ของ profile ตามความสามารถของ SMTP/provider
- SQLite รองรับได้หลายหมื่นข้อความ/วัน; ปริมาณสูงกว่านั้นให้ใช้ SQL Server
- หลาย instance: ใช้ SQL Server ร่วมกัน + แชร์ `DataProtection:KeyPath` (network share) — counter rate limit/anomaly จะนับแยกต่อ instance (ดู ARCHITECTURE.md ข้อ 8)
