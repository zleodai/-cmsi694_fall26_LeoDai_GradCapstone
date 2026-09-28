using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlaytestOps.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRunQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PlaytestRuns_State",
                table: "PlaytestRuns");

            migrationBuilder.AddColumn<DateTime>(
                name: "DispatchedAt",
                table: "PlaytestRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlaytestRuns_PlaytestId",
                table: "PlaytestRuns",
                column: "PlaytestId",
                unique: true,
                filter: "State IN ('Queued', 'Pending', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_PlaytestRuns_State_RequestedAt_Id",
                table: "PlaytestRuns",
                columns: new[] { "State", "RequestedAt", "Id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_PlaytestRuns_State",
                table: "PlaytestRuns",
                sql: "State IN ('Queued', 'Pending', 'Running', 'Passed', 'Failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlaytestRuns_PlaytestId",
                table: "PlaytestRuns");

            migrationBuilder.DropIndex(
                name: "IX_PlaytestRuns_State_RequestedAt_Id",
                table: "PlaytestRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PlaytestRuns_State",
                table: "PlaytestRuns");

            migrationBuilder.DropColumn(
                name: "DispatchedAt",
                table: "PlaytestRuns");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PlaytestRuns_State",
                table: "PlaytestRuns",
                sql: "State IN ('Pending', 'Running', 'Passed', 'Failed')");
        }
    }
}
