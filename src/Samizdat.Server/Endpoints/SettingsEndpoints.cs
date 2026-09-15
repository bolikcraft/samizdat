using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Samizdat.Core.Themes;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using Samizdat.Server.Rendering;
using Samizdat.Server.Storage;
using static Samizdat.Server.Endpoints.SettingsPage;

namespace Samizdat.Server.Endpoints;

public static class SettingsEndpoints
{
    const long MaxBackgroundBytes = 8 * 1024 * 1024;

    // Многочастная форма добавляет к файлу границы и заголовки полей — запас с лихвой их перекрывает.
    // Предел стоит на всём теле: такое тело мы отказываемся буферизовать, даже не начиная читать.
    const long MaxBackgroundRequestBytes = MaxBackgroundBytes + 64 * 1024;

    // Новый токен едет от формы до страницы в куке, а не в адресе: адрес попадает в историю
    // браузера, в заголовок Referer и в логи прокси. Кука живёт до первого показа страницы.
    const string NewTokenCookie = "samizdat_new_token";

    public static void MapSettings(this WebApplication app)
    {
        // Группа требует вход; роль проверяется на каждом маршруте: читателю тут доступен
        // один раздел — свой пароль.
        var group = app.MapGroup("/settings").RequireAuthorization();

        group.MapGet("/", (PageRenderer pages, SamizdatDbContext db, SiteSettings settings, ClaimsPrincipal user,
                           ThemeFactory themes, IThemeSource theme, BackgroundFile background,
                           IAntiforgery antiforgery, HttpContext context,
                           string? ok, string? err) =>
        {
            // Читателю открыт один раздел — свой пароль, поэтому чужие списки ему и не собираем.
            var isOwner = ArticleAccess.IsOwner(user);
            if (CurrentUser(db, user) is not { } person) return LoggedOut();

            List<ApiTokenRow> tokens = isOwner
                ? db.ApiTokens.Where(token => token.UserId == person.Id)
                    .OrderByDescending(token => token.CreatedAt).ToList()
                : [];

            var newToken = context.Request.Cookies[NewTokenCookie];
            if (newToken is not null) context.Response.Cookies.Delete(NewTokenCookie, NewTokenCookieOptions(context));

            var now = DateTimeOffset.UtcNow;
            List<Dictionary<string, object?>> links = [];
            if (isOwner)
            {
                var titles = db.Articles.ToDictionary(article => article.Slug, article => article.Title);
                // Живые сверху: мёртвые строки остаются как след, но не мешают найти рабочую ссылку.
                links = db.ShareLinks.ToList()
                    .OrderByDescending(link => link.IsAlive(now)).ThenByDescending(link => link.CreatedAt)
                    .Select(link => new Dictionary<string, object?>
                    {
                        ["id"] = link.Id,
                        ["slug"] = link.Slug,
                        ["title"] = titles.GetValueOrDefault(link.Slug, link.Slug),
                        ["note"] = link.Note,
                        ["url"] = $"{context.Request.Scheme}://{context.Request.Host}/s/{link.Token}",
                        ["alive"] = link.IsAlive(now),
                        ["expires_at"] = link.ExpiresAt?.ToString("yyyy-MM-dd HH:mm"),
                        ["opened_count"] = link.OpenedCount,
                        ["last_opened_at"] = link.LastOpenedAt?.ToString("yyyy-MM-dd HH:mm"),
                    }).ToList();
            }

            List<Dictionary<string, object?>> people = isOwner
                // Ждущие сюда не попадают: они живут в очереди раздела «Регистрация», пока
                // владелец не решит. Кнопки этого списка им не подходят.
                ? db.Users.Where(row => row.ApprovedAt != null).OrderBy(row => row.Login).ToList()
                    .Select(row => new Dictionary<string, object?>
                {
                    ["id"] = row.Id,
                    ["login"] = row.Login,
                    ["role"] = row.Role == UserRole.Owner ? "владелец" : "читатель",
                    ["created_at"] = row.CreatedAt.ToString("yyyy-MM-dd"),
                    // Строка владельца — без кнопок: свой пароль меняют в разделе «Пароль»,
                    // а чужого владельца не трогают вовсе.
                    ["can_change"] = row.Role != UserRole.Owner,
                }).ToList()
                : [];

            return Results.Content(pages.Render("settings.html", new()
            {
                ["page_title"] = "Настройки",
                ["site"] = PageEndpoints.SiteModel(settings),
                // Вместо дерева статей в боковике — список разделов настроек.
                ["side_nav"] = "settings-nav",
                ["user"] = PageEndpoints.UserModel(user),
                ["antiforgery"] = AntiforgeryHtml.Field(antiforgery, context),
                ["message"] = Message(ok, err),
                ["message_kind"] = err is not null ? "err" : ok is not null ? "ok" : null,
                ["message_section"] = (err ?? ok) is { } code ? SectionOf(code) : null,
                ["color_scheme"] = settings.ColorScheme,
                ["background"] = BackgroundModel(settings, background, BackgroundCatalog.Read(theme)),
                ["download"] = new Dictionary<string, object?>
                {
                    ["readers"] = settings.Download.Readers,
                    ["guests"] = settings.Download.Guests,
                },
                ["themes"] = themes.AvailableThemes().Select(name => new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["selected"] = name == settings.ThemeName,
                }).ToList(),
                ["new_token"] = newToken,
                ["tokens"] = tokens.Select(token => new Dictionary<string, object?>
                {
                    ["id"] = token.Id,
                    ["note"] = token.Note,
                    ["created_at"] = token.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                    ["last_used_at"] = token.LastUsedAt?.ToString("yyyy-MM-dd HH:mm"),
                }).ToList(),
                ["links"] = links,
                ["people"] = people,
                ["signup"] = isOwner ? SignupSettingsEndpoints.Model(db, settings, context) : null,
            }), "text/html; charset=utf-8");
        });

