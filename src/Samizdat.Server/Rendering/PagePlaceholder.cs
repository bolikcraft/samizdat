using System.Security.Cryptography;

namespace Samizdat.Server.Rendering;

/// Метки в странице из кэша, которые заменяются при каждой отдаче. Случайная часть создаётся
/// на запуск процесса: текст статьи её не знает и не может подставить метку сам.
public static class PagePlaceholder
{
    public static string Nonce { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public static string Of(string name) => $"__{name}_{Nonce}__";
}
