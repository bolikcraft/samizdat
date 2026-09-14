using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Samizdat.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DefaultBackground : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Фон первого запуска: чистый сайт открывается сразу с картинкой, а не с пустым полем.
            // DO NOTHING, а не InsertData: на работающем сайте настройка уже есть, и своё значение —
            // в том числе пустое, то есть выбранное «без фона» — она не отдаёт.
            migrationBuilder.Sql("""
                INSERT INTO site_settings ("Key", "Value")
                VALUES ('theme.background', 'preset:moss.webp')
                ON CONFLICT ("Key") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Убираем только нетронутое значение: выбор владельца откат миграции стирать не должен.
            migrationBuilder.Sql("""
                DELETE FROM site_settings
                WHERE "Key" = 'theme.background' AND "Value" = 'preset:moss.webp';
                """);
        }
    }
}
