using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Endpoints;

/// Поля обеих форм регистрации и их проверка. Форма одна на приглашение и на открытую запись:
/// правила у них общие, разная только судьба заведённой строки.
public readonly record struct RegistrationForm(string Login, string Password, string Repeat)
{
    public const int MaxLoginLength = 100;

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
        { Login.Length: > MaxLoginLength } => $"Логин длиннее {MaxLoginLength} знаков.",
        { Password.Length: < SettingsPage.MinPasswordLength } =>
            $"Пароль должен быть не короче {SettingsPage.MinPasswordLength} знаков.",
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