        group.MapPost("/password", async (HttpContext context, SamizdatDbContext db, ClaimsPrincipal user) =>
        {
            var form = await context.Request.ReadFormAsync();
            var current = form["current"].ToString();
            var next = form["new"].ToString();
            var repeat = form["new2"].ToString();

            if (CurrentUser(db, user) is not { } person) return LoggedOut();

            if (!PasswordHasher.Verify(current, person.PasswordHash)) return Err("wrong_password");
            if (next.Length < MinPasswordLength) return Err("short_password");
            if (next != repeat) return Err("password_mismatch");

            person.PasswordHash = PasswordHasher.Hash(next);
            // Новая метка гасит прочие сессии этого человека; свою тут же выдаём заново,
            // иначе смена своего пароля выкидывала бы со страницы настроек.
            person.SessionStamp = UserRow.NewSessionStamp();
            db.SaveChanges();
            await SessionCookie.SignIn(context, person);
            return Ok("password");
        }).RequireValidToken();

        group.MapPost("/appearance", async (HttpContext context, SiteSettings settings, ThemeFactory themes) =>
        {
            var form = await context.Request.ReadFormAsync();
            var theme = form["theme"].ToString();
            var colorScheme = form["color_scheme"].ToString();

            if (themes.AvailableThemes().Contains(theme)) settings.Set("theme.name", theme);
            if (colorScheme is "light" or "dark" or "system") settings.Set("theme.color_scheme", colorScheme);

            return Ok("appearance");
        }).RequireValidToken().OwnerOnly();

        group.MapPost("/articles", async (HttpContext context, SiteSettings settings) =>
        {
            var form = await context.Request.ReadFormAsync();

            // Снятый флажок форма не присылает вовсе, поэтому пишем обе настройки разом,
            // а не только те, что пришли: иначе выключить скачивание было бы нечем.
            settings.Set("articles.download.readers", form["readers"].ToString() == "on" ? "on" : "");
            settings.Set("articles.download.guests", form["guests"].ToString() == "on" ? "on" : "");

            return Ok("articles");
        }).RequireValidToken().OwnerOnly();

