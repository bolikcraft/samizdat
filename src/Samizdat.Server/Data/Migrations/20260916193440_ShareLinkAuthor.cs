using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Samizdat.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShareLinkAuthor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "share_links",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_share_links_CreatedByUserId",
                table: "share_links",
                column: "CreatedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_share_links_users_CreatedByUserId",
                table: "share_links",
                column: "CreatedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_share_links_users_CreatedByUserId",
                table: "share_links");

            migrationBuilder.DropIndex(
                name: "IX_share_links_CreatedByUserId",
                table: "share_links");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "share_links");
        }
    }
}
