namespace Samizdat.Server.Data;

/// Приглашение: одноразовая ссылка, по которой человек заводит себе учётку читателя.
/// Устроено как ShareLinkRow — тот же токен, тот же срок, тот же отзыв.
public sealed class InviteRow
{
    public int Id { get; set; }
    public required string Token { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }

    /// Логин, а не ключ на users: удалили человека — след приглашения остаётся.
    public string? UsedByLogin { get; set; }

    public bool IsAlive(DateTimeOffset now)
        => RevokedAt is null && UsedAt is null && (ExpiresAt is null || ExpiresAt > now);
}
