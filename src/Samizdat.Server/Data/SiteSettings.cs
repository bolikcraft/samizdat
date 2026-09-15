using Samizdat.Server.Auth;
using Samizdat.Server.Storage;

namespace Samizdat.Server.Data;

public enum BackgroundKind { None, Upload, Preset, Color }

public readonly record struct BackgroundChoice(BackgroundKind Kind, string Value);

public sealed class SiteSettings(SamizdatDbContext db, IConfiguration configuration, BackgroundFile background)
{
    public const string PresetPrefix = "preset:";
    public const string ColorPrefix = "color:";

    public string ThemeName => Get("theme.name", configuration["Samizdat:Theme"] ?? "default");
    public string ColorScheme => Get("theme.color_scheme", configuration["Samizdat:ColorScheme"] ?? "system");

    /// Открытая регистрация. По умолчанию выключена: сайт личный, и пускать к нему кого попало
    /// владелец должен решить сам.
    public bool OpenRegistration => Get("auth.open_registration", "false") == "true";

    /// Одна настройка на все виды фона: пусто — фона нет, `preset:` — картинка из набора темы,
    /// `color:` — цвет из палитры, всё остальное — имя загруженного файла (так писала первая версия).
    public BackgroundChoice Background => Parse(Get("theme.background", ""));

    public string BackgroundFileName
        => Background is { Kind: BackgroundKind.Upload, Value: var name } ? name : "";

    /// В адрес загруженной картинки идут только цифры версии, имя файла в шаблон не попадает.
    /// Имя картинки из набора уже проверено каталогом темы — в адресе оно безопасно.
    public string? BackgroundUrl => Background switch
    {
        { Kind: BackgroundKind.Upload } => background.Version(BackgroundFileName) is { Length: > 0 } version
            ? $"/background?v={version}"
            : null,
        { Kind: BackgroundKind.Preset, Value: var file } => $"/assets/backgrounds/{file}",
        _ => null,
    };

    public string? BackgroundColor
        => Background is { Kind: BackgroundKind.Color, Value: var color } ? color : null;

    /// Скачивание статьи читателем и гостем по ссылке. Владельца тут нет: он качает всегда.
    public DownloadPolicy Download => new(Get("articles.download.readers", "") == "on",
                                          Get("articles.download.guests", "") == "on");

    /// Всё, что вид страницы берёт из настроек. Задумана как часть ключа кэша страниц:
    /// layout.html рендерится внутри закэшированной страницы, и без этого отпечатка смена
    /// схемы, фона или разрешения скачивать была бы не видна на статье, отрендеренной раньше.
    public string ViewFingerprint
        => $"{ThemeName}|{ColorScheme}|{Get("theme.background", "")}|{background.Version(BackgroundFileName)}"
           + $"|{Download.Readers}|{Download.Guests}";

    public string Get(string key, string fallback)
        => db.Settings.Find(key)?.Value is { Length: > 0 } value ? value : fallback;

    public void Set(string key, string value)
    {
        var row = db.Settings.Find(key);
        if (row is null) db.Settings.Add(new SettingRow { Key = key, Value = value });
        else row.Value = value;
        db.SaveChanges();
    }

    static BackgroundChoice Parse(string raw) => raw switch
    {
        "" => new(BackgroundKind.None, ""),
        _ when raw.StartsWith(PresetPrefix, StringComparison.Ordinal)
            => new(BackgroundKind.Preset, raw[PresetPrefix.Length..]),
        _ when raw.StartsWith(ColorPrefix, StringComparison.Ordinal)
            => new(BackgroundKind.Color, raw[ColorPrefix.Length..]),
        _ => new(BackgroundKind.Upload, raw),
    };
}
