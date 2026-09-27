using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlaytestOps.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUnityCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Assembly",
                table: "Playtests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FullName",
                table: "Playtests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAvailable",
                table: "Playtests",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "Playtests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProjectId",
                table: "Playtests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RunState",
                table: "Playtests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SkipReason",
                table: "Playtests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UniqueName",
                table: "Playtests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UnityProjects",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    UnityVersion = table.Column<string>(type: "TEXT", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnityProjects", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Playtests_ProjectId_Mode_UniqueName",
                table: "Playtests",
                columns: new[] { "ProjectId", "Mode", "UniqueName" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Playtests_UnityProjects_ProjectId",
                table: "Playtests",
                column: "ProjectId",
                principalTable: "UnityProjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Playtests_UnityProjects_ProjectId",
                table: "Playtests");

            migrationBuilder.DropTable(
                name: "UnityProjects");

            migrationBuilder.DropIndex(
                name: "IX_Playtests_ProjectId_Mode_UniqueName",
                table: "Playtests");

            migrationBuilder.DropColumn(
                name: "Assembly",
                table: "Playtests");

            migrationBuilder.DropColumn(
                name: "FullName",
                table: "Playtests");

            migrationBuilder.DropColumn(
                name: "IsAvailable",
                table: "Playtests");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "Playtests");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "Playtests");

            migrationBuilder.DropColumn(
                name: "RunState",
                table: "Playtests");

            migrationBuilder.DropColumn(
                name: "SkipReason",
                table: "Playtests");

            migrationBuilder.DropColumn(
                name: "UniqueName",
                table: "Playtests");
        }
    }
}
