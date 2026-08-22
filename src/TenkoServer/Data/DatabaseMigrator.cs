using System;
using System.Data;
using Microsoft.EntityFrameworkCore;

namespace TenkoServer.Data
{
    /// <summary>
    /// EnsureCreated は既存 DB のスキーマを更新しないため、
    /// 旧 DB 向けの冪等なスキーマ移行を提供する。
    /// 新規 DB では EnsureCreated が最新スキーマを作成済みのため何もしない。
    /// </summary>
    public static class DatabaseMigrator
    {
        /// <summary>
        /// 旧 DB への冪等なスキーマ移行を実行する。EnsureCreated の後に呼び出すこと。
        /// </summary>
        public static void Migrate(TenkoDbContext db)
        {
            // アーカイブテーブルが無い旧 DB 向けに冪等な CREATE 文を実行する
            // (EnsureCreated は DB ファイルが既に存在する場合、新規テーブルも作成しない)
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS ArchivedScans (
                    Id TEXT NOT NULL CONSTRAINT PK_ArchivedScans PRIMARY KEY,
                    Timestamp TEXT NOT NULL,
                    Barcode TEXT NOT NULL,
                    Last5 INTEGER NOT NULL,
                    StudentName TEXT NOT NULL,
                    StudentCode TEXT NOT NULL,
                    Location TEXT NOT NULL,
                    ClientId TEXT NOT NULL,
                    ReceivedAt TEXT NOT NULL,
                    ScanDate TEXT NOT NULL,
                    SessionId TEXT NOT NULL,
                    SessionLabel TEXT NULL,
                    ClosedAt TEXT NOT NULL,
                    IsDeleted INTEGER NOT NULL DEFAULT 0,
                    DeletedAt TEXT NULL,
                    DeletedByClientId TEXT NOT NULL DEFAULT ''
                )
                """);
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_ArchivedScans_SessionId ON ArchivedScans (SessionId);");
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_ArchivedScans_ScanDate ON ArchivedScans (ScanDate);");

            // 論理削除 (ソフトデリート) 用の列を旧 DB へ追加する (冪等)
            EnsureColumn(db, "Scans", "IsDeleted", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(db, "Scans", "DeletedAt", "TEXT NULL");
            EnsureColumn(db, "Scans", "DeletedByClientId", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(db, "ArchivedScans", "IsDeleted", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(db, "ArchivedScans", "DeletedAt", "TEXT NULL");
            EnsureColumn(db, "ArchivedScans", "DeletedByClientId", "TEXT NOT NULL DEFAULT ''");
        }

        /// <summary>
        /// テーブルに列が存在しない場合のみ ALTER TABLE で追加する。
        /// table / column / columnDdl は内部固定値のみ渡されるため連結は安全。
        /// </summary>
        public static void EnsureColumn(TenkoDbContext db, string table, string column, string columnDdl)
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                connection.Open();
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('" + table + "') WHERE name = '" + column + "'";
                bool exists = Convert.ToInt64(command.ExecuteScalar()) > 0;
                if (!exists)
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE " + table + " ADD COLUMN " + column + " " + columnDdl);
                }
            }
        }
    }
}
