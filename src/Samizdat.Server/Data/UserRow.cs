namespace Samizdat.Server.Data;

public enum UserRole
{
    Owner = 0,

    /// Читает открытые статьи, делает гостевые ссылки, меняет свой пароль. Номер 1 когда-то
    /// занимал автор — значение убрано, номер не переиспользуем.
    Reader = 2,
}

public sealed class UserRow
{
    public int Id { get; set; }
    public required string Login { get; set; }
    public required string PasswordHash { get; set; }
    public UserRole Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
