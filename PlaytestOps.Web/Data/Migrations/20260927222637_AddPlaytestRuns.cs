using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlaytestOps.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaytestRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlaytestRuns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PlaytestId = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    StackTrace = table.Column<string>(type: "TEXT", nullable: false),
                    Output = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaytestRuns", x => x.Id);
                    table.CheckConstraint("CK_PlaytestRuns_State", "State IN ('Pending', 'Running', 'Passed', 'Failed')");
                    table.ForeignKey(
                        name: "FK_PlaytestRuns_Playtests_PlaytestId",
                        column: x => x.PlaytestId,
                        principalTable: "Playtests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaytestRuns_PlaytestId_RequestedAt",
                table: "PlaytestRuns",
                columns: new[] { "PlaytestId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PlaytestRuns_SessionId",
                table: "PlaytestRuns",
                column: "SessionId",
                unique: true,
                filter: "State IN ('Pending', 'Running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaytestRuns");
        }
    }
}
