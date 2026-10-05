using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlaytestOps.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRunLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DroppedLogCount",
                table: "PlaytestRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LogsJson",
                table: "PlaytestRuns",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "LogsTruncated",
                table: "PlaytestRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DroppedLogCount",
                table: "PlaytestRuns");

            migrationBuilder.DropColumn(
                name: "LogsJson",
                table: "PlaytestRuns");

            migrationBuilder.DropColumn(
                name: "LogsTruncated",
                table: "PlaytestRuns");
        }
    }
}
