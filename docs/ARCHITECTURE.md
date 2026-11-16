# สถาปัตยกรรมระบบ EmailRelayAPI

## 1. ภาพรวม

```
                ┌──────────────────────────────────────────────────────────────────┐
  Client A ──►  │  Security pipeline                                              │
  (X-API-Key)   │  ForwardedHeaders → SecurityHeaders → IpFilter(block list,       │
  Client B ──►  │  global IP limit, burst detect) → RequestLogging → ApiKey auth   │
                │  (hash lookup + cache, client IP allow list, HMAC signature)     │
  Admin UI ──►  │  → ClientRateLimit (per-client, quota) → Authorization           │
  (cookie)      └───────────────┬──────────────────────────────────────────────────┘
                                │
                 ┌──────────────▼───────────────┐      ┌────────────────────────┐
                 │ EmailQueueService (accept)   │      │ Admin API + Console    │
                 │ validate · template · quota  │      │ clients/keys/profiles  │
                 │ duplicate storm · attachments│      │ logs/security/reports  │
                 │ persist (Queued) → enqueue   │      └────────────────────────┘
                 └──────────────┬───────────────┘
                                │ DeliveryQueue (Channel<Guid>) + DB poll (recovery / scheduled / retries)
                 ┌──────────────▼───────────────┐
                 │ EmailDispatcherService       │  N workers · per-profile concurrency & throttle
                 │ retry w/ exponential backoff │  → events timeline · profile health · webhooks
                 └───────┬──────────────┬───────┘
                         │              │
               ┌─────────▼───┐   ┌──────▼─────────────────────────┐
               │ SMTP        │   │ HTTP API                       │
               │ (MailKit)   │   │ SendGrid · Mailgun · MS Graph  │
               └─────────────┘   │ Generic JSON endpoint          │
                                 └────────────────────────────────┘
   Background: LogWriterService (batch audit/request logs → DB + JSONL) · RetentionService (purge)
               KeyRotationService (opt-in policy) · BlockListService (refresh) · AlertService
```

หลักการออกแบบ

- **Accept fast, deliver async** – API ตอบ `202 Accepted` ทันทีหลัง validate/persist; การส่งจริงทำโดย worker พร้อม retry → ผู้เรียกไม่ต้องรอ SMTP และระบบทนต่อ provider ล่มชั่วคราว
- **ทุกอย่างมีร่องรอย** – ทุก request → `RequestLogs`, ทุกเหตุการณ์สำคัญ → `AuditLogs` (+ไฟล์ JSONL), ทุกข้อความมี timeline (`EmailEvents`)
- **Fail closed** – key ไม่ถูกต้อง/IP ไม่อนุญาต/ลายเซ็นผิด → ปฏิเสธ + นับเป็น auth failure → บล็อกอัตโนมัติเมื่อเกิน threshold
- **Secret ไม่เก็บเป็น plain text** – API key เก็บเฉพาะ SHA-256, รหัสผ่าน SMTP/API key ของ provider/signing secret เข้ารหัสด้วย ASP.NET Data Protection, รหัสผ่านผู้ดูแลเก็บเป็น PBKDF2
- **ตั้งค่าได้ทุกชั้น** – threshold/limit ทั้งหมดอยู่ใน `appsettings.json` (hot-reload ผ่าน `IOptionsMonitor` สำหรับส่วนใหญ่) และค่าต่อ client/profile อยู่ใน DB แก้ได้จาก console

## 2. Data model (EF Core)

