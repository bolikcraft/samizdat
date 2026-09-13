namespace Samizdat.Server.Data;

public sealed class SiteSettings(SamizdatDbContext db, IConfiguration configuration)
{
    public string ThemeName => Get("theme.name", configuration["Samizdat:Theme"] ?? "default");
    public string ColorScheme => Get("theme.color_scheme", configuration["Samizdat:ColorScheme"] ?? "system");

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
