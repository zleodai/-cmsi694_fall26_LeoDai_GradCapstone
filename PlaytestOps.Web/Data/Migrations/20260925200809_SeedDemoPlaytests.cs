using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlaytestOps.Web.Data.Migrations;

public partial class SeedDemoPlaytests : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // One-time demo data. Application startup never upserts these rows.
        migrationBuilder.InsertData(
            table: "Playtests",
            columns: new[] { "Id", "Name", "Description", "Status" },
            values: new object[,]
            {
                { 1, "[Demo] Player movement", "Check that the player can move in each direction. Placeholder only; not connected to Unity.", 0 },
                { 2, "[Demo] Scene transition", "Check that moving between scenes preserves player state. Running status is illustrative only.", 1 },
                { 3, "[Demo] Damage handling", "Check that touching a hazard reduces player health. Failed status is illustrative only.", 2 },
                { 4, "[Demo] Item pickup", "Check that collecting an item updates the inventory. Passed status is illustrative only.", 3 }
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        for (var id = 1; id <= 4; id++)
            migrationBuilder.DeleteData(table: "Playtests", keyColumn: "Id", keyValue: id);
    }
}
