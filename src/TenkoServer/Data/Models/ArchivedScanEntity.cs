using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TenkoServer.Data.Models
{
    /// <summary>
    /// 「セッション締め」操作で退避された点呼データ。
    /// 重複判定 (PostScans) の対象はアクティブな Scans のみのため、
    /// 締め後は同一学生の再点呼が可能になる。
    /// </summary>
    [Table("ArchivedScans")]
    public class ArchivedScanEntity
    {
        [Key]
        [MaxLength(100)]
        public string Id { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; }

        [MaxLength(20)]
        public string Barcode { get; set; } = string.Empty;

        public ushort Last5 { get; set; }

        [MaxLength(50)]
        public string StudentName { get; set; } = string.Empty;

        [MaxLength(20)]
        public string StudentCode { get; set; } = string.Empty;

        [MaxLength(50)]
        public string Location { get; set; } = string.Empty;

        [MaxLength(50)]
        public string ClientId { get; set; } = string.Empty;

        public DateTime ReceivedAt { get; set; }

        [MaxLength(10)]
        public string ScanDate { get; set; } = string.Empty;

        /// <summary>
        /// 締め操作ごとに発行されるセッション識別子 (GUID)
        /// </summary>
        [MaxLength(36)]
        public string SessionId { get; set; } = string.Empty;

        /// <summary>
        /// 締め時に付与される任意ラベル (例: 午前 / 午後)
        /// </summary>
        [MaxLength(50)]
        public string? SessionLabel { get; set; }

        public DateTime ClosedAt { get; set; }
    }
}
