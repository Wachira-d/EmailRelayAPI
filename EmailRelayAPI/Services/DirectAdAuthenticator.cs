using System.DirectoryServices.Protocols;
using System.Net;
using System.Text.RegularExpressions;
using EmailRelayAPI.Options;
using Microsoft.Extensions.Options;

namespace EmailRelayAPI.Services;

public enum DirectAdOutcome
{
    /// <summary>Bind succeeded: the password is correct right now.</summary>
    Success,

    /// <summary>LDAP 49 for a known UPN: wrong password, locked, disabled, expired.</summary>
    InvalidCredentials,

    /// <summary>Server down, connect error, timeout, TLS problem: go to the offline cache.</summary>
    Unavailable,

    /// <summary>Direct AD fallback is not configured.</summary>
    Disabled
}

public sealed record DirectAdResult(DirectAdOutcome Outcome, string Detail);

public interface IDirectAdAuthenticator
{
    bool IsEnabled { get; }

    /// <summary>One LDAPS bind with the user's UPN (guide section 7). Never retried, never with other name formats.</summary>
    Task<DirectAdResult> BindAsync(string userPrincipalName, string password, CancellationToken ct = default);
}

public partial class DirectAdAuthenticator : IDirectAdAuthenticator
{
    private readonly IOptionsMonitor<AuthenApiOptions> _options;

    public DirectAdAuthenticator(IOptionsMonitor<AuthenApiOptions> options) => _options = options;

    public bool IsEnabled
    {
        get
        {
            var d = _options.CurrentValue.DirectAd;
            return d.Enabled && !string.IsNullOrWhiteSpace(d.Server);
        }
    }

    [GeneratedRegex(@"data\s+([0-9a-fA-F]{3,4})")]
    private static partial Regex AdSubCode();

    public Task<DirectAdResult> BindAsync(string userPrincipalName, string password, CancellationToken ct = default)
    {
        if (!IsEnabled) return Task.FromResult(new DirectAdResult(DirectAdOutcome.Disabled, "Direct AD fallback is disabled"));
        var d = _options.CurrentValue.DirectAd;

        // System.DirectoryServices.Protocols is synchronous; keep it off the request thread.
        return Task.Run(() =>
        {
            try
            {
                using var connection = new LdapConnection(new LdapDirectoryIdentifier(d.Server.Trim(), d.Port, fullyQualifiedDnsHostName: true, connectionless: false))
                {
                    AuthType = AuthType.Basic,
                    Timeout = TimeSpan.FromSeconds(Math.Max(1, d.TimeoutSeconds)),
                    Credential = new NetworkCredential(userPrincipalName, password)
                };
                connection.SessionOptions.ProtocolVersion = 3;
                connection.SessionOptions.SecureSocketLayer = true; // LDAPS; certificate validation stays on
                connection.Bind();
                return new DirectAdResult(DirectAdOutcome.Success, $"LDAPS bind to {d.Server} succeeded");
            }
            catch (LdapException ex) when (ex.ErrorCode == 49)
            {
                var sub = AdSubCode().Match(ex.ServerErrorMessage ?? string.Empty);
                var code = sub.Success ? sub.Groups[1].Value.ToLowerInvariant() : "?";
                var meaning = code switch
                {
                    "52e" => "invalid credentials",
                    "775" => "account locked",
                    "533" => "account disabled",
                    "701" => "account expired",
                    "532" => "password expired",
                    "773" => "password must be changed",
                    "525" => "user not found",
                    _ => "invalid credentials"
                };
                return new DirectAdResult(DirectAdOutcome.InvalidCredentials, $"LDAP 49 ({code}: {meaning})");
            }
            catch (LdapException ex)
            {
                return new DirectAdResult(DirectAdOutcome.Unavailable, $"LDAP error {ex.ErrorCode}: {ex.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new DirectAdResult(DirectAdOutcome.Unavailable, $"{ex.GetType().Name}: {ex.Message}");
            }
        }, ct);
    }
}
