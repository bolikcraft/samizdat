using Samizdat.Server.Storage;

namespace Samizdat.Server.Data;

public sealed class SiteSettings(SamizdatDbContext db, IConfiguration configuration, BackgroundFile background)
{
    public string ThemeName => Get("theme.name", configuration["Samizdat:Theme"] ?? "default");
    public string ColorScheme => Get("theme.color_scheme", configuration["Samizdat:ColorScheme"] ?? "system");
    public string BackgroundFileName => Get("theme.background", "");

    /// В адрес идут только цифры версии, имя файла в шаблон не попадает — экранировать нечего.
    public string? BackgroundUrl
        => background.Version(BackgroundFileName) is { Length: > 0 } version ? $"/background?v={version}" : null;

    /// Всё, что вид страницы берёт из настроек. Задумана как часть ключа кэша страниц:
    /// layout.html рендерится внутри закэшированной страницы, и без этого отпечатка смена
    /// схемы или фона была бы не видна на статье, отрендеренной раньше.
    public string ViewFingerprint => $"{ThemeName}|{ColorScheme}|{background.Version(BackgroundFileName)}";

    public string Get(string key, string fallback)
        => db.Settings.Find(key)?.Value is { Length: > 0 } value ? value : fallback;

    public void Set(string key, string value)
    {
        var row = db.Settings.Find(key);
        if (row is null) db.Settings.Add(new SettingRow { Key = key, Value = value });
        else row.Value = value;
        db.SaveChanges();
    }
}
