using System.Security.Cryptography;
using System.Text;
using EmailRelayAPI.Options;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Security;

public interface IRequestSignatureVerifier
{
    /// <summary>
    /// Verifies "X-Timestamp" + "X-Signature" against HMAC-SHA256(secret, "{timestamp}.{METHOD}.{path}.{sha256(body)}").
    /// Returns null when valid, otherwise a human readable reason.
    /// </summary>
    Task<string?> VerifyAsync(HttpRequest request, string signingSecret, CancellationToken ct = default);
}

public class RequestSignatureVerifier : IRequestSignatureVerifier
{
    public const string TimestampHeader = "X-Timestamp";
    public const string SignatureHeader = "X-Signature";
    private readonly IOptionsMonitor<SecurityOptions> _options;

    public RequestSignatureVerifier(IOptionsMonitor<SecurityOptions> options)
    {
        _options = options;
    }

    public async Task<string?> VerifyAsync(HttpRequest request, string signingSecret, CancellationToken ct = default)
    {
        var tsHeader = request.Headers[TimestampHeader].ToString();
        var sigHeader = request.Headers[SignatureHeader].ToString();
        if (string.IsNullOrEmpty(tsHeader) || string.IsNullOrEmpty(sigHeader))
            return $"Signed request required: missing {TimestampHeader} or {SignatureHeader} header";

        if (!long.TryParse(tsHeader, out var ts))
            return $"{TimestampHeader} must be a unix timestamp in seconds";

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - ts) > _options.CurrentValue.SignatureToleranceSeconds)
            return "Request timestamp outside the allowed window (check clock skew)";

        var bodyHash = await HashBodyAsync(request, ct);
        var canonical = $"{ts}.{request.Method.ToUpperInvariant()}.{request.Path.Value}.{bodyHash}";
        var expected = Compute(signingSecret, canonical);

        var provided = sigHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase) ? sigHeader[7..] : sigHeader;
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(provided.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(a, b) ? null : "Invalid request signature";
    }

    public static string Compute(string secret, string canonical)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static async Task<string> HashBodyAsync(HttpRequest request, CancellationToken ct)
    {
        request.EnableBuffering();
        request.Body.Position = 0;
        var hash = await SHA256.HashDataAsync(request.Body, ct);
        request.Body.Position = 0;
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
