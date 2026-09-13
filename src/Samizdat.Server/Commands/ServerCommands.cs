using Microsoft.EntityFrameworkCore;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Commands;

public static class ServerCommands
{
    /// Вызывается до запуска веб-сервера. true — команда обработана, сервер стартовать не нужно.
    public static async Task<bool> TryRun(string[] args, IServiceProvider services)
    {
        if (args is ["owner", "set", var login, var password])
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            await db.Database.MigrateAsync();

            var user = await db.Users.FirstOrDefaultAsync(row => row.Login == login);
            if (user is null)
            {
                db.Users.Add(new UserRow
                {
                    Login = login,
                    PasswordHash = PasswordHasher.Hash(password),
                    Role = UserRole.Owner,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                user.PasswordHash = PasswordHasher.Hash(password);
            }

            await db.SaveChangesAsync();
            Console.WriteLine($"Владелец {login} записан.");
            return true;
        }

        if (args is ["token", "new", ..])
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            await db.Database.MigrateAsync();

            var owner = await db.Users.FirstOrDefaultAsync(user => user.Role == UserRole.Owner);
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

        return false;
    }
}
