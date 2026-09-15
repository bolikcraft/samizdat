namespace Samizdat.Core.Rendering;

/// Знает, какие статьи лежат на сервере. Нужен, чтобы решить судьбу [[ссылки]].
public interface IArticleLookup
{
    /// slug статьи, на которую ведёт цель, или null.
    string? Resolve(string target);
}
