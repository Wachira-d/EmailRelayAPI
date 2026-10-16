using Microsoft.AspNetCore.DataProtection;

namespace EmailRelayAPI.Security;

/// <summary>Encrypts secrets (SMTP passwords, API keys, signing secrets) at rest using ASP.NET Data Protection.</summary>
public interface ISecretProtector
{
    string? Protect(string? plain);
    string? Unprotect(string? protectedValue);
}

public class SecretProtector : ISecretProtector
{
    private readonly IDataProtector _protector;
    private readonly ILogger<SecretProtector> _logger;

    public SecretProtector(IDataProtectionProvider provider, ILogger<SecretProtector> logger)
    {
        _protector = provider.CreateProtector("EmailRelayAPI.Secrets.v1");
        _logger = logger;
    }

    public string? Protect(string? plain)
        => string.IsNullOrEmpty(plain) ? null : _protector.Protect(plain);

    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unprotect a stored secret. The Data Protection key ring may have changed.");
            return null;
        }
    }
}
