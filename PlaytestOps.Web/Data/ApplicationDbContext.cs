using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PlaytestOps.Web.Models;

namespace PlaytestOps.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext(options)
{
    public DbSet<Playtest> Playtests => Set<Playtest>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<Playtest>(entity =>
        {
            entity.ToTable("Playtests", table =>
                table.HasCheckConstraint("CK_Playtests_Status", "Status IN (0, 1, 2, 3)"));
            entity.Property(test => test.Status).HasConversion<int>();
        });
    }
}
