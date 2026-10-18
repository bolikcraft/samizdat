using System.Text;
using Microsoft.EntityFrameworkCore;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using Samizdat.Server.Endpoints;
using Samizdat.Server.Search;
using Samizdat.Server.Storage;

namespace Samizdat.Server.Commands;

public static class ServerCommands
{
    /// Вызывается до запуска веб-сервера. true — команда обработана, сервер стартовать не нужно.
    /// readPassword даёт пароль, которого нет в аргументах; без него пароль читается из stdin.
    public static async Task<bool> TryRun(string[] args, IServiceProvider services,
                                          Func<string?>? readPassword = null)
    {
        if (args is ["owner", "set", ..])
        {
            if (args is not [_, _, var login, ..] || IsConfigArgument(login))
            {
                Console.Error.WriteLine("owner set <логин> [пароль]");
                return true;
            }

            // Пароль в аргументах оставлен ради старых скриптов: его видно в истории оболочки и в ps.
            // Аргумент конфигурации после логина (как у "token new") паролем не считается.
            var password = args is [_, _, _, var given, ..] && !IsConfigArgument(given)
                ? given
                : readPassword is not null ? readPassword() : ReadPassword();
            if (password is null || password.Length < SettingsPage.MinPasswordLength
                                 || password.Length > SettingsPage.MaxPasswordLength)
            {
                Console.Error.WriteLine($"Пароль должен быть длиной от {SettingsPage.MinPasswordLength} " +
                                        $"до {SettingsPage.MaxPasswordLength} символов.");
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
                Console.Error.WriteLine("Сначала заведите владельца: owner set <логин>");
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

    // Аргументы конфигурации .NET пишутся как --ключ=значение или --ключ значение.
    static bool IsConfigArgument(string arg) => arg.StartsWith("--", StringComparison.Ordinal);

    /// Пароль из stdin: из трубы — первая строка, из терминала — без эха на экране.
    static string? ReadPassword()
    {
        if (Console.IsInputRedirected) return Console.In.ReadLine();

        Console.Error.Write("Пароль: ");
        var password = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0) password.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
            }
        }
        Console.Error.WriteLine();
        return password.ToString();
    }
}
