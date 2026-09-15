using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Samizdat.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Stage4Search : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IndexedHash",
                table: "articles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SearchText",
                table: "articles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "article_links",
                columns: table => new
                {
                    FromSlug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ToSlug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_article_links", x => new { x.FromSlug, x.ToSlug });
                    table.ForeignKey(
                        name: "FK_article_links_articles_FromSlug",
                        column: x => x.FromSlug,
                        principalTable: "articles",
                        principalColumn: "Slug",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_article_links_ToSlug",
                table: "article_links",
                column: "ToSlug");

            // Триграммы — запасной поиск, когда полнотекст не нашёл ничего.
            // С PostgreSQL 13 расширение trusted: прав владельца базы хватает, superuser не нужен.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            // Колонки генерируемые: вектор нельзя забыть обновить, база считает его сама.
            // Два вектора, а не один: читателю тело закрытой статьи искать нельзя, а заголовок можно.
            migrationBuilder.Sql("""
                ALTER TABLE articles ADD COLUMN "MetaVector" tsvector GENERATED ALWAYS AS (
                    setweight(to_tsvector('russian', coalesce("Title", '')), 'A') ||
                    setweight(to_tsvector('russian', coalesce("Description", '')), 'B')) STORED;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE articles ADD COLUMN "SearchVector" tsvector GENERATED ALWAYS AS (
                    setweight(to_tsvector('russian', coalesce("Title", '')), 'A') ||
                    setweight(to_tsvector('russian', coalesce("Description", '')), 'B') ||
                    setweight(to_tsvector('russian', coalesce("SearchText", '')), 'C')) STORED;
                """);

            migrationBuilder.Sql("""CREATE INDEX ix_articles_meta_vector ON articles USING gin ("MetaVector");""");
            migrationBuilder.Sql("""CREATE INDEX ix_articles_search_vector ON articles USING gin ("SearchVector");""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Векторы снимаем первыми: SearchText под генерируемой колонкой не удалить.
            migrationBuilder.Sql("""DROP INDEX IF EXISTS ix_articles_search_vector;""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS ix_articles_meta_vector;""");
            migrationBuilder.Sql("""ALTER TABLE articles DROP COLUMN IF EXISTS "SearchVector";""");
            migrationBuilder.Sql("""ALTER TABLE articles DROP COLUMN IF EXISTS "MetaVector";""");
            // Расширение не снимаем: его мог поставить не этот сайт, а снятие уронило бы чужие индексы.

            migrationBuilder.DropTable(
                name: "article_links");

            migrationBuilder.DropColumn(
                name: "IndexedHash",
                table: "articles");

            migrationBuilder.DropColumn(
                name: "SearchText",
                table: "articles");
        }
    }
}
