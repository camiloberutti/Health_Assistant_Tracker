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
        public DbSet<SleepDetailSnapshot> SleepDetailSnapshots { get; set; }
        public DbSet<StepSummary> StepSummaries { get; set; }
        public DbSet<ActivityDetailSnapshot> ActivityDetailSnapshots { get; set; }
        public DbSet<RecommendationFeedback> RecommendationFeedback { get; set; }

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

            modelBuilder.Entity<ActivityDetailSnapshot>()
                .HasIndex(d => d.ActivityId)
                .IsUnique();

            modelBuilder.Entity<ActivityDetailSnapshot>()
                .HasOne(d => d.Activity)
                .WithOne(a => a.DetailSnapshot)
                .HasForeignKey<ActivityDetailSnapshot>(d => d.ActivityId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SleepDetailSnapshot>()
                .HasIndex(s => s.Date)
                .IsUnique();

            modelBuilder.Entity<RecommendationFeedback>()
                .HasIndex(f => f.SubmittedUtc);
        }
    }
}
