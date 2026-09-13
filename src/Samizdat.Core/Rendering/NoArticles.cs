namespace Samizdat.Core.Rendering;

/// Список статей для гостя: любая вики-ссылка становится текстом, ни один чужой slug не утекает.
public sealed class NoArticles : IArticleLookup
{
    public static readonly IArticleLookup Instance = new NoArticles();

    NoArticles() { }

    public bool Exists(string slug) => false;
}