| ตาราง | หน้าที่ | หมายเหตุ |
|---|---|---|
| `Clients` | ผู้ใช้งาน API หนึ่งราย (ระบบ/ทีม) | default sender profile, limits, allow lists, signing/webhook secret (encrypted), rotation policy |
| `ApiKeys` | key ของ client | `Prefix` (12 ตัวแรก, unique, ใช้ค้นหา), `KeyHash` (SHA-256), scopes, expiry/revoke/rotation, usage stats |
| `SenderProfiles` | ช่องทางส่ง | `Type` = Smtp / HttpApi, การตั้งค่า SMTP หรือ provider, throttle, health (last success/failure, consecutive failures) |
| `Messages` | ข้อความ + สถานะการส่ง | `Status` Queued→Sending→Sent/Failed (หรือ Scheduled/Cancelled), `NextAttemptAt`, `ContentHash`, `IdempotencyKey` (unique ต่อ client), provider id/response |
| `Attachments` | metadata ไฟล์แนบ | ไฟล์จริงอยู่ใน `Delivery:AttachmentStoragePath/yyyyMM/{id}/` |
| `EmailEvents` | timeline ต่อข้อความ | Queued, Attempt/Retry, Sent, Failed, Cancelled, Requeued, Recovered, WebhookSent/Failed |
| `AuditLogs` | audit trail | Category (Authentication, ApiKey, Security, Admin, Delivery, System, Client) + Severity (Info/Warning/Critical) |
| `RequestLogs` | ทุก API request | client/key/user, IP, method, path, status, duration, bytes, trace id |
| `BlockEntries` | block list | target = IP/CIDR, client id หรือ key prefix; ชั่วคราวหรือถาวร; hit count |
| `ClientSeenIps` | IP ต้นทางที่แต่ละ client เคยใช้ | ใช้ตรวจ "IP ใหม่" และเป็นรายงานตรวจสอบผู้เรียก |
| `AdminUsers` | cache ผู้ดูแลจาก AuthenAPI | PBKDF2 hash + salt, groups, role, cache expiry, lockout |
| `Templates` | template ข้อความ | global หรือเฉพาะ client, `{{var}}` (HTML-encoded) / `{{{var}}}` (raw) |
| `SystemLogs` | log ของแอปเอง (ILogger) | Warning ขึ้นไป + Information ของโค้ด relay (ตั้งค่า `Audit:SystemLogMinimumLevel`), มี exception/trace id/path; ดูได้ที่ Logs → System log (+ live buffer 500 รายการในหน่วยความจำ) |

Schema ถูกสร้างด้วย `EnsureCreated()` ตอน start (เหมาะกับ deploy ครั้งแรก). เมื่อมีการเปลี่ยน schema ในอนาคตให้เปลี่ยนไปใช้ EF migrations (`dotnet ef migrations add ...`) แล้วแทน `EnsureCreatedAsync` ด้วย `MigrateAsync` ใน `Data/DbInitializer.cs`

## 3. Flow การส่งอีเมล

1. `POST /api/v1/emails` → `ApiKeyAuthenticationHandler`
   - อ่าน key จาก header (`X-API-Key`) หรือ `Authorization: Bearer`
   - ค้นหาด้วย prefix (memory cache 60 วิ) → เทียบ hash แบบ constant-time → ตรวจ revoked/expired/client disabled
   - ตรวจ block list (client/key), client IP allow list, HMAC signature (ถ้า client เปิด)
2. `ClientRateLimitMiddleware` → sliding window ต่อ client (`RateLimitPerMinute`) + บันทึก IP ที่เห็น + burst detection
3. `EmailQueueService.AcceptAsync`
   - idempotency: key ซ้ำ → คืนข้อความเดิม (`200`, `duplicate=true`)
   - เลือก sender profile (ระบุชื่อใน request ได้ หรือใช้ default ของ client)
   - render template/variables, validate subject/body/recipient (RFC address), จำกัดจำนวนผู้รับ, โดเมนผู้รับ, From ที่อนุญาต, custom header (`X-*` เท่านั้น), ไฟล์แนบ (ขนาด/นามสกุลต้องห้าม)
   - โควต้ารายวัน (`DailyQuota`) → `429`
   - `ContentHash` → anomaly detector (duplicate storm → `409` ถ้าตั้ง `RejectDuplicates`)
   - บันทึก `Messages` + `Attachments` + event `Queued` → `DeliveryQueue.Enqueue`
