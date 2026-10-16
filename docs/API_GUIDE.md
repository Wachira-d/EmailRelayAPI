# คู่มือการเชื่อมต่อ API (Client Integration Guide)

Base URL: `https://<relay-host>` · Swagger: `/swagger` (เอกสาร v1 สำหรับ client, `admin` สำหรับ console)

ทุก response ใช้ envelope เดียวกับ AuthenAPI:

```json
{ "success": true, "message": "Message accepted for delivery", "data": { ... }, "error": null, "timestamp": "2026-10-16T03:00:00Z" }
```

## 1. การยืนยันตัวตน

ส่ง API key ใน header (ชื่อ header ตั้งค่าได้ที่ `Security:ApiKeyHeaderName`, ค่าเริ่มต้น `X-API-Key`) หรือ `Authorization: Bearer <key>`

```http
POST /api/v1/emails HTTP/1.1
X-API-Key: erk_7Kq2...   (แสดงครั้งเดียวตอนสร้างใน console)
Content-Type: application/json
```

| scope | ใช้ได้กับ |
|---|---|
| `send` | ส่ง/ยกเลิกข้อความ, rotate key ตัวเอง |
| `read` | ดูสถานะ/รายการข้อความของตัวเอง, `/api/v1/me` |
| `admin` | เรียก Admin API (`/api/admin/...`) แทน cookie |

ข้อผิดพลาดที่พบบ่อย

| HTTP | ความหมาย |
|---|---|
| 401 | ไม่มี key / key ผิด / หมดอายุ / ถูก revoke / ลายเซ็นไม่ถูกต้อง |
| 403 | IP ต้นทางไม่อยู่ใน allow list ของ client, client/IP ถูกบล็อก, scope ไม่พอ, From หรือโดเมนผู้รับไม่อนุญาต |
| 409 | ข้อความเดิมซ้ำถี่เกิน threshold (duplicate storm) – ใช้ `idempotencyKey` แทนการ retry เอง |
| 413 | ไฟล์แนบรวมเกิน `Delivery:MaxAttachmentBytes` |
| 422 | ไม่มี sender profile / profile ปิดอยู่ / template ไม่พบ |
| 429 | เกิน rate limit ต่อนาที (`Retry-After`) หรือเกินโควต้ารายวัน |

Header ที่ตอบกลับ: `X-RateLimit-Limit`, `X-RateLimit-Remaining`

## 2. ส่งอีเมล

`POST /api/v1/emails` → `202 Accepted`

```json
{
  "from": { "address": "noreply@company.com", "name": "HR Portal" },
  "replyTo": "hr@company.com",
  "to": [ "alice@example.com", { "address": "bob@example.com", "name": "Bob" } ],
  "cc": [], "bcc": [],
  "subject": "Your request #{{ticket}}",
  "text": "Plain text body",
  "html": "<p>HTML body <img src=\"cid:logo\"></p>",
  "attachments": [
    { "fileName": "report.pdf", "contentType": "application/pdf", "contentBase64": "JVBERi0..." },
    { "fileName": "logo.png", "contentType": "image/png", "contentBase64": "iVBOR...", "contentId": "logo" }
  ],
  "headers": { "X-Ticket-Id": "4711" },
  "tags": [ "hr", "ticket" ],
  "idempotencyKey": "ticket-4711-created",
  "priority": 1,
  "scheduleAt": "2026-10-17T02:00:00Z",
  "template": "ticket-created",
  "variables": { "ticket": "4711", "name": "Alice" },
  "senderProfile": "Exchange-Internal"
}
```

