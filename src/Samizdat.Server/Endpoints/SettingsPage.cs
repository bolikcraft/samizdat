using System.Security.Claims;
using Samizdat.Server.Data;

namespace Samizdat.Server.Endpoints;

/// Общее у разделов страницы настроек: итог действия, раздел, которому он принадлежит, и текст
/// сообщения. Разделы живут в разных файлах, а механизм у них один.
public static class SettingsPage
{
    /// Одна мера на все места, где заводят пароль: свой, чужой, нового человека и свой при
    /// регистрации.
    public const int MinPasswordLength = 8;

    /// Маршрут только для владельца. Роль стоит на маршрутах, а не на группе: читателю нужен
    /// вход в настройки ради своего пароля.
    public static RouteHandlerBuilder OwnerOnly(this RouteHandlerBuilder builder)
        => builder.RequireAuthorization(policy => policy.RequireRole(nameof(UserRole.Owner)));

    /// Возврат на страницу настроек с итогом действия: код итога в адресе, раздел — в якоре.
    // Якорем страница выбирает раздел: без него открылся бы первый, а не тот, где нажали кнопку.
    public static IResult Ok(string code) => Results.Redirect($"/settings?ok={code}#{SectionOf(code)}");
    public static IResult Err(string code) => Results.Redirect($"/settings?err={code}#{SectionOf(code)}");

    public static IResult LoggedOut() => Results.Redirect("/login");

    /// null — человека в базе уже нет: его удалили, пока запрос шёл. Следующий запрос он же
    /// и последний: проверка cookie погасит сессию.
    public static UserRow? CurrentUser(SamizdatDbContext db, ClaimsPrincipal user)
        => db.Users.FirstOrDefault(row => row.Login == user.Identity!.Name);

    /// Раздел, которому принадлежит итог действия.
    // Разделы переключаются якорем, без перезагрузки, поэтому сообщение стоит внутри своего
    // раздела: одно общее над разделами оставалось висеть над чужой формой. Якорь возврата и
    // место сообщения берутся отсюда оба — иначе сообщение попадало бы в скрытый раздел.
    // Незнакомый код уходит в первый раздел: его же показывает страница без якоря.
    public static string SectionOf(string code) => code switch
    {
        "password" or "wrong_password" or "short_password" or "password_mismatch" => "security",
        "token_created" or "token_note" or "token_revoked" => "tokens",
        "link_revoked" => "links",
        "person_added" or "person_password" or "person_deleted" => "people",
        "bad_person" or "person_short_password" or "login_taken" => "people",
        "last_owner" or "self_delete" or "other_owner" or "own_password" => "people",
        _ => "appearance",
    };

    public static string? Message(string? ok, string? err) => err switch
    {
        "wrong_password" => "Неверный текущий пароль.",
        "short_password" => $"Новый пароль должен быть не короче {MinPasswordLength} символов.",
        "password_mismatch" => "Новый пароль и повтор не совпадают.",
        "background_missing" => "Файл не выбран.",
        "background_type" => "Это не картинка. Подойдёт jpeg, png или webp.",
        "background_too_big" => "Картинка больше 8 МБ.",
        "background_unknown" => "Такого фона нет в наборе темы.",
        "bad_person" => "Логин не должен быть пустым.",
        "person_short_password" => $"Пароль должен быть не короче {MinPasswordLength} символов.",
        "login_taken" => "Такой логин уже занят.",
        "last_owner" => "Это последний владелец, его нельзя удалить.",
        "self_delete" => "Себя удалить нельзя.",
        "other_owner" => "Другого владельца менять нельзя.",
        "own_password" => "Свой пароль меняйте в разделе «Пароль»: там спрашивают текущий.",
        not null => "Не удалось выполнить действие.",
        null => ok switch
        {
            "password" => "Пароль изменён.",
            "appearance" => "Настройки внешнего вида сохранены.",
            "token_created" => "Токен создан.",
            "token_note" => "Заметка сохранена.",
            "token_revoked" => "Токен отозван.",
            "link_revoked" => "Ссылка отозвана.",
            "person_added" => "Пользователь заведён.",
            "person_password" => "Пароль изменён.",
            "person_deleted" => "Пользователь удалён.",
            "background" => "Фон выбран.",
            "background_color" => "Цвет фона выбран.",
            "background_removed" => "Фон убран.",
            _ => null,
        },
    };
}