        group.MapPost("/background", [RequestSizeLimit(MaxBackgroundRequestBytes)]
            async (HttpContext context, SiteSettings settings, BackgroundFile background) =>
        {
            IFormCollection form;
            try
            {
                form = await context.Request.ReadFormAsync();
            }
            catch (BadHttpRequestException)
            {
                // Тело больше лимита: Kestrel обрывает чтение сам, не дав ReadFormAsync его дочитать.
                return Err("background_too_big");
            }
            catch (InvalidDataException)
            {
                // Форма нечитаема: оборванная граница, слишком много полей, слишком длинный ключ.
                return Err("background_form");
            }

            var upload = form.Files["file"];
            if (upload is null || upload.Length == 0) return Err("background_missing");
            if (upload.Length > MaxBackgroundBytes) return Err("background_too_big");

            await using var stream = upload.OpenReadStream();
            var head = new byte[BackgroundFile.HeadLength];
            var read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false);
            if (BackgroundFile.ExtensionOf(head.AsSpan(0, read)) is not { } extension)
                return Err("background_type");

            stream.Position = 0;
            settings.Set("theme.background", background.Save(stream, extension));
            return Ok("background");
        }).RefuseAnOversizedBody().RequireValidToken().OwnerOnly();

        // Выбор готового фона: картинка из набора темы, цвет из палитры, своя загруженная
        // картинка или ничего. Значение попадает в настройку как есть, поэтому всё, кроме пустоты,
        // сверяется с набором темы: в форму можно прислать что угодно.
        group.MapPost("/background/pick",
            async (HttpContext context, SiteSettings settings, BackgroundFile background, IThemeSource theme) =>
        {
            var form = await context.Request.ReadFormAsync();
            var pick = form["pick"].ToString();
            var catalog = BackgroundCatalog.Read(theme);

            if (pick.Length == 0)
            {
                settings.Set("theme.background", "");
                return Ok("background_removed");
            }

            if (pick == "upload")
            {
                if (background.Current() is not { } name) return Err("background_missing");

                settings.Set("theme.background", name);
                return Ok("background");
            }

            if (pick.StartsWith(SiteSettings.PresetPrefix, StringComparison.Ordinal))
            {
                var file = pick[SiteSettings.PresetPrefix.Length..];
                if (!catalog.HasImage(file)) return Err("background_unknown");

                settings.Set("theme.background", pick);
                return Ok("background");
            }

            if (pick.StartsWith(SiteSettings.ColorPrefix, StringComparison.Ordinal))
            {
                var color = pick[SiteSettings.ColorPrefix.Length..];
                if (!catalog.HasColor(color)) return Err("background_unknown");

                settings.Set("theme.background", pick);
                return Ok("background_color");
            }

            return Err("background_unknown");
        }).RequireValidToken().OwnerOnly();

        // Удаление загруженной картинки. Если она стояла фоном, фон заодно снимается: файла больше нет.
        group.MapPost("/background/remove", (SiteSettings settings, BackgroundFile background) =>
        {
            background.Remove();
            if (settings.Background.Kind == BackgroundKind.Upload) settings.Set("theme.background", "");
            return Ok("background_removed");
        }).RequireValidToken().OwnerOnly();

        group.MapPost("/tokens", async (HttpContext context, SamizdatDbContext db, ClaimsPrincipal user) =>
        {
            var form = await context.Request.ReadFormAsync();
            var note = form["note"].ToString().Trim();

            if (CurrentUser(db, user) is not { } owner) return LoggedOut();

            var token = ApiToken.Create();
            db.ApiTokens.Add(new ApiTokenRow
            {
                UserId = owner.Id,
                TokenHash = ApiToken.HashOf(token),
                Note = note.Length > 0 ? note : null,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();

            context.Response.Cookies.Append(NewTokenCookie, token, NewTokenCookieOptions(context));
            return Ok("token_created");
        }).RequireValidToken().OwnerOnly();

        group.MapPost("/tokens/{id:int}/note", async (int id, HttpContext context,
                                                     SamizdatDbContext db, ClaimsPrincipal user) =>
        {
            var form = await context.Request.ReadFormAsync();
            var note = form["note"].ToString().Trim();

            if (CurrentUser(db, user) is not { } owner) return LoggedOut();

            var token = db.ApiTokens.FirstOrDefault(row => row.Id == id);
            if (token is null || token.UserId != owner.Id) return Results.NotFound();

            token.Note = note.Length > 0 ? note : null;
            db.SaveChanges();
            return Ok("token_note");
        }).RequireValidToken().OwnerOnly();

        group.MapPost("/tokens/{id:int}/revoke", (int id, SamizdatDbContext db, ClaimsPrincipal user) =>
        {
            if (CurrentUser(db, user) is not { } owner) return LoggedOut();

            var token = db.ApiTokens.FirstOrDefault(row => row.Id == id);
            if (token is null || token.UserId != owner.Id) return Results.NotFound();

            db.ApiTokens.Remove(token);
            db.SaveChanges();
            return Ok("token_revoked");
        }).RequireValidToken().OwnerOnly();

        group.MapPost("/people", async (HttpContext context, SamizdatDbContext db) =>
        {
            var form = await context.Request.ReadFormAsync();
            var login = form["login"].ToString().Trim();
            var password = form["password"].ToString();

            if (login.Length == 0 || login.Length > MaxLoginLength) return Err("bad_person");
            if (password.Length < MinPasswordLength) return Err("person_short_password");

            db.Users.Add(new UserRow
            {
                Login = login,
                PasswordHash = PasswordHasher.Hash(password),
                Role = UserRole.Reader,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException error)
                when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Занятый логин слышим от базы, а не смотрим перед вставкой: между «посмотрел»
                // и «вставил» логин успевал занять соседний запрос, и владелец получал голый 500.
                return Err("login_taken");
            }

            return Ok("person_added");
        }).RequireValidToken().OwnerOnly();

        group.MapPost("/people/{id:int}/password", async (int id, HttpContext context, SamizdatDbContext db,
                                                         ClaimsPrincipal user) =>
        {
            var form = await context.Request.ReadFormAsync();
            var password = form["password"].ToString();

            if (CurrentUser(db, user) is not { } me) return LoggedOut();

            var person = db.Users.Find(id);
            if (person is null) return Results.NotFound();
            // Свой пароль меняют в разделе «Пароль»: там спрашивают текущий. Чужой владелец
            // не подчиняется даже владельцу — свою учётку он ведёт сам.
            if (person.Id == me.Id) return Err("own_password");
            if (person.Role == UserRole.Owner) return Err("other_owner");
            if (password.Length < MinPasswordLength) return Err("person_short_password");

            person.PasswordHash = PasswordHasher.Hash(password);
            person.SessionStamp = UserRow.NewSessionStamp();
            await db.SaveChangesAsync();
            return Ok("person_password");
        }).RequireValidToken().OwnerOnly();

        group.MapPost("/people/{id:int}/delete", async (int id, SamizdatDbContext db, ClaimsPrincipal user) =>
        {
            if (CurrentUser(db, user) is not { } me) return LoggedOut();

            var person = db.Users.Find(id);
            if (person is null) return Results.NotFound();

            // Ждущего эта кнопка не трогает: его разбирают в разделе «Регистрация», в обход
            // очереди тут его снести нельзя.
            if (person.ApprovedAt is null) return Err("not_pending");

            // Последнего владельца удалять нельзя: сайт остался бы без входа в настройки,
            // и поднять его можно было бы только командой в консоли.
            if (person.Role == UserRole.Owner && db.Users.Count(row => row.Role == UserRole.Owner) == 1)
                return Err("last_owner");
            if (person.Id == me.Id) return Err("self_delete");
            if (person.Role == UserRole.Owner) return Err("other_owner");

            db.Users.Remove(person);
            await db.SaveChangesAsync();
            return Ok("person_deleted");
        }).RequireValidToken().OwnerOnly();

        // Отзыв мягкий: строка остаётся, чтобы гость получил 410 «ссылка не работает», а не 404.
        group.MapPost("/links/{id:int}/revoke", (int id, SamizdatDbContext db) =>
        {
            var link = db.ShareLinks.FirstOrDefault(row => row.Id == id && row.RevokedAt == null);
            if (link is null) return Results.NotFound();

            link.RevokedAt = DateTimeOffset.UtcNow;
            db.SaveChanges();
            return Ok("link_revoked");
        }).RequireValidToken().OwnerOnly();
    }

    /// Отсеивает слишком большое тело по Content-Length, ничего не читая.
    // Обязан стоять до RequireValidToken: тот ради токена читает многочастную форму сам, Kestrel
    // обрывает чтение прямо в нём, и владелец получает голый 400 вместо сообщения про 8 МБ.
    // [RequestSizeLimit] на маршруте оставлен: он ловит тело без Content-Length (chunked).
    static RouteHandlerBuilder RefuseAnOversizedBody(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter(async (invocation, next) =>
            invocation.HttpContext.Request.ContentLength > MaxBackgroundRequestBytes
                ? Err("background_too_big")
                : await next(invocation));

    // Path сужает куку до настроек, HttpOnly закрывает её от скриптов. Delete обязан повторить
    // эти же поля, иначе браузер удалит не ту куку, и токен останется висеть до конца сеанса.
    static CookieOptions NewTokenCookieOptions(HttpContext context) => new()
    {
        HttpOnly = true,
        Secure = context.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = "/settings",
    };

    /// Галерея фонов: плитки набора темы, палитра цветов и загруженная картинка. Отмечена ровно
    /// одна плитка — та, что стоит фоном сейчас.
    static Dictionary<string, object?> BackgroundModel(SiteSettings settings, BackgroundFile background,
                                                       BackgroundCatalog catalog)
    {
        var choice = settings.Background;
        var uploaded = background.Current();

        return new Dictionary<string, object?>
        {
            ["none_selected"] = choice.Kind == BackgroundKind.None,
            ["upload_url"] = uploaded is not null ? $"/background?v={background.Version(uploaded)}" : null,
            ["upload_selected"] = choice.Kind == BackgroundKind.Upload,
            ["color"] = settings.BackgroundColor,
            ["color_selected"] = choice.Kind == BackgroundKind.Color,
            ["images"] = catalog.Images.Select(image => new Dictionary<string, object?>
            {
                ["file"] = image.File,
                ["title"] = image.Title,
                // Плитке хватает миниатюры: полный снимок весит в десять раз больше и нужен
                // только когда фон уже выбран.
                ["url"] = $"/assets/backgrounds/{image.Thumb}",
                ["selected"] = choice.Kind == BackgroundKind.Preset && choice.Value == image.File,
            }).ToList(),
            ["colors"] = catalog.Colors.Select(color => new Dictionary<string, object?>
            {
                ["value"] = color,
                ["selected"] = choice.Kind == BackgroundKind.Color
                               && string.Equals(choice.Value, color, StringComparison.OrdinalIgnoreCase),
            }).ToList(),
        };
    }

}
