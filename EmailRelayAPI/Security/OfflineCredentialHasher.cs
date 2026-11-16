using System.Security.Cryptography;
using System.Text;

namespace EmailRelayAPI.Security;

/// <summary>
/// Offline credential hashing per the AuthenAPI integration guide: HMAC-SHA256 with a pepper kept outside the
/// database, then PBKDF2-SHA256 with 600,000 iterations and a random per-user salt.
/// Format: "v2$iterations$saltBase64$hashBase64".
/// </summary>
public static class OfflineCredentialHasher
{
    public const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string Hash(string password, string pepper, int iterations = Iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(password, pepper, salt, iterations);
        return $"v2${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string pepper, string? stored)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(pepper)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "v2" || !int.TryParse(parts[1], out var iterations) || iterations < 100_000) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Derive(password, pepper, salt, iterations);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Derive(string password, string pepper, byte[] salt, int iterations)
    {
        var peppered = HMACSHA256.HashData(Encoding.UTF8.GetBytes(pepper), Encoding.UTF8.GetBytes(password));
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(peppered, salt, iterations, HashAlgorithmName.SHA256, HashSize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(peppered);
        }
    }
}
