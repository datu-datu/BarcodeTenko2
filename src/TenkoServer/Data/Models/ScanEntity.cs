using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TenkoServer.Data.Models
{
    [Table("Scans")]
    public class ScanEntity
    {
        [Key]
        [MaxLength(100)]
        public string Id { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; }

        [MaxLength(20)]
        public string Barcode { get; set; } = string.Empty;

        public ushort Last5 { get; set; }

        // 個人情報保護: サーバーは氏名・出席番号を保持しない。
        // 保持するのは 時刻(Timestamp) / 場所(Location) / 学籍番号下5桁(Last5) のみ。

        [MaxLength(50)]
        public string Location { get; set; } = string.Empty;

        [MaxLength(50)]
        public string ClientId { get; set; } = string.Empty;

        public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 当日（日付文字列: yyyy-MM-dd）
        /// </summary>
        [MaxLength(10)]
        public string ScanDate { get; set; } = string.Empty;

        /// <summary>
        /// 論理削除フラグ。
        /// 重複チェック・未点呼判定・エクスポート等からは除外されるが、
        /// データ自体は管理パネル向けに保持される (git diff 風の削除表示用)。
        /// </summary>
        public bool IsDeleted { get; set; }

        /// <summary>論理削除日時 (UTC)。未削除時は null</summary>
        public DateTime? DeletedAt { get; set; }

        /// <summary>削除を要求したクライアント</summary>
        [MaxLength(50)]
        public string DeletedByClientId { get; set; } = string.Empty;

        /// <summary>削除理由 (監査用。管理パネルでの削除時に入力)</summary>
        [MaxLength(200)]
        public string? DeletedReason { get; set; }

        /// <summary>復元日時 (UTC)。未復元時は null</summary>
        public DateTime? RestoredAt { get; set; }

        /// <summary>復元を実行した主体 (例: admin-panel)</summary>
        [MaxLength(50)]
        public string? RestoredByClientId { get; set; }
    }
}
