using Samizdat.Core.Localization;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Endpoints;

/// Поля обеих форм регистрации и их проверка. Форма одна на приглашение и на открытую запись:
/// правила у них общие, разная только судьба заведённой строки.
internal readonly record struct RegistrationForm(string Login, string Password, string Repeat)
{
    public static async Task<RegistrationForm> Read(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync();
        return new(form["login"].ToString().Trim(),
                   form["password"].ToString(),
                   form["repeat"].ToString());
    }

    /// null — поля в порядке. Иначе готовая строка для формы.
    public string? Fault(Translator text) => this switch
    {
        { Login.Length: 0 } => text["register.err.empty_login"],
        { Login.Length: > SettingsPage.MaxLoginLength } =>
            text.Format("register.err.long_login", SettingsPage.MaxLoginLength),
        { Password.Length: < SettingsPage.MinPasswordLength } =>
            text.Format("register.err.short_password", SettingsPage.MinPasswordLength),
        { Password.Length: > SettingsPage.MaxPasswordLength } =>
            text.Format("register.err.long_password", SettingsPage.MaxPasswordLength),
        _ when Password != Repeat => text["register.err.mismatch"],
        _ => null,
    };

    public UserRow ToReader(DateTimeOffset now, bool approved) => new()
    {
        Login = Login,
        PasswordHash = PasswordHasher.Hash(Password),
        Role = UserRole.Reader,
        CreatedAt = now,
        ApprovedAt = approved ? now : null,
    };
}
