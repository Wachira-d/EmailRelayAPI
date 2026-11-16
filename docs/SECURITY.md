# ความปลอดภัยและการตรวจจับการใช้งานผิดปกติ

## 1. ชั้นป้องกัน (ตามลำดับ pipeline)

| # | ชั้น | ค่าตั้ง (appsettings) | ผลเมื่อไม่ผ่าน |
|---|---|---|---|
| 1 | Forwarded headers (เฉพาะ proxy ที่ระบุ) | `Security:KnownProxies` | ไม่เชื่อ `X-Forwarded-For` จากที่อื่น |
| 2 | Security headers | – | CSP, `X-Frame-Options: DENY`, nosniff, Referrer-Policy, no-store สำหรับ `/api` |
| 3 | HTTPS + HSTS | `Security:RequireHttps` | redirect (ปิดใน Development) |
| 4 | Block list (IP/CIDR) | จัดการใน console / อัตโนมัติ | `403` (audit แบบ throttle 1 ครั้ง/นาที/IP) |
| 5 | Global IP allow list | `Security:EnableIpAllowList`, `AllowedIpAddresses` | `403` + นับเป็น auth failure |
| 6 | Global per-IP limit | `Security:GlobalRateLimitPerMinute` (600) | `429` |
| 7 | Burst detection ต่อ IP | `Anomaly:BurstThreshold`/`BurstWindowSeconds` (100/10s) | audit Critical + auto block |
| 8 | API key auth | – | `401` + นับ auth failure |
| 9 | Block list (client/key) | console | `403` |
| 10 | Client IP allow list | ต่อ client | `403` + นับ auth failure |
| 11 | HMAC signature | ต่อ client (`RequireSignedRequests`) | `401` + นับ auth failure |
| 12 | Client rate limit | ต่อ client `RateLimitPerMinute` | `429` (+ audit throttle) |
| 13 | Daily quota | ต่อ client `DailyQuota` | `429` |
| 14 | Content policy | ผู้รับ/From/โดเมน/header/attachment | `400`/`403`/`413` |
| 15 | Duplicate storm | `Anomaly:DuplicateThreshold`, `RejectDuplicates` | `409` |

## 2. API key

- รูปแบบ `erk_` + 43 ตัวอักษร (256-bit random); เก็บเฉพาะ **SHA-256** และ **prefix 12 ตัว** (ใช้ค้นหา/แสดงผล)
- เทียบ hash ด้วย `CryptographicOperations.FixedTimeEquals` (กัน timing attack)
- cache ผลค้นหา 60 วินาที; แก้ไข/revoke ที่ console มีผลทันที (invalidate)
- scope: `send`, `read`, `admin`; กำหนด `ExpiresAt` ได้; revoke พร้อมเหตุผล; ติดตาม `LastUsedAt/Ip/UseCount`
- **Rotation**: ปิดเป็นค่าเริ่มต้น เมื่อ client เปิด `KeyRotationEnabled` → `KeyRotationService` ตรวจทุกชั่วโมง key ที่อายุเกิน `KeyRotationDays` จะได้ `ExpiresAt = due + GraceDays`, บันทึก audit `KeyRotationDue` และแจ้งเตือน; ออก key ใหม่ได้ทั้งจาก console (*Rotate*) และ self-service (`POST /api/v1/me/keys/rotate`) โดย key เก่ายังใช้ได้ช่วง grace

## 3. Anomaly detection (`Security/AnomalyDetector.cs`)

| เหตุการณ์ | เงื่อนไข (ค่าเริ่มต้น) | การตอบสนอง |
|---|---|---|
| `AuthFailureFlood` | auth ล้มเหลว ≥ 10 ครั้ง/10 นาที จาก IP เดียว | Critical + block IP 30 นาที + alert |
| `RequestBurst` | ≥ 100 requests/10 วิ จาก IP เดียว (ก่อน auth) | Critical + block IP + alert |
| `ClientRequestBurst` | ≥ 100 requests/10 วิ จาก client เดียว | Warning |
| `DuplicateMessageStorm` | ข้อความ hash เดียวกัน ≥ 5 ครั้ง/10 นาที | Warning + ปฏิเสธ `409` (ถ้า `RejectDuplicates`) |
| `RecipientSpike` | ผู้รับรวม ≥ 2000/10 นาที ต่อ client | Warning |
| `ClientErrorRate` | 4xx ≥ 100/5 นาที ต่อ client | Warning (integration ผิดพลาด) |
| `NewSourceIp` | client เรียกจาก IP ที่ไม่เคยเห็น | Info (ดูรายการที่หน้า Security) |
| `AdminAccountLocked` | login console ผิด ≥ 5 ครั้ง | Critical + lock 15 นาที |
| `SenderProfileUnhealthy` | ส่งล้มเหลวติดกัน ≥ 5 ครั้ง | Critical + alert |

