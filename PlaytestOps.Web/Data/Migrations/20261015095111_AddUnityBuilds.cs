using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlaytestOps.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUnityBuilds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UnityBuilds",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RequestKey = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectName = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Phase = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUpdateAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UnityVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ActiveTarget = table.Column<string>(type: "TEXT", nullable: false),
                    ProfilePath = table.Column<string>(type: "TEXT", nullable: false),
                    OutputPath = table.Column<string>(type: "TEXT", nullable: false),
                    ExecutablePath = table.Column<string>(type: "TEXT", nullable: false),
                    ManifestPath = table.Column<string>(type: "TEXT", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalWarnings = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalErrors = table.Column<int>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    LastLogSequence = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalLogCharacters = table.Column<int>(type: "INTEGER", nullable: false),
                    LogsTruncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    DroppedLogCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnityBuilds", x => x.Id);
                    table.CheckConstraint("CK_UnityBuilds_State", "State IN ('Pending', 'Running', 'Succeeded', 'Failed', 'Rejected', 'Unknown', 'Interrupted')");
                });

            migrationBuilder.CreateTable(
                name: "UnityBuildLogs",
                columns: table => new
                {
                    BuildId = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    TimestampUtc = table.Column<string>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    StackTrace = table.Column<string>(type: "TEXT", nullable: false),
                    Truncated = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnityBuildLogs", x => new { x.BuildId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_UnityBuildLogs_UnityBuilds_BuildId",
                        column: x => x.BuildId,
                        principalTable: "UnityBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UnityBuilds_ProjectId",
                table: "UnityBuilds",
                column: "ProjectId",
                unique: true,
                filter: "State IN ('Pending', 'Running', 'Unknown')");

            migrationBuilder.CreateIndex(
                name: "IX_UnityBuilds_ProjectId_RequestedAt",
                table: "UnityBuilds",
                columns: new[] { "ProjectId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UnityBuilds_RequestKey",
                table: "UnityBuilds",
                column: "RequestKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UnityBuildLogs");

            migrationBuilder.DropTable(
                name: "UnityBuilds");
        }
    }
}
