# EmailRelayAPI

ระบบ **Email Relay** สำหรับองค์กร: รับคำขอส่งอีเมลผ่าน REST API แล้วส่งต่อผ่าน **SMTP** หรือ **Mail API** (SendGrid, Mailgun, Microsoft Graph หรือ endpoint ของคุณเอง) ตามที่ตั้งค่าไว้ต่อผู้ใช้งาน (client) พร้อมระบบความปลอดภัยหลายชั้น, log/audit ละเอียด, ตรวจจับการใช้งานผิดปกติ และ Admin Console สำหรับดูรายงาน

- **ASP.NET Core 8** (C#), EF Core (SQLite เริ่มต้น / SQL Server), MailKit
- Login ผู้ดูแลผ่าน **AuthenAPI** (Active Directory) แบบ *hybrid cache* ตามรูปแบบ [Wachira-d/AuthenAPI](https://github.com/Wachira-d/AuthenAPI)
- Admin console (SPA) ที่ `/admin/` · Swagger ที่ `/swagger`

> เอกสารเพิ่มเติม: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) · [docs/API_GUIDE.md](docs/API_GUIDE.md) · [docs/SECURITY.md](docs/SECURITY.md) · [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)

---

## ความสามารถหลัก

| ด้าน | รายละเอียด |
|---|---|
| **รับ-ส่งอีเมลผ่าน API** | `POST /api/v1/emails` (เดี่ยว) และ `/batch` (สูงสุด 100 ฉบับ/ครั้ง), ไฟล์แนบ (base64), inline image (cid), custom `X-*` headers, tag, ตั้งเวลาส่ง (`scheduleAt`), priority, idempotency key, template + variables |
| **Sender Profile** | กำหนดช่องทางส่งได้หลายแบบ: SMTP (STARTTLS / SSL / Auto), SendGrid, Mailgun, Microsoft Graph (OAuth client credentials), Generic JSON endpoint · ทดสอบการเชื่อมต่อ/ส่งทดสอบจากหน้า console · จำกัด concurrency และ msg/min ต่อ profile · health tracking |
| **หลายผู้ใช้งาน (Client)** | แต่ละ client ผูกกับ sender profile ของตัวเอง (A → SMTP, B → API), มี API key ได้หลายดอก (scope `send`, `read`, `admin`), rate limit/นาที, โควต้า/วัน, จำกัดจำนวนผู้รับ, จำกัด From, จำกัดโดเมนผู้รับ, จำกัด IP ต้นทาง, webhook แจ้งสถานะ |
| **คิวส่ง & retry** | worker หลายตัว, exponential back-off + jitter, กู้คืนข้อความค้างหลัง restart, ยกเลิก/ส่งซ้ำจาก console, timeline ของทุกข้อความ |
| **ความปลอดภัย** | API key แบบ hash (SHA-256, แสดงครั้งเดียว), HMAC request signing (เลือกเปิดต่อ client), IP allow list (global + ต่อ client), block list อัตโนมัติ/manual, rate limit 2 ชั้น, security headers, เข้ารหัส secret ที่เก็บด้วย Data Protection, CSRF guard สำหรับ console |
| **Key rotation** | ปิดเป็นค่าเริ่มต้น (key ไม่หมดอายุ) · client ที่ต้องการเปิด rotation ได้ (ทุก N วัน + grace period) · rotate ได้เองผ่าน `POST /api/v1/me/keys/rotate` หรือจาก console โดย key เก่ายังใช้ได้ช่วง grace |
| **ตรวจจับการใช้งานผิดปกติ** | ยิงซ้ำ (burst), auth ล้มเหลวซ้ำ (brute force), ส่งข้อความเดิมซ้ำ ๆ (duplicate storm), ผู้รับพุ่งผิดปกติ, อัตรา 4xx สูง, IP ใหม่ที่ไม่เคยเห็น → บันทึกเหตุการณ์ + บล็อกอัตโนมัติ + แจ้งเตือน (email/webhook) |
| **Log & รายงาน** | Request log ทุก call, Audit trail (auth/security/admin/delivery/system), ไฟล์ JSONL รายวัน, dashboard (กราฟรายชั่วโมง/รายวัน, top clients, profile health), รายงานการใช้งานตามช่วงเวลา/ต่อ client/ต่อ profile, export CSV, retention/purge อัตโนมัติ |
| **Login ผู้ดูแล** | AuthenAPI (AD/LDAPS) + local credential cache (PBKDF2) ทำให้ login ซ้ำไม่ต้องไป AD และยัง login ได้เมื่อ AD ล่ม, lockout, map AD group → role Admin/Viewer, break-glass local admin |

---

## เริ่มต้นใช้งาน (Development)

```bash
cd EmailRelayAPI
dotnet restore
dotnet run
```

- เปิด `http://localhost:5258/admin/` (ค่าใน `Properties/launchSettings.json`)
- โหมด Development เปิด **local admin** ไว้ใน `appsettings.Development.json`: user `admin` / password `Admin@123` (เปลี่ยนก่อนใช้จริง: `dotnet run -- hash-password "รหัสใหม่"` แล้วนำค่าไปใส่ `AuthenApi:LocalAdmin:PasswordHash`)
- ฐานข้อมูล SQLite และไฟล์แนบจะถูกสร้างใน `EmailRelayAPI/data/` อัตโนมัติ

### ขั้นตอนตั้งค่าครั้งแรก

1. **Sender Profiles** → สร้าง profile เช่น `Exchange-Internal` (SMTP) หรือ `SendGrid-Marketing` (API) → กด *Test connection*
2. **Clients & Keys** → สร้าง client (เลือก default sender profile, กำหนด rate limit/quota/IP ที่อนุญาต) → *New key* → คัดลอก key (แสดงครั้งเดียว)
3. ทดสอบส่ง:

```bash
curl -X POST http://localhost:5258/api/v1/emails \
  -H "X-API-Key: erk_xxxxxxxx" -H "Content-Type: application/json" \
  -d '{"to":["someone@example.com"],"subject":"Hello","html":"<p>Hi</p>","idempotencyKey":"order-1001"}'
```

4. ติดตามสถานะที่ `GET /api/v1/emails/{id}` หรือดูที่หน้า **Messages** ใน console

---

## โครงสร้างโปรเจกต์

```
EmailRelayAPI/
├─ Program.cs                 # DI, auth, pipeline, Swagger
├─ Options/                   # การตั้งค่า (Security, Anomaly, Delivery, Retention, AuthenApi, Alerts, ApiKeys, Audit, Database)
├─ Data/                      # EF Core DbContext + entities (Clients, ApiKeys, SenderProfiles, Messages, Attachments, Events, AuditLogs, RequestLogs, BlockEntries, AdminUsers, Templates ...)
├─ Security/                  # API key auth handler, HMAC signature, IP utils, rate limit, anomaly detector, block list, secret protection, password hashing
├─ Middleware/                # SecurityHeaders, IpFilter (block list + global limit), ClientRateLimit, RequestLogging
├─ Services/                  # EmailQueueService (validate/accept), EmailDispatcherService (workers/retry), Transports (SMTP, HTTP API), AuditService, AlertService, WebhookNotifier, RetentionService, KeyRotationService, AuthenApiClient, AdminAuthService, StatsService, TemplateRenderer
├─ Controllers/               # Public API: EmailsController, MeController
├─ Controllers/Admin/         # Console API: auth, clients, profiles, messages, logs, security, templates, reports/system
├─ wwwroot/admin/             # Admin console (vanilla JS + Bootstrap + Chart.js)
└─ appsettings.json           # ค่าเริ่มต้นทั้งหมด (ดู docs/DEPLOYMENT.md)
docs/                         # สถาปัตยกรรม, คู่มือ API, ความปลอดภัย, การติดตั้ง
```

## สถานะ

โค้ดชุดนี้ถูกออกแบบและเขียนครบทั้งระบบ แต่ยังไม่ได้ build/run ในสภาพแวดล้อมที่สร้างโค้ด (ไม่มี .NET SDK และเข้าถึง NuGet ไม่ได้) — โปรด `dotnet build` ครั้งแรกบนเครื่องของคุณและแจ้ง error ที่พบ (ถ้ามี) เพื่อแก้ไข รายละเอียดอยู่ท้าย `docs/ARCHITECTURE.md`
