using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Samizdat.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SessionStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SessionStamp",
                table: "users",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Своя метка каждому: общая пустая строка пустила бы cookie одного человека к другому,
            // если у того имя совпало. Выданные до этого cookie метки не несут и гаснут сами.
            migrationBuilder.Sql("""UPDATE users SET "SessionStamp" = md5(random()::text || "Id"::text);""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SessionStamp",
                table: "users");
        }
    }
}