การแจ้งเตือนซ้ำถูก throttle ต่อ key ภายใน window; `Anomaly:AutoBlock=false` เพื่อให้บันทึกอย่างเดียว

## 4. Console

- Cookie `EmailRelay.Admin`: `HttpOnly`, `SameSite=Strict`, sliding expiration (`AuthenApi:SessionTimeoutMinutes`)
- CSRF: request ที่ไม่ใช่ GET ต้องมี `X-Requested-With: XMLHttpRequest` (`RequireAjaxHeaderAttribute`)
- Login: ถาม AuthenAPI ทุกครั้ง (ดู ARCHITECTURE.md ข้อ 5) · จำกัด login ผิด 5 ครั้ง/15 นาที ต่อ username และต่อ IP · request ไปที่ endpoint login ไม่เกิน 10 ครั้ง/นาที/IP · ทุกความพยายาม login ถูก audit พร้อมผลจาก AuthenAPI (ไม่บันทึกรหัสผ่าน)
- Role: `Admin` (แก้ไขได้) / `Viewer` (อ่านอย่างเดียว) map จาก AD group; session ที่ได้จาก fallback เป็น Viewer เสมอและหมดอายุใน 60 นาที
- Session มีอายุสูงสุด 8 ชม.; Disable ผู้ใช้แล้ว session เดิมใช้ไม่ได้ภายใน 1 นาที และ offline credential ถูกลบ
- CSP `script-src 'self'` – console ไม่ใช้ inline script; HTML ของข้อความแสดงใน iframe `sandbox=""`
- CSV export escape ค่าที่ขึ้นต้นด้วย `= + - @` (formula injection)

## 5. ข้อมูลที่เก็บ

| ข้อมูล | วิธีเก็บ |
|---|---|
| API key | SHA-256 เท่านั้น |
| SMTP password, provider API key, OAuth secret, signing secret, webhook secret | เข้ารหัสด้วย ASP.NET Data Protection (key ring ที่ `DataProtection:KeyPath`; **สำรองโฟลเดอร์นี้** ไม่เช่นนั้นถอดรหัสไม่ได้หลังย้ายเครื่อง) |
| รหัสผ่านผู้ดูแล (offline credential, เฉพาะ Viewer) | HMAC-SHA256 ด้วย pepper นอก DB แล้ว PBKDF2-SHA256 600,000 รอบ + salt 16 ไบต์ ต่อผู้ใช้; ใช้ได้ ≤ 24 ชม. หลัง login ออนไลน์ และเฉพาะตอน AuthenAPI ติดต่อไม่ได้ |
| Local admin | `pbkdf2$iterations$salt$hash` ใน appsettings (สร้างด้วย `dotnet run -- hash-password`) |
| เนื้อหาอีเมล/ไฟล์แนบ | DB/ดิสก์ ตามอายุ `Retention:MessageRetentionDays` แล้ว purge |

## 6. Checklist ก่อนขึ้น production

- [ ] `Security:RequireHttps=true` และใบรับรองถูกต้อง; ตั้ง `KnownProxies` ถ้ามี reverse proxy/LB
- [ ] ตั้ง `AuthenApi:BaseUrl` เป็น https ไม่มี path (เช่น `https://authen.hondacx.me`), `ApiKey` เฉพาะของระบบนี้, `AdminGroups` และ `ViewerGroups` (อย่าปล่อยว่าง) · ปิด `LocalAdmin` หรือตั้งรหัสที่แข็งแรงและเก็บไว้เป็น break-glass
- [ ] ตั้ง `AuthenApi:OfflineCachePepper` (สุ่ม ≥ 32 ตัวอักษร เก็บนอก DB เช่น environment variable `AuthenApi__OfflineCachePepper`) หรือตั้ง `OfflineWindowHours: 0` ถ้าไม่ต้องการ login ตอน AuthenAPI ล่ม
- [ ] ส่ง IP ขาออกของเซิร์ฟเวอร์ให้ผู้ดูแล AuthenAPI ขอ scope `authenticate` และทดสอบตามหัวข้อ 10 ของคู่มือ AuthenAPI
- [ ] เปิด `Security:EnableIpAllowList` เมื่อ client ทั้งหมดอยู่ในเครือข่ายที่รู้จัก; ตั้ง `AllowedIpRanges` ต่อ client
- [ ] ตั้ง `Alerts` (admin emails + sender profile สำหรับแจ้งเตือน หรือ webhook)
- [ ] สำรอง `data/keys` (Data Protection) และฐานข้อมูล; จำกัดสิทธิ์โฟลเดอร์ `data/`, `logs/` ให้เฉพาะ service account
- [ ] ทบทวน retention ให้สอดคล้องนโยบายข้อมูลส่วนบุคคล
- [ ] ปรับ threshold ใน `Anomaly` ให้เหมาะกับปริมาณจริง (ดูจาก Reports หลังใช้งาน 1–2 สัปดาห์)
