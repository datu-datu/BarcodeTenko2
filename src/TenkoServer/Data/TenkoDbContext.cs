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
        public DbSet<ArchivedScanEntity> ArchivedScans => Set<ArchivedScanEntity>();
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

                // 論理削除済みレコードを全クエリから除外する。
                // これにより重複チェック・未点呼判定・エクスポート等が自動的に削除済みを無視する。
                // 管理パネル表示時は IgnoreQueryFilters() で全件取得する。
                // (EF Core の FindAsync はクエリフィルタを適用しない点に注意)
                entity.HasQueryFilter(e => !e.IsDeleted);
            });

            modelBuilder.Entity<ArchivedScanEntity>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.SessionId);
                entity.HasIndex(e => e.ScanDate);
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
