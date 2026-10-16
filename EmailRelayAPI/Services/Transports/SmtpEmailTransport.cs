using System.Net.Sockets;
using EmailRelayAPI.Data.Entities;
using EmailRelayAPI.Security;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;

namespace EmailRelayAPI.Services.Transports;

/// <summary>Delivers mail through an SMTP server using MailKit.</summary>
public class SmtpEmailTransport : IEmailTransport
{
    private readonly ISecretProtector _secrets;
    private readonly ILogger<SmtpEmailTransport> _logger;

    public SmtpEmailTransport(ISecretProtector secrets, ILogger<SmtpEmailTransport> logger)
    {
        _secrets = secrets;
        _logger = logger;
    }

    public TransportType Type => TransportType.Smtp;

    public async Task<TransportResult> SendAsync(SenderProfile profile, OutboundEmail email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.SmtpHost))
            return TransportResult.Fail("SMTP host is not configured on the sender profile", retryable: false);

        MimeMessage message;
        try
        {
            message = BuildMessage(profile, email);
        }
        catch (Exception ex)
        {
            return TransportResult.Fail("Failed to build MIME message: " + ex.Message, retryable: false);
        }

        try
        {
            using var client = CreateClient(profile);
            await ConnectAndAuthenticateAsync(client, profile, ct);
            var response = await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
            return TransportResult.Ok(message.MessageId, response);
        }
        catch (Exception ex)
        {
            return Classify(ex);
        }
    }

    public async Task<TransportResult> TestAsync(SenderProfile profile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.SmtpHost))
            return TransportResult.Fail("SMTP host is not configured", retryable: false);

        try
        {
            using var client = CreateClient(profile);
            await ConnectAndAuthenticateAsync(client, profile, ct);
            await client.NoOpAsync(ct);
            var caps = client.Capabilities.ToString();
            var secure = client.IsSecure;
            var authenticated = client.IsAuthenticated;
            await client.DisconnectAsync(true, ct);
            return TransportResult.Ok(null, $"Connected to {profile.SmtpHost}:{profile.SmtpPort} (secure={secure}, auth={authenticated}); capabilities: {caps}");
        }
        catch (Exception ex)
        {
            return Classify(ex);
        }
    }

    private SmtpClient CreateClient(SenderProfile profile)
    {
        var client = new SmtpClient
        {
            Timeout = Math.Max(5, profile.TimeoutSeconds) * 1000
        };
        if (profile.SkipCertificateValidation)
        {
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        }
        if (!string.IsNullOrWhiteSpace(profile.SmtpLocalDomain))
        {
            client.LocalDomain = profile.SmtpLocalDomain;
        }
        return client;
    }

    private async Task ConnectAndAuthenticateAsync(SmtpClient client, SenderProfile profile, CancellationToken ct)
    {
        var security = profile.SmtpSecurity switch
        {
            SmtpSecurity.None => SecureSocketOptions.None,
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.StartTlsWhenAvailable => SecureSocketOptions.StartTlsWhenAvailable,
            _ => SecureSocketOptions.Auto
        };

        await client.ConnectAsync(profile.SmtpHost, profile.SmtpPort, security, ct);

        if (!string.IsNullOrWhiteSpace(profile.SmtpUsername))
        {
            var password = _secrets.Unprotect(profile.SmtpPasswordProtected) ?? string.Empty;
            await client.AuthenticateAsync(profile.SmtpUsername, password, ct);
        }
    }

    private static MimeMessage BuildMessage(SenderProfile profile, OutboundEmail email)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(email.FromName ?? string.Empty, email.FromAddress));
        if (!string.IsNullOrWhiteSpace(email.ReplyTo))
            message.ReplyTo.Add(MailboxAddress.Parse(email.ReplyTo));

        foreach (var a in email.To) message.To.Add(new MailboxAddress(a.Name ?? string.Empty, a.Address));
        foreach (var a in email.Cc) message.Cc.Add(new MailboxAddress(a.Name ?? string.Empty, a.Address));
        foreach (var a in email.Bcc) message.Bcc.Add(new MailboxAddress(a.Name ?? string.Empty, a.Address));

        message.Subject = email.Subject;

        var domain = email.FromAddress.Contains('@') ? email.FromAddress[(email.FromAddress.IndexOf('@') + 1)..] : "emailrelay.local";
        message.MessageId = MimeUtils.GenerateMessageId(domain);

        var builder = new BodyBuilder();
        if (!string.IsNullOrEmpty(email.Text)) builder.TextBody = email.Text;
        if (!string.IsNullOrEmpty(email.Html)) builder.HtmlBody = email.Html;
        if (string.IsNullOrEmpty(email.Text) && string.IsNullOrEmpty(email.Html)) builder.TextBody = string.Empty;

        foreach (var att in email.Attachments)
        {
            var contentType = ContentType.TryParse(att.ContentType, out var parsed) ? parsed : new ContentType("application", "octet-stream");
            if (att.IsInline && !string.IsNullOrEmpty(att.ContentId))
            {
                var entity = builder.LinkedResources.Add(att.FileName, att.Content, contentType);
                entity.ContentId = att.ContentId;
            }
            else
            {
                builder.Attachments.Add(att.FileName, att.Content, contentType);
            }
        }

        message.Body = builder.ToMessageBody();

        foreach (var (key, value) in email.Headers)
        {
            message.Headers.Add(key, value);
        }

        message.Headers.Add("X-EmailRelay-Id", email.Id.ToString());
        return message;
    }

    private TransportResult Classify(Exception ex)
    {
        switch (ex)
        {
            case SmtpCommandException cmd:
            {
                var code = (int)cmd.StatusCode;
                var retryable = code >= 400 && code < 500;
                return TransportResult.Fail($"SMTP {code} ({cmd.ErrorCode}): {cmd.Message}", retryable, cmd.Message);
            }
            case AuthenticationException auth:
                return TransportResult.Fail("SMTP authentication failed: " + auth.Message, retryable: false);
            case SmtpProtocolException proto:
                return TransportResult.Fail("SMTP protocol error: " + proto.Message, retryable: true);
            case SocketException sock:
                return TransportResult.Fail("Network error: " + sock.Message, retryable: true);
            case OperationCanceledException:
                return TransportResult.Fail("SMTP operation timed out or was cancelled", retryable: true);
            case IOException io:
                return TransportResult.Fail("I/O error: " + io.Message, retryable: true);
            default:
                _logger.LogWarning(ex, "Unexpected SMTP failure");
                return TransportResult.Fail(ex.GetType().Name + ": " + ex.Message, retryable: true);
        }
    }
}
