using Microsoft.EntityFrameworkCore;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using static Samizdat.Server.Endpoints.SettingsPage;

namespace Samizdat.Server.Endpoints;

/// Блок регистрации в разделе «Пользователи»: галка открытой записи, очередь заявок и приглашения.
/// Живёт отдельно от SettingsEndpoints — тот и без него не короткий.
public static class SignupSettingsEndpoints
{
    // День, неделя, месяц и «без срока»: другие значения формой не выдаются и не принимаются.
    static readonly int[] AllowedDays = [0, 1, 7, 30];

    const int MaxNoteLength = 200;

    public static void MapSignupSettings(this WebApplication app)
    {
        var group = app.MapGroup("/settings").RequireAuthorization();

        // Галка без значения в форме не приходит вовсе: браузер шлёт поле только у отмеченной.
        group.MapPost("/signup/open", async (HttpContext context, SiteSettings settings) =>
        {
            var form = await context.Request.ReadFormAsync();
            settings.Set("auth.open_registration", form["open"].ToString() == "on" ? "true" : "false");
            return Ok("signup_open");
        }).RequireValidToken().OwnerOnly();

        group.MapPost("/signup/{id:int}/approve", (int id, SamizdatDbContext db) =>
        {
            var person = db.Users.AsNoTracking().FirstOrDefault(row => row.Id == id);
            if (person is null) return Results.NotFound();
            // Только ждущего: живого человека эти кнопки не трогают, иначе «отказать» стало бы
            // вторым способом удалить кого угодно в обход раздела «Пользователи». Ждущий не
            // бывает владельцем — пустую дату ставит только открытая регистрация, а она заводит
            // читателя.
            if (person.ApprovedAt is not null) return Err("not_pending");

            // Условие внутри UPDATE, а не только проверка выше: без него соседняя вкладка
            // успевает снести человека между чтением и записью.
            var changed = db.Users.Where(row => row.Id == id && row.ApprovedAt == null)
                .ExecuteUpdate(set => set.SetProperty(row => row.ApprovedAt, DateTimeOffset.UtcNow));

            return changed == 0 ? Err("not_pending") : Ok("signup_approved");
        }).RequireValidToken().OwnerOnly();

        group.MapPost("/signup/{id:int}/reject", (int id, SamizdatDbContext db) =>
        {
            var person = db.Users.AsNoTracking().FirstOrDefault(row => row.Id == id);
            if (person is null) return Results.NotFound();
            if (person.ApprovedAt is not null) return Err("not_pending");

            // Отказ сносит строку, а не помечает её: ждущая заявка держит логин занятым
            // уникальным индексом, и помеченная держала бы его вечно. Условие внутри DELETE —
            // по той же причине, что и в approve: соседняя вкладка не должна снести уже пущенного.
            var changed = db.Users.Where(row => row.Id == id && row.ApprovedAt == null).ExecuteDelete();

            return changed == 0 ? Err("not_pending") : Ok("signup_rejected");
        }).RequireValidToken().OwnerOnly();

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
        group.MapPost("/invites/{id:int}/revoke", (int id, SamizdatDbContext db) =>
        {
            var invite = db.Invites.FirstOrDefault(
                row => row.Id == id && row.RevokedAt == null && row.UsedAt == null);
            if (invite is null) return Results.NotFound();

            invite.RevokedAt = DateTimeOffset.UtcNow;
            db.SaveChanges();
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
            ["pending"] = db.Users.Where(row => row.ApprovedAt == null)
                .OrderBy(row => row.CreatedAt).ToList()
                .Select(row => new Dictionary<string, object?>
                {
                    ["id"] = row.Id,
                    ["login"] = row.Login,
                    ["created_at"] = row.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                }).ToList(),
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
