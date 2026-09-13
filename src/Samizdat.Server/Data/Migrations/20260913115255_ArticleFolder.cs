using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Samizdat.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ArticleFolder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Folder",
                table: "articles",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Folder",
                table: "articles");
        }
    }
}
