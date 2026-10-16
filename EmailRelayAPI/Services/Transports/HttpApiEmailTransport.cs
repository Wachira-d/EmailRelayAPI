using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Security;

namespace EmailRelayAPI.Services.Transports;

/// <summary>Delivers mail through HTTP mail APIs: SendGrid, Mailgun, Microsoft Graph or a generic JSON endpoint.</summary>
public class HttpApiEmailTransport : IEmailTransport
{
    public const string HttpClientName = "mail-api";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ISecretProtector _secrets;
    private readonly ILogger<HttpApiEmailTransport> _logger;
    private readonly ConcurrentDictionary<int, (string Token, DateTime Expires)> _graphTokens = new();
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public HttpApiEmailTransport(IHttpClientFactory httpFactory, ISecretProtector secrets, ILogger<HttpApiEmailTransport> logger)
    {
        _httpFactory = httpFactory;
        _secrets = secrets;
        _logger = logger;
    }

    public TransportType Type => TransportType.HttpApi;

    public async Task<TransportResult> SendAsync(SenderProfile profile, OutboundEmail email, CancellationToken ct)
    {
        try
        {
            return profile.ApiProvider switch
            {
                ApiProvider.SendGrid => await SendGridAsync(profile, email, ct),
                ApiProvider.Mailgun => await MailgunAsync(profile, email, ct),
                ApiProvider.MicrosoftGraph => await GraphAsync(profile, email, ct),
                _ => await GenericAsync(profile, email, ct)
            };
        }
        catch (Exception ex)
        {
            return Classify(ex);
        }
    }

