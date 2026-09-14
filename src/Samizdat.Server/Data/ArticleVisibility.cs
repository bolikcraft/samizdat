namespace Samizdat.Server.Data;

public enum ArticleVisibility
{
    /// Видит только владелец.
    Private = 0,

    /// Видит любой заведённый человек. Наружу, без входа, статья уходит только гостевой ссылкой.
    Shared = 1,
}