4. `EmailDispatcherService`
   - worker ดึง id จาก channel; poll loop ทุก `PollIntervalSeconds` ดึงข้อความที่ถึงกำหนด (retry/scheduled) และกู้คืนข้อความที่ค้างใน `Sending`
   - ล็อก per-profile (`MaxConcurrency`), throttle (`MaxPerMinute`)
   - `IEmailTransport.SendAsync` (SMTP หรือ HTTP API) พร้อม timeout
   - สำเร็จ → `Sent` + provider id; ล้มเหลว → ถ้า retryable และยังไม่ครบ `MaxAttempts` → `Queued` + `NextAttemptAt` (back-off `base·2^(n-1)` + jitter, สูงสุด `RetryMaxDelaySeconds`) มิฉะนั้น `Failed` + audit
   - อัปเดต health ของ profile; ครบ `ProfileUnhealthyThreshold` → alert
   - ถ้า client มี `WebhookUrl` → POST สถานะ (ลงลายเซ็น HMAC ถ้ามี secret)

## 4. ความปลอดภัย (สรุป; รายละเอียดใน SECURITY.md)

| ชั้น | กลไก |
|---|---|
| Transport | HTTPS redirect + HSTS (`Security:RequireHttps`), security headers (CSP, X-Frame-Options, nosniff, Referrer-Policy) |
| ก่อน auth | dynamic block list (IP), global IP allow list (optional), global per-IP limit, burst detection |
| Auth | API key hash + cache, scope (`send`/`read`/`admin`), expiry, revoke, rotation, per-client IP allow list, optional HMAC-SHA256 signed requests (timestamp window กัน replay) |
| หลัง auth | per-client rate limit, daily quota, recipient/from/domain policy, attachment policy, size limit |
| Anomaly | auth-failure flood, request burst, duplicate storm, recipient spike, error-rate, new-source-IP → audit + auto block + alert |
| Console | cookie `HttpOnly` + `SameSite=Strict`, `X-Requested-With` guard (CSRF), role Admin/Viewer, login rate limit, lockout, AuthenAPI + PBKDF2 cache |
| Data | secrets เข้ารหัส (Data Protection key ring ที่ `DataProtection:KeyPath`), key แสดงครั้งเดียว, log ไม่เก็บ secret, CSV export ป้องกัน formula injection |

## 5. Login ผู้ดูแลผ่าน AuthenAPI (ตามคู่มือเชื่อมต่อ AuthenAPI ฉบับปัจจุบัน)

หลักการ: **ถาม AuthenAPI ทุกครั้งที่ login** ส่วน cache ใช้ได้เฉพาะตอน "ติดต่อ AuthenAPI ไม่ได้" เท่านั้น ไม่ใช่ทางลัดให้ login เร็วขึ้น

```
username + password ─► ตรวจรูปแบบชื่อ + จำกัดการลองผิด (5 ครั้ง/15 นาที ต่อ username และต่อ IP)
   │
   ├─ ชื่อตรงกับ LocalAdmin (break-glass) ─► ตรวจกับ PBKDF2 ใน config (ไม่ส่งไป AuthenAPI)
   │
   ▼
 1) AuthenAPI  POST {BaseUrl}/api/ldap/authenticate  (ส่งชื่อตามที่พิมพ์, รหัสจริง, https, ไม่ follow redirect, timeout 45 วิ, ไม่ retry)
   ├─ 200 JSON success ─────────────► ✅ เข้าระบบ + บันทึก profile, alias และ offline credential (เฉพาะ Viewer)
   ├─ 401 JSON / 400 ───────────────► ❌ "Invalid username or password" + ลบ offline credential ของคนนี้
   ├─ 429 ──────────────────────────► ⏳ "ลองใหม่ภายหลัง"
   ├─ 401(reason)/403/404/405/3xx/401 HTML ► ⚙️ "ติดต่อผู้ดูแลระบบ" + alert + System log
   ├─ timeout / 500 / 504 / ขาดระหว่างรับ ► ⚠️ "ระบบขัดข้องชั่วคราว" (ห้าม fallback)
   └─ เชื่อมต่อไม่ได้ (refused/DNS/TLS), 502, 503 HTML, 503 JSON (AD ล่ม), circuit breaker เปิด
        ▼
 2) LDAPS bind ตรงกับ DC 1 ครั้ง ด้วย UPN จาก cache (ถ้าเปิด DirectAd) ─ สำเร็จ ► ✅ degraded / 49 ► ❌ + ลบ cache / อื่น ๆ ► ขั้น 3
        ▼
 3) Offline cache (อายุ ≤ OfflineWindowHours, จองสิทธิ์ลองแบบ atomic ≤ 5 ครั้ง/คน, ≤ 20 ครั้ง/นาทีรวม) ─ ตรง ► ✅ degraded / ไม่ตรง ► ❌
        ▼
   ⚠️ "The authentication service is temporarily unavailable"
```

