using Microsoft.EntityFrameworkCore;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using Samizdat.Server.Search;
using Samizdat.Server.Storage;

namespace Samizdat.Server.Commands;

public static class ServerCommands
{
    /// Вызывается до запуска веб-сервера. true — команда обработана, сервер стартовать не нужно.
    public static async Task<bool> TryRun(string[] args, IServiceProvider services)
    {
        if (args is ["owner", "set", ..])
        {
            // После логина и пароля могут идти аргументы конфигурации, как у "token new".
            if (args is not [_, _, var login, var password, ..])
            {
                Console.Error.WriteLine("owner set <логин> <пароль>");
                return true;
            }

            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            await db.Database.MigrateAsync();

            var user = await db.Users.FirstOrDefaultAsync(row => row.Login == login);
            if (user is null)
            {
                user = new UserRow
                {
                    Login = login,
                    PasswordHash = PasswordHasher.Hash(password),
                    Role = UserRole.Owner,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                db.Users.Add(user);
            }
            else
            {
                user.PasswordHash = PasswordHasher.Hash(password);
                user.SessionStamp = UserRow.NewSessionStamp();
                // Команда обещает владельца. Без повышения admin ниже удалится, и владельцев не останется.
                user.Role = UserRole.Owner;
                user.ApprovedAt ??= DateTimeOffset.UtcNow;
            }

            // Сид миграции Init — admin/admin. Пока пароль не сменили, это открытая дверь на сайт.
            var demo = await db.Users.FirstOrDefaultAsync(row => row.Login == "admin");
            var demoRemoved = demo is not null && demo != user && PasswordHasher.Verify("admin", demo.PasswordHash);
            if (demoRemoved)
                db.Users.Remove(demo!);

            await db.SaveChangesAsync();
            Console.WriteLine($"Владелец {login} записан.");
            if (demoRemoved)
                Console.WriteLine("Демо-учётка admin с паролем admin удалена.");
            return true;
        }

        if (args is ["token", "new", ..])
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            await db.Database.MigrateAsync();

            var owner = await db.Users.OrderBy(user => user.Id).FirstOrDefaultAsync(user => user.Role == UserRole.Owner);
            if (owner is null)
            {
                Console.Error.WriteLine("Сначала заведите владельца: owner set <логин> <пароль>");
                return true;
            }

            var token = ApiToken.Create();
            db.ApiTokens.Add(new ApiTokenRow
            {
                UserId = owner.Id,
                TokenHash = ApiToken.HashOf(token),
                Note = args.Length > 2 ? args[2] : null,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            Console.WriteLine(token);
            Console.Error.WriteLine("Токен показан один раз, сохраните его.");
            return true;
        }

        if (args is ["reindex", ..])
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            await db.Database.MigrateAsync();

            var result = IndexBackfill.Run(services, scope.ServiceProvider.GetRequiredService<ArticleFiles>(),
                                           scope.ServiceProvider.GetRequiredService<ILogger<Program>>(),
                                           force: true);
            Console.WriteLine($"Индекс собран заново: статей {result.Done}, не вышло {result.Failed}.");
            if (result.Failed > 0)
                Console.Error.WriteLine("Часть статей не проиндексирована, подробности в логе.");
            return true;
        }

        return false;
    }
}
