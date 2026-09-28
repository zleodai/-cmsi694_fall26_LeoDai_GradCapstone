using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PlaytestOps.Web.Models;

namespace PlaytestOps.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext(options)
{
    public DbSet<Playtest> Playtests => Set<Playtest>();
    public DbSet<PlaytestRun> PlaytestRuns => Set<PlaytestRun>();
    public DbSet<UnityProject> UnityProjects => Set<UnityProject>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<PlaytestRun>(entity =>
        {
            entity.HasOne(run => run.Playtest).WithMany(test => test.Runs).HasForeignKey(run => run.PlaytestId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(run => new { run.PlaytestId, run.RequestedAt });
            entity.HasIndex(run => run.PlaytestId).IsUnique().HasFilter("State IN ('Queued', 'Pending', 'Running')");
            entity.HasIndex(run => new { run.State, run.RequestedAt, run.Id });
            entity.HasIndex(run => run.SessionId).IsUnique().HasFilter("State IN ('Pending', 'Running')");
            entity.ToTable("PlaytestRuns", table => table.HasCheckConstraint("CK_PlaytestRuns_State", "State IN ('Queued', 'Pending', 'Running', 'Passed', 'Failed')"));
        });
        builder.Entity<Playtest>(entity =>
        {
            entity.ToTable("Playtests", table =>
                table.HasCheckConstraint("CK_Playtests_Status", "Status IN (0, 1, 2, 3)"));
            entity.Property(test => test.Status).HasConversion<int>();
            entity.Property(test => test.IsAvailable).HasDefaultValue(true);
            entity.HasIndex(test => new { test.ProjectId, test.Mode, test.UniqueName }).IsUnique();
            entity.HasOne(test => test.Project).WithMany().HasForeignKey(test => test.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
