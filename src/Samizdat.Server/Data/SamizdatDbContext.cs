using Microsoft.EntityFrameworkCore;

namespace Samizdat.Server.Data;

public sealed class SamizdatDbContext(DbContextOptions<SamizdatDbContext> options) : DbContext(options)
{
    public DbSet<ArticleRow> Articles => Set<ArticleRow>();
    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<ApiTokenRow> ApiTokens => Set<ApiTokenRow>();
    public DbSet<SettingRow> Settings => Set<SettingRow>();
    public DbSet<ShareLinkRow> ShareLinks => Set<ShareLinkRow>();
    public DbSet<InviteRow> Invites => Set<InviteRow>();

    /// Сравнение без оглядки на регистр: Ivan и ivan — один человек. Правило стоит на колонке,
    /// поэтому его держат разом и уникальный индекс, и поиск при входе.
    const string CaseInsensitive = "case_insensitive";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasCollation(CaseInsensitive, locale: "und-u-ks-level2", provider: "icu", deterministic: false);

        model.Entity<ArticleRow>(article =>
        {
            article.ToTable("articles");
            article.HasKey(row => row.Slug);
            article.Property(row => row.Slug).HasMaxLength(200);
            article.Property(row => row.Title).HasMaxLength(500);
            article.Property(row => row.Folder).HasMaxLength(1000).HasDefaultValue("");
        });

        model.Entity<UserRow>(user =>
        {
            user.ToTable("users");
            user.HasIndex(row => row.Login).IsUnique();
            user.Property(row => row.Login).HasMaxLength(100).UseCollation(CaseInsensitive);
        });

        model.Entity<ApiTokenRow>(token =>
        {
            token.ToTable("api_tokens");
            token.HasIndex(row => row.TokenHash).IsUnique();
            // Каскад: удалили владельца — токены сироты быть не должно, а не 500 при авторизации по нему.
            token.HasOne<UserRow>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<SettingRow>(setting =>
        {
            setting.ToTable("site_settings");
            setting.HasKey(row => row.Key);
            setting.Property(row => row.Key).HasMaxLength(100);
            setting.Property(row => row.Value).HasMaxLength(500);
        });

        model.Entity<ShareLinkRow>(link =>
        {
            link.ToTable("share_links");
            link.HasIndex(row => row.Token).IsUnique();
            link.Property(row => row.Token).HasMaxLength(22);
            link.Property(row => row.Slug).HasMaxLength(200);
            link.Property(row => row.Note).HasMaxLength(200);
            // Каскад: статью снесли через push --prune — её ссылки не должны пережить статью.
            link.HasOne<ArticleRow>().WithMany().HasForeignKey(row => row.Slug).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<InviteRow>(invite =>
        {
            invite.ToTable("invites");
            invite.HasIndex(row => row.Token).IsUnique();
            invite.Property(row => row.Token).HasMaxLength(22);
            invite.Property(row => row.Note).HasMaxLength(200);
            invite.Property(row => row.UsedByLogin).HasMaxLength(100);
        });
    }
}