| ฟิลด์ | หมายเหตุ |
|---|---|
| `from` | ไม่ใส่ = ใช้ From ของ sender profile; ใส่ได้เฉพาะที่อยู่/โดเมนใน *Allowed From* ของ client |
| `to`/`cc`/`bcc` | string หรือ object; รวมกันไม่เกิน `MaxRecipientsPerMessage` ของ client |
| `text`/`html` | ต้องมีอย่างน้อยหนึ่ง (หรือมาจาก template) |
| `attachments` | base64; รวมไม่เกิน 10 MB (ค่าเริ่มต้น); นามสกุล `.exe .js .bat ...` ถูกปฏิเสธ; ใส่ `contentId` สำหรับ inline image |
| `headers` | เฉพาะ `X-*` |
| `idempotencyKey` | ซ้ำ → คืนข้อความเดิมด้วย `200` และ `data.duplicate = true` (ไม่ส่งซ้ำ) |
| `priority` | `1` = ส่งก่อน |
| `scheduleAt` | UTC; สถานะ `scheduled` จนถึงเวลา |
| `template` + `variables` | ใช้ template ที่ผู้ดูแลสร้าง; `{{var}}` ถูก HTML-encode ใน HTML body, `{{{var}}}` ใส่ดิบ; `variables` ใช้กับ subject/body ที่ส่งมาเองได้ด้วย |
| `senderProfile` | override profile (ต้องมีอยู่และเปิดใช้งาน) |

Response

```json
{ "success": true, "message": "Message accepted for delivery",
  "data": { "id": "6f1c...", "status": "queued", "createdAt": "...", "scheduledAt": null, "duplicate": false } }
```

### ส่งหลายฉบับ

`POST /api/v1/emails/batch` body `{ "messages": [ {...}, {...} ] }` (สูงสุด `Delivery:MaxBatchSize` = 100) → `200` พร้อม `data[]` ที่บอก `accepted`, `id`, `error` ต่อรายการ (แต่ละรายการนับโควต้าแยก)

## 3. ติดตามสถานะ

- `GET /api/v1/emails/{id}` → สถานะ, จำนวนครั้งที่พยายาม, `nextAttemptAt`, `providerMessageId`, `lastError`, timeline `events[]`, ไฟล์แนบ
- `GET /api/v1/emails?status=failed&from=2026-10-01&search=invoice&page=1&pageSize=50`
- `POST /api/v1/emails/{id}/cancel` (เฉพาะ `queued`/`scheduled`)

สถานะ: `queued` → `sending` → `sent` | `failed` ; `scheduled` ; `cancelled`

### Webhook แจ้งสถานะ (ตั้งค่าใน client)

เมื่อข้อความ `sent` หรือ `failed` ระบบจะ `POST` ไปที่ `WebhookUrl` ของ client (retry 3 ครั้ง)

```json
{ "event": "email.sent", "id": "6f1c...", "status": "sent", "clientId": 3, "idempotencyKey": "ticket-4711-created",
  "subject": "...", "attempts": 1, "providerMessageId": "<...@company.com>", "error": null, "sentAt": "...", "tags": ["hr"], "timestamp": "..." }
```

Header: `X-Relay-Event`, `X-Relay-Timestamp` (unix seconds), `X-Relay-Signature: sha256=hex(HMAC_SHA256(secret, timestamp + "." + body))` เมื่อตั้ง webhook secret ไว้ — ตรวจลายเซ็นและปฏิเสธ timestamp ที่เก่ากว่า 5 นาที

## 4. ข้อมูลตัวเองและ key rotation

- `GET /api/v1/me` → ชื่อ client, profile, rate limit, โควต้า/ใช้ไปวันนี้, scope, prefix ของ key, วันหมดอายุ, กำหนด rotation
- `POST /api/v1/me/keys/rotate` → ออก key ใหม่ (scope เดิม) คืนค่า **ครั้งเดียว**; key เดิมใช้ได้ต่อจนถึง `oldKeyExpiresAt` (grace period ของ client, ค่าเริ่มต้น 7 วัน) จึงสลับได้โดยไม่มี downtime

Key rotation เป็น **ทางเลือก**: ถ้า client ไม่เปิด `KeyRotationEnabled` key จะไม่หมดอายุเอง (ยกเว้นผู้ดูแลกำหนด `ExpiresAt`)

## 5. Signed requests (HMAC) – สำหรับ client ที่ต้องการความปลอดภัยสูง

เมื่อผู้ดูแลสร้าง *signing secret* และเปิด *Require signed requests* ทุก request ต้องมี

