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
    public string? Fault() => this switch
    {
        { Login.Length: 0 } => "Логин не должен быть пустым.",
        { Login.Length: > SettingsPage.MaxLoginLength } =>
            $"Логин длиннее {SettingsPage.MaxLoginLength} символов.",
        { Password.Length: < SettingsPage.MinPasswordLength } =>
            $"Пароль должен быть не короче {SettingsPage.MinPasswordLength} символов.",
        // Верхняя граница есть у хэша: Argon2id считает по всей строке, и мегабайт пароля
        // занял бы процессор надолго.
        { Password.Length: > 200 } => "Пароль длиннее 200 символов.",
        _ when Password != Repeat => "Пароль и повтор не совпадают.",
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
