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
        public string Email { get; set; } = string.Empty;
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
}
