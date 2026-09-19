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
            // 個人情報保護のため StudentName / StudentCode 列は持たない。
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS ArchivedScans (
                    Id TEXT NOT NULL CONSTRAINT PK_ArchivedScans PRIMARY KEY,
                    Timestamp TEXT NOT NULL,
                    Barcode TEXT NOT NULL,
                    Last5 INTEGER NOT NULL,
                    Location TEXT NOT NULL,
                    ClientId TEXT NOT NULL,
                    ReceivedAt TEXT NOT NULL,
                    ScanDate TEXT NOT NULL,
                    SessionId TEXT NOT NULL,
                    SessionLabel TEXT NULL,
                    ClosedAt TEXT NOT NULL,
                    IsDeleted INTEGER NOT NULL DEFAULT 0,
                    DeletedAt TEXT NULL,
                    DeletedByClientId TEXT NOT NULL DEFAULT '',
                    DeletedReason TEXT NULL,
                    RestoredAt TEXT NULL,
                    RestoredByClientId TEXT NULL
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

            // 削除監査 (理由・復元情報) 用の列を追加する (冪等)
            EnsureColumn(db, "Scans", "DeletedReason", "TEXT NULL");
            EnsureColumn(db, "Scans", "RestoredAt", "TEXT NULL");
            EnsureColumn(db, "Scans", "RestoredByClientId", "TEXT NULL");
            EnsureColumn(db, "ArchivedScans", "DeletedReason", "TEXT NULL");
            EnsureColumn(db, "ArchivedScans", "RestoredAt", "TEXT NULL");
            EnsureColumn(db, "ArchivedScans", "RestoredByClientId", "TEXT NULL");

            // 個人情報保護: 旧スキーマに StudentName / StudentCode 列が残っている場合、
            // SQLite での列削除は行の再作成を伴うため、代わりに既存データを空文字で上書きして
            // 個人情報を確実に消去する (列は残るが氏名データは残らない)。冪等。
            PurgePersonalInfoColumns(db);
        }

        /// <summary>
        /// 旧 DB の StudentName / StudentCode 列に残存する個人情報を空文字で上書きする。
        /// 列が存在しない場合は何もしない。
        /// </summary>
        private static void PurgePersonalInfoColumns(TenkoDbContext db)
        {
            foreach (var table in new[] { "Scans", "ArchivedScans" })
            {
                if (!ColumnExists(db, table, "StudentName")) continue;

                db.Database.ExecuteSqlRaw(
                    "UPDATE " + table + " SET StudentName = '', StudentCode = '' " +
                    "WHERE StudentName <> '' OR StudentCode <> ''");
            }
        }

        /// <summary>テーブルに列が存在するかどうかを返す。</summary>
        private static bool ColumnExists(TenkoDbContext db, string table, string column)
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                connection.Open();
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('" + table + "') WHERE name = '" + column + "'";
                return Convert.ToInt64(command.ExecuteScalar()) > 0;
            }
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
