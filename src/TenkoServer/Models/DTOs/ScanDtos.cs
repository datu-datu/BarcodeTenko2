using System;
using System.Collections.Generic;

namespace TenkoServer.Models.DTOs
{
    public class ScanItemDto
    {
        public string Id { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public ushort Last5 { get; set; }
        public string? StudentName { get; set; }
        public string? StudentCode { get; set; }
        public string Location { get; set; } = string.Empty;
        public bool NotificationSent { get; set; }
    }

    public class ScanBatchRequestDto
    {
        public string ClientId { get; set; } = string.Empty;
        public List<ScanItemDto> Records { get; set; } = new();
    }

    public class ScanBatchResponseDto
    {
        public bool Success { get; set; } = true;
        public int AcceptedCount { get; set; }
        public int DuplicateCount { get; set; }
        public int NotificationQueuedCount { get; set; }
        public string? Message { get; set; }
    }
}
