using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TenkoServer.Data;
using TenkoServer.Models.DTOs;
using TenkoServer.Services;

namespace TenkoServer.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/v1/dashboard")]
    public class DashboardController : ControllerBase
    {
        private readonly TenkoDbContext _db;
        private readonly IStudentMasterService _studentMaster;

        public DashboardController(TenkoDbContext db, IStudentMasterService studentMaster)
        {
            _db = db;
            _studentMaster = studentMaster;
        }

        [HttpGet("summary")]
        public async Task<ActionResult<DashboardSummaryDto>> GetSummary([FromQuery] string? date)
        {
            string targetDate = string.IsNullOrWhiteSpace(date) ? DateTime.Today.ToString("yyyy-MM-dd") : date;

            var query = _db.Scans.Where(s => s.ScanDate == targetDate);

            int totalScans = await query.CountAsync();
            var scans = await query.ToListAsync();

            var uniqueStudents = scans.Select(s => s.Last5).Distinct().ToList();
            int uniqueCount = uniqueStudents.Count;

            var masterStudents = _studentMaster.GetAllStudents();
            int totalMasterCount = masterStudents.Count;
            int unverifiedCount = totalMasterCount > 0 ? Math.Max(0, totalMasterCount - uniqueCount) : 0;
            double completionRate = totalMasterCount > 0 ? Math.Round((double)uniqueCount / totalMasterCount * 100, 1) : 0;

            var byLocation = scans
                .GroupBy(s => string.IsNullOrWhiteSpace(s.Location) ? "未設定" : s.Location)
                .ToDictionary(g => g.Key, g => g.Count());

            var recentScans = scans
                .OrderByDescending(s => s.Timestamp)
                .Take(20)
                .Select(s => new ScanItemDto
                {
                    Id = s.Id,
                    Timestamp = s.Timestamp,
                    Barcode = s.Barcode,
                    Last5 = s.Last5,
                    StudentName = s.StudentName,
                    StudentCode = s.StudentCode,
                    Location = s.Location
                })
                .ToList();

            return Ok(new DashboardSummaryDto
            {
                TotalScansToday = totalScans,
                UniqueStudentsToday = uniqueCount,
                TotalMasterStudents = totalMasterCount,
                UnverifiedStudentsCount = unverifiedCount,
                CompletionRatePercentage = completionRate,
                ScansByLocation = byLocation,
                RecentScans = recentScans
            });
        }

        [HttpGet("scans")]
        public async Task<ActionResult<List<ScanItemDto>>> GetScans([FromQuery] string? date, [FromQuery] string? location, [FromQuery] string? search)
        {
            string targetDate = string.IsNullOrWhiteSpace(date) ? DateTime.Today.ToString("yyyy-MM-dd") : date;
            var query = _db.Scans.Where(s => s.ScanDate == targetDate);

            if (!string.IsNullOrWhiteSpace(location))
            {
                query = query.Where(s => s.Location == location);
            }

            var list = await query.OrderByDescending(s => s.Timestamp).ToListAsync();

            if (!string.IsNullOrWhiteSpace(search))
            {
                string lower = search.ToLower();
                list = list.Where(s =>
                    s.StudentName.ToLower().Contains(lower) ||
                    s.StudentCode.ToLower().Contains(lower) ||
                    s.Last5.ToString("D5").Contains(lower) ||
                    s.Barcode.Contains(lower) ||
                    s.Location.ToLower().Contains(lower)).ToList();
            }

            var result = list.Select(s => new ScanItemDto
            {
                Id = s.Id,
                Timestamp = s.Timestamp,
                Barcode = s.Barcode,
                Last5 = s.Last5,
                StudentName = s.StudentName,
                StudentCode = s.StudentCode,
                Location = s.Location
            }).ToList();

            return Ok(result);
        }

        [HttpGet("unverified")]
        public async Task<ActionResult<List<UnverifiedStudentDto>>> GetUnverified([FromQuery] string? date)
        {
            string targetDate = string.IsNullOrWhiteSpace(date) ? DateTime.Today.ToString("yyyy-MM-dd") : date;
            var scannedNumbers = await _db.Scans
                .Where(s => s.ScanDate == targetDate)
                .Select(s => s.Last5)
                .Distinct()
                .ToListAsync();

            var unverified = _studentMaster.GetUnverifiedStudents(scannedNumbers);
            return Ok(unverified);
        }

        [HttpGet("export/csv")]
        public async Task<IActionResult> ExportCsv([FromQuery] string? date, [FromQuery] string? location)
        {
            string targetDate = string.IsNullOrWhiteSpace(date) ? DateTime.Today.ToString("yyyy-MM-dd") : date;
            var query = _db.Scans.Where(s => s.ScanDate == targetDate);

            if (!string.IsNullOrWhiteSpace(location))
            {
                query = query.Where(s => s.Location == location);
            }

            var records = await query.OrderBy(s => s.Timestamp).ToListAsync();

            var sb = new StringBuilder();
            sb.AppendLine("Timestamp,StudentNumber,StudentName,StudentCode,Location,Barcode,ClientId");
            foreach (var r in records)
            {
                sb.AppendLine($"{r.Timestamp:yyyy-MM-dd HH:mm:ss},{r.Last5:D5},\"{r.StudentName}\",\"{r.StudentCode}\",\"{r.Location}\",\"{r.Barcode}\",\"{r.ClientId}\"");
            }

            string filename = $"tenko_export_{targetDate}_{(string.IsNullOrWhiteSpace(location) ? "all" : location)}.csv";
            return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", filename);
        }

        [HttpGet("export/bin")]
        public async Task<IActionResult> ExportBin([FromQuery] string? date, [FromQuery] string? location)
        {
            string targetDate = string.IsNullOrWhiteSpace(date) ? DateTime.Today.ToString("yyyy-MM-dd") : date;
            var query = _db.Scans.Where(s => s.ScanDate == targetDate);

            if (!string.IsNullOrWhiteSpace(location))
            {
                query = query.Where(s => s.Location == location);
            }

            var records = await query.OrderBy(s => s.Timestamp).ToListAsync();
            var bytes = records.SelectMany(r => BitConverter.GetBytes(r.Last5)).ToArray();

            string filename = $"ids_{targetDate}_{(string.IsNullOrWhiteSpace(location) ? "all" : location)}.bin";
            return File(bytes, "application/octet-stream", filename);
        }

        [HttpGet("logs")]
        public async Task<IActionResult> GetNotificationLogs([FromQuery] int limit = 50)
        {
            var logs = await _db.NotificationLogs
                .OrderByDescending(l => l.SentAt)
                .Take(limit)
                .ToListAsync();

            return Ok(logs);
        }
    }
}
