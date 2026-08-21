using Microsoft.EntityFrameworkCore;
using TenkoServer.Data.Models;

namespace TenkoServer.Data
{
    public class TenkoDbContext : DbContext
    {
        public TenkoDbContext(DbContextOptions<TenkoDbContext> options) : base(options)
        {
        }

        public DbSet<ScanEntity> Scans => Set<ScanEntity>();
        public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<ScanEntity>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => new { e.ScanDate, e.Last5 });
                entity.HasIndex(e => e.Timestamp);
                entity.HasIndex(e => e.Location);
            });

            modelBuilder.Entity<NotificationLog>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.ScanId);
                entity.HasIndex(e => e.SentAt);
            });
        }
    }
}
