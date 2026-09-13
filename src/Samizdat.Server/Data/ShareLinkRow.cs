namespace Samizdat.Server.Data;

public sealed class ShareLinkRow
{
    public int Id { get; set; }
    public required string Token { get; set; }
    public required string Slug { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public int OpenedCount { get; set; }
    public DateTimeOffset? LastOpenedAt { get; set; }

    public bool IsAlive(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}
