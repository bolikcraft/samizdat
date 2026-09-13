namespace Samizdat.Core.Rendering;

/// Знает, какие статьи лежат на сервере. Нужен, чтобы решить судьбу [[ссылки]].
public interface IArticleLookup
{
    bool Exists(string slug);
}
