using Microsoft.EntityFrameworkCore;
using GarminTempApi.Models;

namespace GarminTempApi.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> opts) : base(opts) { }

        public DbSet<Activity> Activities { get; set; }
        public DbSet<ActivityPoint> ActivityPoints { get; set; }
        public DbSet<SleepSummary> SleepSummaries { get; set; }
        public DbSet<StepSummary> StepSummaries { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Activity>()
                .HasMany(a => a.Points)
                .WithOne(p => p.Activity)
                .HasForeignKey(p => p.ActivityId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Activity>()
                .HasIndex(a => a.ExternalId)
                .IsUnique();

            modelBuilder.Entity<SleepSummary>()
                .HasIndex(s => s.Date)
                .IsUnique();

            modelBuilder.Entity<StepSummary>()
                .HasIndex(s => s.Date)
                .IsUnique();
        }
    }
}
