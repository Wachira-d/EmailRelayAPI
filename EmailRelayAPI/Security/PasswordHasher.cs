using System.Security.Cryptography;
using System.Text;

namespace EmailRelayAPI.Security;

/// <summary>PBKDF2-SHA256 password hashing for the admin user cache and the local break-glass admin.</summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string GenerateSalt()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltSize));

    public static string Hash(string password, string saltBase64, int iterations)
    {
        var salt = Convert.FromBase64String(saltBase64);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashSize);
        return Convert.ToBase64String(hash);
    }

    public static bool Verify(string password, string saltBase64, int iterations, string expectedHashBase64)
    {
        try
        {
            var actual = Convert.FromBase64String(Hash(password, saltBase64, iterations));
            var expected = Convert.FromBase64String(expectedHashBase64);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Self-contained format "pbkdf2$iterations$salt$hash" used in appsettings for the local admin.</summary>
    public static string HashToString(string password, int iterations = DefaultIterations)
    {
        var salt = GenerateSalt();
        return $"pbkdf2${iterations}${salt}${Hash(password, salt, iterations)}";
    }

    public static bool VerifyString(string password, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        if (!int.TryParse(parts[1], out var iterations)) return false;
        return Verify(password, parts[2], iterations, parts[3]);
    }
}