| เรื่อง | การทำงาน |
|---|---|
| userKey | `userPrincipalName` ตัวพิมพ์เล็ก ถ้าว่างใช้ `samAccountName` ถ้าว่างทั้งคู่ปฏิเสธ (ตรวจด้วย `IsNullOrWhiteSpace`) ชื่อที่ผู้ใช้พิมพ์ทุกแบบเก็บเป็น alias ชี้ไปที่ userKey เดียว |
| Offline credential | HMAC-SHA256(pepper) แล้ว PBKDF2-SHA256 600,000 รอบ salt สุ่มรายคน; pepper อยู่ใน config (`AuthenApi:OfflineCachePepper`) ไม่อยู่ใน DB; ไม่มี pepper = ปิด offline; hash แบบเก่า (v1) ถูกลบตอน start |
| Admin | บัญชีที่เป็น Admin **ไม่ถูก cache offline** ใช้ได้เฉพาะ login ออนไลน์ (หรือ LocalAdmin ตอนฉุกเฉิน) |
| Degraded session | login จากขั้น 2–3 ได้สิทธิ์ Viewer (อ่านอย่างเดียว) อายุ `DegradedSessionMinutes` (60 นาที) มีแถบเตือนใน console และ audit `AdminLoginDegraded` |
| Session ปกติ | อายุสูงสุด `SessionTimeoutMinutes` (8 ชม.) idle `IdleTimeoutMinutes` (60 นาที); ถ้าผู้ดูแลกด Disable ผู้ใช้ session จะหมดภายใน 1 นาที |
| Circuit breaker | ติดต่อไม่ได้ 3 ครั้งติด → ข้ามไป fallback 30 วินาที → ปล่อย 1 request ลอง; ไม่นับ 401/timeout/500 |
| Group → role | `AdminGroups` / `ViewerGroups` (direct member); ไม่อยู่ในกลุ่มใดเลย → ปฏิเสธ; ถ้า `AdminGroups` ว่าง ทุกคนเป็น Admin (มีคำเตือนตอน start) |
| ตั้งค่าผิด | `BaseUrl` ที่เป็น http หรือมี path อื่นนอกจาก `/api` → แอปไม่ยอม start (`/api` เดิมจะถูกตัดออกพร้อมคำเตือน) |
| ทดสอบ | `EmailRelayAPI.Tests` ครอบคลุมตารางตัดสินใจ หัวข้อ 4 และกรณีทดสอบ 1–10 หัวข้อ 10 ของคู่มือ (`dotnet test`) |

## 6. Caching ในระบบ