    public async Task<TransportResult> TestAsync(SenderProfile profile, CancellationToken ct)
    {
        try
        {
            var client = CreateClient(profile);
            switch (profile.ApiProvider)
            {
                case ApiProvider.SendGrid:
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.sendgrid.com/v3/scopes");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey(profile));
                    using var res = await client.SendAsync(req, ct);
                    var body = await res.Content.ReadAsStringAsync(ct);
                    return res.IsSuccessStatusCode
                        ? TransportResult.Ok(null, "SendGrid credentials accepted")
                        : TransportResult.Fail($"SendGrid returned {(int)res.StatusCode}", false, body);
                }
                case ApiProvider.Mailgun:
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"{MailgunBase(profile)}/domains/{profile.ApiDomain}");
                    req.Headers.Authorization = BasicAuth("api", ApiKey(profile));
                    using var res = await client.SendAsync(req, ct);
                    var body = await res.Content.ReadAsStringAsync(ct);
                    return res.IsSuccessStatusCode
                        ? TransportResult.Ok(null, $"Mailgun domain {profile.ApiDomain} verified")
                        : TransportResult.Fail($"Mailgun returned {(int)res.StatusCode}", false, body);
                }
                case ApiProvider.MicrosoftGraph:
                {
                    var token = await GetGraphTokenAsync(profile, client, ct);
                    return TransportResult.Ok(null, $"Acquired Microsoft Graph token (length {token.Length})");
                }
                default:
                {
                    if (string.IsNullOrWhiteSpace(profile.ApiEndpoint) || !Uri.TryCreate(profile.ApiEndpoint, UriKind.Absolute, out var uri))
                        return TransportResult.Fail("API endpoint is not a valid absolute URL", false);
                    var addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
                    return TransportResult.Ok(null, $"Endpoint {uri.Host} resolves to {string.Join(", ", addresses.Select(a => a.ToString()))}. Generic endpoints have no credential test.");
                }
            }
        }
        catch (Exception ex)
        {
            return Classify(ex);
        }
    }

    // ---------------- SendGrid ----------------
    private async Task<TransportResult> SendGridAsync(SenderProfile profile, OutboundEmail email, CancellationToken ct)
    {
        var endpoint = string.IsNullOrWhiteSpace(profile.ApiEndpoint) ? "https://api.sendgrid.com/v3/mail/send" : profile.ApiEndpoint;

        var personalization = new JsonObject
        {
            ["to"] = new JsonArray(email.To.Select(ToSendGrid).ToArray())
        };
        if (email.Cc.Count > 0) personalization["cc"] = new JsonArray(email.Cc.Select(ToSendGrid).ToArray());
        if (email.Bcc.Count > 0) personalization["bcc"] = new JsonArray(email.Bcc.Select(ToSendGrid).ToArray());

        var content = new JsonArray();
        if (!string.IsNullOrEmpty(email.Text)) content.Add(new JsonObject { ["type"] = "text/plain", ["value"] = email.Text });
        if (!string.IsNullOrEmpty(email.Html)) content.Add(new JsonObject { ["type"] = "text/html", ["value"] = email.Html });
        if (content.Count == 0) content.Add(new JsonObject { ["type"] = "text/plain", ["value"] = " " });

        var payload = new JsonObject
        {
            ["personalizations"] = new JsonArray(personalization),
            ["from"] = new JsonObject { ["email"] = email.FromAddress, ["name"] = email.FromName },
            ["subject"] = email.Subject,
            ["content"] = content,
            ["custom_args"] = new JsonObject { ["relay_id"] = email.Id.ToString() }
        };
        if (!string.IsNullOrWhiteSpace(email.ReplyTo)) payload["reply_to"] = new JsonObject { ["email"] = email.ReplyTo };
        if (email.Headers.Count > 0)
        {
            var h = new JsonObject();
            foreach (var (k, v) in email.Headers) h[k] = v;
            payload["headers"] = h;
        }
        if (email.Attachments.Count > 0)
        {
            payload["attachments"] = new JsonArray(email.Attachments.Select(a => (JsonNode)new JsonObject
            {
                ["content"] = Convert.ToBase64String(a.Content),
                ["filename"] = a.FileName,
                ["type"] = a.ContentType,
                ["disposition"] = a.IsInline ? "inline" : "attachment",
                ["content_id"] = a.IsInline ? a.ContentId : null
            }).ToArray());
        }
        if (email.Tags.Count > 0) payload["categories"] = new JsonArray(email.Tags.Take(10).Select(t => (JsonNode)t).ToArray());

        var client = CreateClient(profile);
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(JsonOpts), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey(profile));

        using var res = await client.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.IsSuccessStatusCode)
        {
            var id = res.Headers.TryGetValues("X-Message-Id", out var ids) ? ids.FirstOrDefault() : null;
            return TransportResult.Ok(id, $"HTTP {(int)res.StatusCode} {body}");
        }
        return HttpFailure("SendGrid", res.StatusCode, body);
    }

    private static JsonNode ToSendGrid(Models.EmailAddressDto a)
        => new JsonObject { ["email"] = a.Address, ["name"] = string.IsNullOrWhiteSpace(a.Name) ? null : a.Name };

    // ---------------- Mailgun ----------------
    private static string MailgunBase(SenderProfile profile)
        => string.IsNullOrWhiteSpace(profile.ApiEndpoint) ? "https://api.mailgun.net/v3" : profile.ApiEndpoint.TrimEnd('/');

    private async Task<TransportResult> MailgunAsync(SenderProfile profile, OutboundEmail email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.ApiDomain))
            return TransportResult.Fail("Mailgun sending domain (ApiDomain) is not configured", false);

        var form = new MultipartFormDataContent();
        form.Add(new StringContent(FormatAddress(email.FromAddress, email.FromName)), "from");
        foreach (var a in email.To) form.Add(new StringContent(FormatAddress(a.Address, a.Name)), "to");
        foreach (var a in email.Cc) form.Add(new StringContent(FormatAddress(a.Address, a.Name)), "cc");
        foreach (var a in email.Bcc) form.Add(new StringContent(FormatAddress(a.Address, a.Name)), "bcc");
        form.Add(new StringContent(email.Subject), "subject");
        if (!string.IsNullOrEmpty(email.Text)) form.Add(new StringContent(email.Text), "text");
        if (!string.IsNullOrEmpty(email.Html)) form.Add(new StringContent(email.Html), "html");
        if (!string.IsNullOrWhiteSpace(email.ReplyTo)) form.Add(new StringContent(email.ReplyTo), "h:Reply-To");
        foreach (var (k, v) in email.Headers) form.Add(new StringContent(v), "h:" + k);
        foreach (var t in email.Tags.Take(3)) form.Add(new StringContent(t), "o:tag");
        form.Add(new StringContent(email.Id.ToString()), "v:relay_id");
        foreach (var a in email.Attachments)
        {
            var part = new ByteArrayContent(a.Content);
            part.Headers.ContentType = MediaTypeHeaderValue.TryParse(a.ContentType, out var mediaType) ? mediaType : new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, a.IsInline ? "inline" : "attachment", a.FileName);
        }

        var client = CreateClient(profile);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{MailgunBase(profile)}/{profile.ApiDomain}/messages") { Content = form };
        req.Headers.Authorization = BasicAuth("api", ApiKey(profile));

        using var res = await client.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.IsSuccessStatusCode)
        {
            return TransportResult.Ok(TryReadJsonString(body, "id"), body);
        }
        return HttpFailure("Mailgun", res.StatusCode, body);
    }

    // ---------------- Microsoft Graph ----------------
    private async Task<TransportResult> GraphAsync(SenderProfile profile, OutboundEmail email, CancellationToken ct)
    {
        var client = CreateClient(profile);
        var token = await GetGraphTokenAsync(profile, client, ct);
        var baseUrl = string.IsNullOrWhiteSpace(profile.ApiEndpoint) ? "https://graph.microsoft.com/v1.0" : profile.ApiEndpoint.TrimEnd('/');

        var message = new JsonObject
        {
            ["subject"] = email.Subject,
            ["body"] = new JsonObject
            {
                ["contentType"] = string.IsNullOrEmpty(email.Html) ? "Text" : "HTML",
                ["content"] = string.IsNullOrEmpty(email.Html) ? (email.Text ?? string.Empty) : email.Html
            },
            ["toRecipients"] = new JsonArray(email.To.Select(ToGraph).ToArray()),
            ["from"] = new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = email.FromAddress, ["name"] = email.FromName } }
        };
        if (email.Cc.Count > 0) message["ccRecipients"] = new JsonArray(email.Cc.Select(ToGraph).ToArray());
        if (email.Bcc.Count > 0) message["bccRecipients"] = new JsonArray(email.Bcc.Select(ToGraph).ToArray());
        if (!string.IsNullOrWhiteSpace(email.ReplyTo))
            message["replyTo"] = new JsonArray(new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = email.ReplyTo } });
        if (email.Headers.Count > 0)
        {
            message["internetMessageHeaders"] = new JsonArray(email.Headers.Select(h => (JsonNode)new JsonObject { ["name"] = h.Key, ["value"] = h.Value }).ToArray());
        }
        if (email.Attachments.Count > 0)
        {
            message["attachments"] = new JsonArray(email.Attachments.Select(a => (JsonNode)new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.fileAttachment",
                ["name"] = a.FileName,
                ["contentType"] = a.ContentType,
                ["contentBytes"] = Convert.ToBase64String(a.Content),
                ["isInline"] = a.IsInline,
                ["contentId"] = a.IsInline ? a.ContentId : null
            }).ToArray());
        }

        var payload = new JsonObject { ["message"] = message, ["saveToSentItems"] = false };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/users/{Uri.EscapeDataString(email.FromAddress)}/sendMail")
        {
            Content = new StringContent(payload.ToJsonString(JsonOpts), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var res = await client.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.IsSuccessStatusCode)
        {
            var reqId = res.Headers.TryGetValues("request-id", out var ids) ? ids.FirstOrDefault() : null;
            return TransportResult.Ok(reqId, $"HTTP {(int)res.StatusCode}");
        }
        if (res.StatusCode == HttpStatusCode.Unauthorized) _graphTokens.TryRemove(profile.Id, out _);
        return HttpFailure("Microsoft Graph", res.StatusCode, body);
    }

    private static JsonNode ToGraph(Models.EmailAddressDto a)
        => new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = a.Address, ["name"] = string.IsNullOrWhiteSpace(a.Name) ? null : a.Name } };

    private async Task<string> GetGraphTokenAsync(SenderProfile profile, HttpClient client, CancellationToken ct)
    {
        if (_graphTokens.TryGetValue(profile.Id, out var cached) && cached.Expires > DateTime.UtcNow.AddMinutes(2))
            return cached.Token;

        if (string.IsNullOrWhiteSpace(profile.OAuthTenantId) || string.IsNullOrWhiteSpace(profile.OAuthClientId))
            throw new InvalidOperationException("Microsoft Graph profile requires OAuthTenantId, OAuthClientId and OAuthClientSecret");

        var secret = _secrets.Unprotect(profile.OAuthClientSecretProtected)
                     ?? throw new InvalidOperationException("OAuth client secret is missing or cannot be decrypted");

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = profile.OAuthClientId,
            ["client_secret"] = secret,
            ["scope"] = "https://graph.microsoft.com/.default",
            ["grant_type"] = "client_credentials"
        });

        using var res = await client.PostAsync($"https://login.microsoftonline.com/{profile.OAuthTenantId}/oauth2/v2.0/token", form, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Token request failed ({(int)res.StatusCode}): {body}", null, res.StatusCode);

        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("No access_token in response");
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        _graphTokens[profile.Id] = (token, DateTime.UtcNow.AddSeconds(expiresIn));
        return token;
    }

    // ---------------- Generic ----------------
    private async Task<TransportResult> GenericAsync(SenderProfile profile, OutboundEmail email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.ApiEndpoint))
            return TransportResult.Fail("API endpoint is not configured on the sender profile", false);

        var payload = new
        {
            id = email.Id,
            from = new { address = email.FromAddress, name = email.FromName },
            replyTo = email.ReplyTo,
            to = email.To,
            cc = email.Cc,
            bcc = email.Bcc,
            subject = email.Subject,
            text = email.Text,
            html = email.Html,
            headers = email.Headers,
            tags = email.Tags,
            attachments = email.Attachments.Select(a => new
            {
                fileName = a.FileName,
                contentType = a.ContentType,
                contentId = a.ContentId,
                isInline = a.IsInline,
                contentBase64 = Convert.ToBase64String(a.Content)
            })
        };

        var client = CreateClient(profile);
        using var req = new HttpRequestMessage(HttpMethod.Post, profile.ApiEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json")
        };
        var key = _secrets.Unprotect(profile.ApiKeyProtected);
        if (!string.IsNullOrEmpty(key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        ApplyExtraHeaders(req, profile);

        using var res = await client.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.IsSuccessStatusCode)
        {
            var id = TryReadJsonString(body, "id") ?? TryReadJsonString(body, "messageId") ?? TryReadJsonString(body, "message_id");
            return TransportResult.Ok(id, $"HTTP {(int)res.StatusCode} {body}");
        }
        return HttpFailure("Endpoint", res.StatusCode, body);
    }

    // ---------------- helpers ----------------
    private HttpClient CreateClient(SenderProfile profile)
    {
        var client = _httpFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, profile.TimeoutSeconds));
        return client;
    }

    private string ApiKey(SenderProfile profile)
        => _secrets.Unprotect(profile.ApiKeyProtected) ?? throw new InvalidOperationException("API key is missing or cannot be decrypted");

    private static AuthenticationHeaderValue BasicAuth(string user, string password)
        => new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    private static string FormatAddress(string address, string? name)
        => string.IsNullOrWhiteSpace(name) ? address : $"\"{name.Replace("\"", "'")}\" <{address}>";

    private static void ApplyExtraHeaders(HttpRequestMessage req, SenderProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.ExtraHeadersJson)) return;
        try
        {
            var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(profile.ExtraHeadersJson);
            if (headers == null) return;
            foreach (var (k, v) in headers)
            {
                if (k.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    req.Headers.Authorization = AuthenticationHeaderValue.Parse(v);
                else
                    req.Headers.TryAddWithoutValidation(k, v);
            }
        }
        catch (Exception)
        {
            // invalid JSON in ExtraHeaders is ignored; validated on save by the admin API
        }
    }

    private static string? TryReadJsonString(string body, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String)
                return p.GetString();
        }
        catch
        {
            // not JSON
        }
        return null;
    }

    private static TransportResult HttpFailure(string provider, HttpStatusCode status, string body)
    {
        var code = (int)status;
        var retryable = code == 408 || code == 429 || code >= 500;
        return TransportResult.Fail($"{provider} returned HTTP {code}", retryable, body);
    }

    private TransportResult Classify(Exception ex)
    {
        switch (ex)
        {
            case TaskCanceledException:
            case OperationCanceledException:
                return TransportResult.Fail("HTTP request timed out", retryable: true);
            case HttpRequestException http:
                return TransportResult.Fail("HTTP error: " + http.Message, retryable: true);
            case InvalidOperationException invalid:
                return TransportResult.Fail(invalid.Message, retryable: false);
            default:
                _logger.LogWarning(ex, "Unexpected HTTP API failure");
                return TransportResult.Fail(ex.GetType().Name + ": " + ex.Message, retryable: true);
        }
    }
}
