using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using static Samizdat.Server.Endpoints.SettingsPage;

namespace Samizdat.Server.Endpoints;

/// Раздел настроек «Регистрация»: галка открытой записи, очередь заявок и приглашения.
/// Живёт отдельно от SettingsEndpoints — тот и без него на пятьсот строк.
public static class SignupSettingsEndpoints
{
    // День, неделя, месяц и «без срока»: другие значения формой не выдаются и не принимаются.
    static readonly int[] AllowedDays = [0, 1, 7, 30];

    const int MaxNoteLength = 200;

    public static void MapSignupSettings(this WebApplication app)
    {
        var group = app.MapGroup("/settings").RequireAuthorization();

        group.MapPost("/invites", async (HttpContext context, SamizdatDbContext db) =>
        {
            var form = await context.Request.ReadFormAsync();
            var note = form["note"].ToString().Trim();

            if (note.Length > MaxNoteLength) return Err("invite_note");
            if (!int.TryParse(form["days"], out var days) || !AllowedDays.Contains(days))
                return Err("invite_term");

            var now = DateTimeOffset.UtcNow;
            db.Invites.Add(new InviteRow
            {
                Token = ShareToken.Create(),
                Note = note.Length == 0 ? null : note,
                CreatedAt = now,
                ExpiresAt = days == 0 ? null : now.AddDays(days),
            });
            await db.SaveChangesAsync();
            return Ok("invite_created");
        }).RequireValidToken().OwnerOnly();

        // Отзыв мягкий: строка остаётся, чтобы приглашённый увидел 410 «ссылка не работает».
        group.MapPost("/invites/{id:int}/revoke", async (int id, SamizdatDbContext db) =>
        {
            var invite = db.Invites.FirstOrDefault(
                row => row.Id == id && row.RevokedAt == null && row.UsedAt == null);
            if (invite is null) return Results.NotFound();

            invite.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Ok("invite_revoked");
        }).RequireValidToken().OwnerOnly();
    }

    /// Всё, что раздел показывает владельцу. Зовётся из GET /settings — страница там одна.
    internal static Dictionary<string, object?> Model(SamizdatDbContext db, SiteSettings settings,
                                                      HttpContext context)
    {
        var now = DateTimeOffset.UtcNow;

        return new Dictionary<string, object?>
        {
            ["open"] = settings.OpenRegistration,
            // Живые сверху: погашенные остаются следом, но не мешают найти рабочую ссылку.
            ["invites"] = db.Invites.ToList()
                .OrderByDescending(row => row.IsAlive(now)).ThenByDescending(row => row.CreatedAt)
                .Select(row => new Dictionary<string, object?>
                {
                    ["id"] = row.Id,
                    ["note"] = row.Note,
                    ["url"] = $"{context.Request.Scheme}://{context.Request.Host}/i/{row.Token}",
                    ["alive"] = row.IsAlive(now),
                    ["expires_at"] = row.ExpiresAt?.ToString("yyyy-MM-dd HH:mm"),
                    ["used_at"] = row.UsedAt?.ToString("yyyy-MM-dd HH:mm"),
                    ["used_by"] = row.UsedByLogin,
                }).ToList(),
        };
    }
}
