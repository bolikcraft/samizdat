using System.Text.Json;

namespace Samizdat.Core.Localization;

public sealed class LanguageException(string message) : Exception(message);

public interface ILanguageSource
{
    /// Метка содержимого: по ней кэш понимает, что пакеты пора перечитать.
    string Version { get; }

    /// Пакет целиком, плоским словарём точечных ключей. null — такого языка тут нет.
    IReadOnlyDictionary<string, string>? Read(string code);

    IReadOnlyList<string> Codes();
}

static class LanguagePack
{
    public static IReadOnlyDictionary<string, string> Parse(string json, string name)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? throw new LanguageException($"Пакет {name} пуст");
        }
        catch (JsonException error)
        {
            throw new LanguageException($"Пакет {name}: {error.Message}");
        }
    }
}
