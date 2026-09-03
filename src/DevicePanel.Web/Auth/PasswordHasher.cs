using System.Security.Cryptography;

namespace DevicePanel.Web.Auth;

/// <summary>
/// 密码哈希：PBKDF2-SHA256，随机 16 字节盐、32 字节派生值。
/// 存储格式：pbkdf2-sha256:{iterations}:{saltBase64}:{hashBase64}，不落明文。
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string encodedHash);
}

public sealed class PasswordHasher : IPasswordHasher
{
    public const string AlgorithmId = "pbkdf2-sha256";
    public const int DefaultIterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public string Hash(string password)
    {
        return Hash(password, RandomNumberGenerator.GetBytes(SaltSize), DefaultIterations);
    }

    public bool Verify(string password, string encodedHash)
    {
        var parts = encodedHash.Split(':');
        if (parts.Length != 4 || parts[0] != AlgorithmId)
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string Hash(string password, byte[] salt, int iterations)
    {
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{AlgorithmId}:{iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }
}
