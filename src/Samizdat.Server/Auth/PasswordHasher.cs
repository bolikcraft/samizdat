using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Samizdat.Server.Auth;

public static class PasswordHasher
{
    const int SaltSize = 16, HashSize = 32, Iterations = 3, MemoryKb = 65536, Threads = 2;

    /// Хэш случайного пароля, который никто не знает. С ним сверяют пароль, когда логина нет:
    /// так ответ идёт столько же, сколько с настоящим логином.
    public static readonly string Decoy = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltSize)));

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(password, salt);
        return $"argon2id${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        // Konscious.Argon2id требует непустой пароль и иначе бросает исключение — пустой пароль
        // просто не подходит, без хэширования.
        if (password.Length == 0) return false;

        var parts = stored.Split('$');
        if (parts.Length != 3 || parts[0] != "argon2id") return false;

        // Строка в базе могла испортиться (обрезка, ручная правка) — мусор здесь означает "не подошло",
        // а не 500.
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

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
