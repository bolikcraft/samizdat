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

        return false;
    }
}