```
X-Timestamp: 1760583600                      (unix seconds, คลาดได้ ±300 วิ)
X-Signature: sha256=<hex>                    (ตัวพิมพ์เล็ก)
canonical = "{timestamp}.{METHOD}.{path}.{sha256hex(body)}"   เช่น "1760583600.POST./api/v1/emails.e3b0c4..."
signature = HMAC_SHA256(secret, canonical)
```

ตัวอย่าง Python

```python
import hashlib, hmac, json, time, requests
secret = b"..."; key = "erk_..."
body = json.dumps({"to": ["a@b.com"], "subject": "Hi", "text": "Hello"}).encode()
ts = str(int(time.time()))
canonical = f"{ts}.POST./api/v1/emails.{hashlib.sha256(body).hexdigest()}"
sig = hmac.new(secret, canonical.encode(), hashlib.sha256).hexdigest()
r = requests.post("https://relay/api/v1/emails", data=body,
    headers={"X-API-Key": key, "Content-Type": "application/json", "X-Timestamp": ts, "X-Signature": "sha256=" + sig})
print(r.status_code, r.json())
```

ตัวอย่าง C#

```csharp
using var http = new HttpClient { BaseAddress = new Uri("https://relay/") };
http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
var payload = new { to = new[] { "a@b.com" }, subject = "Hi", html = "<p>Hello</p>", idempotencyKey = Guid.NewGuid().ToString() };
var json = JsonSerializer.Serialize(payload);
var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
var sig = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{ts}.POST./api/v1/emails.{bodyHash}"))).ToLowerInvariant();
var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/emails") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
req.Headers.Add("X-Timestamp", ts); req.Headers.Add("X-Signature", "sha256=" + sig);
var res = await http.SendAsync(req);
```

## 6. แนวปฏิบัติสำหรับผู้พัฒนา client

1. ใส่ `idempotencyKey` ทุกครั้งที่ส่งจากงานที่อาจถูก retry (เช่น job queue) — ระบบจะไม่ส่งซ้ำและไม่นับเป็น duplicate storm
2. เมื่อได้ `429` ให้รอตาม `Retry-After`; อย่า retry แบบวนทันที (จะถูกตรวจจับเป็น burst และบล็อก IP ชั่วคราว)
3. เก็บ key ใน secret store ไม่ฝังในโค้ด; ใช้ key แยกต่อ environment (ตั้งชื่อ key ใน console)
4. ใช้ webhook แทน polling ถ้าต้องการสถานะแบบ real-time; ถ้า poll ให้ใช้ `GET /api/v1/emails?status=...` เป็นรอบ ไม่ poll ทีละ id
5. ตั้ง `AllowedIpRanges` ของ client ให้แคบที่สุด และเปิด signed requests สำหรับระบบที่ส่งข้อมูลสำคัญ

## 7. Admin API (สำหรับ automation)

ทุก endpoint ภายใต้ `/api/admin/*` ใช้ได้ทั้ง cookie (login ที่ `/admin/`) และ key ที่มี scope `admin` (ดู Swagger "Administration API") เช่น

- `GET /api/admin/dashboard`, `GET /api/admin/reports/usage?from=&to=&clientId=`
- `GET/POST/PUT/DELETE /api/admin/clients`, `POST /api/admin/clients/{id}/keys`, `.../keys/{keyId}/revoke|rotate`, `POST /api/admin/clients/{id}/signing-secret`
- `GET/POST/PUT/DELETE /api/admin/profiles`, `POST /api/admin/profiles/{id}/test`, `/test-send`
- `GET /api/admin/messages`, `/{id}`, `POST .../requeue|cancel`, `GET /api/admin/messages/export`
- `GET /api/admin/logs/audit`, `/requests` (+ `/export`)
- `GET/POST/DELETE /api/admin/security/blocks`, `GET /api/admin/security/seen-ips`, `/key-health`, `/users`
- `GET/POST/PUT/DELETE /api/admin/templates`, `POST /api/admin/templates/{id}/preview`
- `GET /api/admin/system/info`, `POST /api/admin/system/purge`, `/rotation-check`

เมื่อเรียกด้วย cookie ต้องส่ง header `X-Requested-With: XMLHttpRequest` กับ request ที่ไม่ใช่ GET (CSRF guard); key แบบ `admin` ไม่ต้อง