| Cache | ที่เก็บ | อายุ |
|---|---|---|
| API key lookup (key + client + default profile) | `IMemoryCache` | `Security:ApiKeyCacheSeconds` (60s) – invalidate ทันทีเมื่อแก้ client/key จาก console |
| Block list | memory mirror ของ `BlockEntries` | refresh ทุก 60s + ทันทีเมื่อ block/unblock |
| Rate limit / anomaly counters | `SlidingCounterStore` (memory) | ตาม window, เคลียร์อัตโนมัติ |
| Daily quota | memory counter seed จาก DB | ต่อวัน (UTC) |
| Offline credential ผู้ดูแล (เฉพาะ Viewer) | `AdminUsers` (DB) + pepper ใน config | `AuthenApi:OfflineWindowHours` (24 ชม.) นับจาก login ออนไลน์ครั้งล่าสุด ใช้เฉพาะตอน AuthenAPI ติดต่อไม่ได้ |
| Microsoft Graph token | memory ต่อ profile | ตาม `expires_in` |
| Seen IPs ต่อ client | memory set โหลดจาก DB | ตลอด process |

> ระบบรันได้หลาย instance ต่อ DB เดียวกัน แต่ counter ที่อยู่ใน memory (rate limit/anomaly/quota) จะนับแยกต่อ instance — ถ้า scale-out ให้ย้าย counter ไป Redis (ดูข้อ 8)

## 7. Observability

- **Dashboard**: KPI วันนี้/7 วัน, success rate, latency, queue depth, security events, blocks, keys due rotation; กราฟรายชั่วโมง (24h) และรายวัน (30d); top clients; profile health; เหตุการณ์ความปลอดภัยและ failure ล่าสุด (auto refresh 30s)
- **Messages**: ค้นหา/กรองทุกมิติ, รายละเอียด + timeline + preview HTML (sandboxed iframe) + ไฟล์แนบ, requeue/cancel/delete, export CSV
- **Logs**: Audit trail (category/severity/action/user/IP/ช่วงเวลา/ค้นหา), Request log (status/path/client/IP) และ **System log** (warning/error/exception ของแอป พร้อม stack trace และ trace id เชื่อมกับ request log), export CSV, ไฟล์ `logs/audit-yyyyMMdd.jsonl`
- Admin API ทุก endpoint ที่เป็น `PUT`/`DELETE` มี alias แบบ `POST .../update` และ `POST .../delete` ซึ่ง console ใช้เป็นหลัก เพราะ IIS/proxy/WAF บางแห่งตอบ 405 ให้ verb เหล่านั้น
- **Security**: block list (+history), IP ต้นทางต่อ client, key health (หมดอายุ/ครบกำหนด rotate/ไม่ได้ใช้นาน), สถานะ AuthenAPI, console users
- **Reports**: ช่วงเวลา/ต่อ client → by day/by client/by profile/by status/top errors, export CSV
- `/health`, `/health/ready` (ตรวจ DB + queue) สำหรับ monitoring ภายนอก

## 8. วิเคราะห์: ระบบควรมี/เป็นอย่างไรเพิ่มเติม

สิ่งที่ทำแล้วในเวอร์ชันนี้ครอบคลุมโจทย์ทั้งหมด (relay SMTP/API, หลาย client ผูก profile, log/audit ละเอียด, security ขั้นสูง, rotation แบบเลือกได้, dashboard/รายงาน, anomaly detection, ตรวจสอบผู้เรียก, login ผ่าน AuthenAPI + cache) ข้อเสนอแนะสำหรับขั้นถัดไป เรียงตามความคุ้มค่า:

1. **Inbound / delivery events จาก provider** – รับ webhook ของ SendGrid/Mailgun (delivered, bounced, complaint) และ DSN/bounce mailbox (IMAP polling) เพื่อให้สถานะ "ถึงผู้รับจริง" ไม่ใช่แค่ "ส่งออกสำเร็จ"; เก็บเป็น `EmailEvents` type `ProviderEvent` และ suppression list ของ bounce ถาวร
2. **Scale-out** – ย้าย rate-limit/anomaly/quota counters ไป Redis และใช้ DB-backed lease (`SKIP LOCKED`) ในการดึงงาน เพื่อรันหลาย instance หลัง load balancer; หรือใช้ queue ภายนอก (RabbitMQ/Azure Service Bus) เมื่อปริมาณสูงมาก
3. **Suppression & compliance** – รายการห้ามส่ง (unsubscribe/bounce/complaint) ต่อ client, header `List-Unsubscribe`, นโยบาย PII retention แยกจาก log (เช่น ลบ body แต่คง metadata), การ mask ข้อมูลใน log
4. **DKIM/SPF/DMARC** – ลงลายเซ็น DKIM ใน relay (MailKit รองรับ `DkimSigner`) สำหรับ profile SMTP ที่ส่งตรง, และหน้า "sender domain check" ที่ตรวจ SPF/DMARC ของโดเมน From
5. **Per-client dashboard/self-service** – ให้ client ดูสถิติ/ log ของตัวเองด้วย key scope `read` (API มีแล้ว: `/api/v1/emails`, `/api/v1/me`) เพิ่ม UI หรือ portal แยก
6. **Alert channels** – Teams/Slack/LINE Notify adapter เพิ่มจาก webhook ทั่วไป, นโยบาย escalation, และ alert จากตัวชี้วัด (queue depth, success rate ต่ำกว่า x%)
7. **Metrics export** – `/metrics` แบบ Prometheus (OpenTelemetry) สำหรับ Grafana; trace id ผูกกับ W3C trace context แล้ว (`TraceId` ใน log)
8. **Secrets** – ย้าย AuthenAPI key / connection string ไป Azure Key Vault หรือ environment variables ใน production; เปิด `DataProtection` key encryption ด้วย certificate บน Windows/IIS
9. **Multi-tenant RBAC ใน console** – role ละเอียดกว่า Admin/Viewer (เช่น ดูได้เฉพาะ client ของแผนก) โดย map จาก AD group → client tags
10. **Message size & attachment policy ต่อ client**, virus scan hook (ICAP/ClamAV) สำหรับไฟล์แนบ, และ content policy (ห้ามคำ/ลิงก์ภายนอก) ต่อ client
11. **Tests** – unit tests (validation, rate limiter, anomaly thresholds, template renderer, signature) และ integration tests ด้วย `WebApplicationFactory` + smtp4dev/MailHog
12. **EF migrations + seed script** สำหรับ production upgrade path และ backup/restore runbook (SQLite → SQL Server)

## 9. สิ่งที่ตรวจสอบแล้ว และสิ่งที่ยังต้องทดสอบในระบบจริง

ตรวจแล้ว:

1. `dotnet build` ผ่านโดยไม่มี error และ `dotnet test` ผ่านทั้งหมด (ตารางตัดสินใจของ AuthenAPI, กรณีทดสอบ 1–10, circuit breaker, offline hash)
2. รันแอปจริงกับ SQLite: login console, สร้าง/แก้ไข client และ profile, สร้าง API key, System log
3. Login กับ AuthenAPI จำลองผ่าน https: ส่งชื่อตามที่พิมพ์, API key และ User-Agent, ไม่ follow redirect, timeout, connection refused → offline แบบ degraded, circuit breaker, BaseUrl ที่เป็น http ทำให้แอปไม่ start
4. อัปเกรดฐานข้อมูลที่สร้างจากเวอร์ชันแรก: เพิ่มตารางและคอลัมน์ที่ขาด และลบ hash รหัสผ่านแบบเก่า

ยังต้องทดสอบในระบบจริง:

1. SQL Server (การสร้างตารางและการอัปเกรด schema)
2. AuthenAPI และ Active Directory จริง ตามหัวข้อ 10 ของคู่มือ AuthenAPI รวมถึง LDAPS fallback (`DirectAd`) ซึ่งทดสอบแค่ด้วย test double
3. IIS (โมดูล WebDAV, request filtering) และการส่งอีเมลผ่าน SMTP/API ของผู้ให้บริการจริง
