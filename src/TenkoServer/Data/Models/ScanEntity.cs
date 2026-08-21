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

        [MaxLength(50)]
        public string StudentName { get; set; } = string.Empty;

        [MaxLength(20)]
        public string StudentCode { get; set; } = string.Empty;

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
    }
}
