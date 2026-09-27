using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlaytestOps.Web.Data.Migrations;

public partial class RemoveDemoPlaytests : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Target only the original unlinked seed rows; never delete imported Unity tests.
        migrationBuilder.Sql("""
            DELETE FROM "Playtests"
            WHERE "ProjectId" IS NULL AND (
                ("Id" = 1 AND "Name" = '[Demo] Player movement') OR
                ("Id" = 2 AND "Name" = '[Demo] Scene transition') OR
                ("Id" = 3 AND "Name" = '[Demo] Damage handling') OR
                ("Id" = 4 AND "Name" = '[Demo] Item pickup')
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentional: a rollback must not recreate user-deleted demo data.
    }
}
