using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Models;

namespace EmailRelayAPI.Services.Transports;

/// <summary>Fully resolved message ready to be handed to a transport.</summary>
public sealed class OutboundEmail
{
    public Guid Id { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string? FromName { get; set; }
    public string? ReplyTo { get; set; }
    public List<EmailAddressDto> To { get; set; } = new();
    public List<EmailAddressDto> Cc { get; set; } = new();
    public List<EmailAddressDto> Bcc { get; set; } = new();
    public string Subject { get; set; } = string.Empty;
    public string? Text { get; set; }
    public string? Html { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public List<OutboundAttachment> Attachments { get; set; } = new();
    public List<string> Tags { get; set; } = new();
}

public sealed class OutboundAttachment
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public string? ContentId { get; set; }
    public bool IsInline { get; set; }
}

public sealed record TransportResult(bool Success, string? ProviderMessageId, string? Response, string? Error, bool Retryable)
{
    public static TransportResult Ok(string? providerMessageId, string? response = null) => new(true, providerMessageId, Truncate(response), null, false);
    public static TransportResult Fail(string error, bool retryable, string? response = null) => new(false, null, Truncate(response), Truncate(error), retryable);

    private static string? Truncate(string? s) => s == null ? null : (s.Length > 2000 ? s[..2000] : s);
}

public interface IEmailTransport
{
    TransportType Type { get; }
    Task<TransportResult> SendAsync(SenderProfile profile, OutboundEmail email, CancellationToken ct);

    /// <summary>Connectivity / credential test without sending a message.</summary>
    Task<TransportResult> TestAsync(SenderProfile profile, CancellationToken ct);
}

public interface IEmailTransportResolver
{
    IEmailTransport Resolve(SenderProfile profile);
}

public class EmailTransportResolver : IEmailTransportResolver
{
    private readonly IEnumerable<IEmailTransport> _transports;

    public EmailTransportResolver(IEnumerable<IEmailTransport> transports) => _transports = transports;

    public IEmailTransport Resolve(SenderProfile profile)
        => _transports.FirstOrDefault(t => t.Type == profile.Type)
           ?? throw new InvalidOperationException($"No transport registered for {profile.Type}");
}
