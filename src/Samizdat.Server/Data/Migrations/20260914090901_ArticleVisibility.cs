using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Samizdat.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ArticleVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Visibility",
                table: "articles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VisibilityChangedAt",
                table: "articles",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "articles");

            migrationBuilder.DropColumn(
                name: "VisibilityChangedAt",
                table: "articles");
        }
    }
}
