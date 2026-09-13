namespace Samizdat.Core.Themes;

public interface IThemeSource
{
    /// Путь всегда с прямыми слэшами: "article.html", "assets/style.css". null — файла нет.
    string? ReadText(string path);
    Stream? OpenRead(string path);

    /// Меняется при любой правке темы. Входит в ключ кэша страниц.
    string Version { get; }
}
