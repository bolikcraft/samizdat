using Microsoft.EntityFrameworkCore;

namespace Samizdat.Server.Data;

public sealed class SamizdatDbContext(DbContextOptions<SamizdatDbContext> options) : DbContext(options)
{
    public DbSet<ArticleRow> Articles => Set<ArticleRow>();
    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<ApiTokenRow> ApiTokens => Set<ApiTokenRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<ArticleRow>(article =>
        {
            article.ToTable("articles");
            article.HasKey(row => row.Slug);
            article.Property(row => row.Slug).HasMaxLength(200);
            article.Property(row => row.Title).HasMaxLength(500);
        });

        model.Entity<UserRow>(user =>
        {
            user.ToTable("users");
            user.HasIndex(row => row.Login).IsUnique();
            user.Property(row => row.Login).HasMaxLength(100);
        });

        model.Entity<ApiTokenRow>(token =>
        {
            token.ToTable("api_tokens");
            token.HasIndex(row => row.TokenHash).IsUnique();
        });
    }
}
