using System;
using System.Collections.Generic;

namespace TenkoServer.Models.DTOs
{
    public class DashboardSummaryDto
    {
        public int TotalScansToday { get; set; }
        public int UniqueStudentsToday { get; set; }
        public int TotalMasterStudents { get; set; }
        public int UnverifiedStudentsCount { get; set; }
        public double CompletionRatePercentage { get; set; }
        public Dictionary<string, int> ScansByLocation { get; set; } = new();
        public List<ScanItemDto> RecentScans { get; set; } = new();
    }

    public class UnverifiedStudentDto
    {
        public ushort StudentNumber { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
    }

    public class LoginRequestDto
    {
        public string Password { get; set; } = string.Empty;
    }

    public class LoginResponseDto
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
    }

    public class NotificationSettingsDto
    {
        public bool IsAutoSend { get; set; }
        public bool IsWebhookConfigured { get; set; }
    }

    public class UpdateNotificationSettingsRequestDto
    {
        public bool IsAutoSend { get; set; }
    }

    public class SendNotificationsRequestDto
    {
        public string? Date { get; set; }
        public List<string>? ScanIds { get; set; }
    }

    public class SendNotificationsResponseDto
    {
        public bool Success { get; set; }
        public int QueuedCount { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// セッション締めリクエスト
    /// </summary>
    public class CloseSessionRequestDto
    {
        public string? Label { get; set; }
    }

    /// <summary>
    /// セッション締めレスポンス
    /// </summary>
    public class CloseSessionResponseDto
    {
        public bool Success { get; set; } = true;
        public string? SessionId { get; set; }
        public int MovedCount { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// 締め済みセッションの概要
    /// </summary>
    public class SessionSummaryDto
    {
        public string SessionId { get; set; } = string.Empty;
        public string? Label { get; set; }
        public DateTime ClosedAt { get; set; }
        public int ScanCount { get; set; }
    }
}
