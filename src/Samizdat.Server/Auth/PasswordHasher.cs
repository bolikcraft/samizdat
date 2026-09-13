using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Samizdat.Server.Auth;

public static class PasswordHasher
{
    const int SaltSize = 16, HashSize = 32, Iterations = 3, MemoryKb = 65536, Threads = 2;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(password, salt);
        return $"argon2id${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 3 || parts[0] != "argon2id") return false;

        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        return CryptographicOperations.FixedTimeEquals(Derive(password, salt), expected);
    }

    static byte[] Derive(string password, byte[] salt)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            Iterations = Iterations,
            MemorySize = MemoryKb,
            DegreeOfParallelism = Threads,
        };
        return argon.GetBytes(HashSize);
    }
}
