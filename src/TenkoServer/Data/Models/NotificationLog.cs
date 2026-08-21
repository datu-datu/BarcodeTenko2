using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TenkoServer.Data.Models
{
    [Table("NotificationLogs")]
    public class NotificationLog
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [MaxLength(100)]
        public string ScanId { get; set; } = string.Empty;

        public ushort StudentNumber { get; set; }

        [MaxLength(100)]
        public string ToEmail { get; set; } = string.Empty;

        public DateTime SentAt { get; set; } = DateTime.UtcNow;

        public bool IsSuccess { get; set; }

        public int StatusCode { get; set; }

        public string? ErrorMessage { get; set; }
    }
}
