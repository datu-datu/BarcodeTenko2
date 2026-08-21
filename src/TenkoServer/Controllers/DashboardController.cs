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
        private readonly INotificationStateService _notificationState;
        private readonly INotificationQueue _notificationQueue;

        public DashboardController(
            TenkoDbContext db,
            IStudentMasterService studentMaster,
            INotificationStateService notificationState,
            INotificationQueue notificationQueue)
        {
            _db = db;
            _studentMaster = studentMaster;
            _notificationState = notificationState;
            _notificationQueue = notificationQueue;
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

            var scanIds = list.Select(s => s.Id).ToList();
            var sentScanIds = await _db.NotificationLogs
                .Where(l => scanIds.Contains(l.ScanId) && l.IsSuccess)
                .Select(l => l.ScanId)
                .Distinct()
                .ToListAsync();
            var sentSet = new HashSet<string>(sentScanIds);

            var result = list.Select(s => new ScanItemDto
            {
                Id = s.Id,
                Timestamp = s.Timestamp,
                Barcode = s.Barcode,
                Last5 = s.Last5,
                StudentName = s.StudentName,
                StudentCode = s.StudentCode,
                Location = s.Location,
                NotificationSent = sentSet.Contains(s.Id)
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

        [HttpGet("notification-settings")]
        public ActionResult<NotificationSettingsDto> GetNotificationSettings()
        {
            return Ok(new NotificationSettingsDto
            {
                IsAutoSend = _notificationState.IsAutoSendEnabled,
                IsWebhookConfigured = _notificationState.IsWebhookConfigured
            });
        }

        [HttpPost("notification-settings")]
        public ActionResult<NotificationSettingsDto> UpdateNotificationSettings([FromBody] UpdateNotificationSettingsRequestDto request)
        {
            if (request != null)
            {
                _notificationState.IsAutoSendEnabled = request.IsAutoSend;
            }

            return Ok(new NotificationSettingsDto
            {
                IsAutoSend = _notificationState.IsAutoSendEnabled,
                IsWebhookConfigured = _notificationState.IsWebhookConfigured
            });
        }

        [HttpPost("notifications/send-all")]
        public async Task<ActionResult<SendNotificationsResponseDto>> SendAllNotifications([FromBody] SendNotificationsRequestDto? request)
        {
            if (!_notificationState.IsWebhookConfigured)
            {
                return BadRequest(new SendNotificationsResponseDto
                {
                    Success = false,
                    QueuedCount = 0,
                    Message = "Webhook URL が設定されていません。"
                });
            }

            string targetDate = string.IsNullOrWhiteSpace(request?.Date) ? DateTime.Today.ToString("yyyy-MM-dd") : request.Date;

            // 対象日のスキャンを取得
            var scans = await _db.Scans
                .Where(s => s.ScanDate == targetDate)
                .ToListAsync();

            if (scans.Count == 0)
            {
                return Ok(new SendNotificationsResponseDto
                {
                    Success = true,
                    QueuedCount = 0,
                    Message = "送信対象のスキャンデータがありません。"
                });
            }

            // 既に送信成功している ScanId を取得
            var scanIds = scans.Select(s => s.Id).ToList();
            var alreadySentScanIds = await _db.NotificationLogs
                .Where(l => scanIds.Contains(l.ScanId) && l.IsSuccess)
                .Select(l => l.ScanId)
                .Distinct()
                .ToListAsync();
            var sentSet = new HashSet<string>(alreadySentScanIds);

            // 未送信のスキャンのみを抽出
            var toSend = scans.Where(s => !sentSet.Contains(s.Id)).ToList();
            int queuedCount = 0;

            foreach (var s in toSend)
            {
                var (_, _, studentEmail) = _studentMaster.GetStudentInfo(s.Last5);
                await _notificationQueue.QueueNotificationAsync(new NotificationTask
                {
                    ScanId = s.Id,
                    StudentNumber = s.Last5,
                    StudentName = s.StudentName,
                    StudentCode = s.StudentCode,
                    Location = s.Location,
                    ClientId = s.ClientId,
                    Timestamp = s.Timestamp,
                    ToEmail = studentEmail
                });
                queuedCount++;
            }

            return Ok(new SendNotificationsResponseDto
            {
                Success = true,
                QueuedCount = queuedCount,
                Message = $"{queuedCount} 件の通知送信をキューに投入しました。"
            });
        }

        [HttpPost("notifications/send-single")]
        public async Task<ActionResult<SendNotificationsResponseDto>> SendSingleNotification([FromBody] SendNotificationsRequestDto request)
        {
            if (!_notificationState.IsWebhookConfigured)
            {
                return BadRequest(new SendNotificationsResponseDto
                {
                    Success = false,
                    QueuedCount = 0,
                    Message = "Webhook URL が設定されていません。"
                });
            }

            string? scanId = request?.ScanIds?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(scanId))
            {
                return BadRequest(new SendNotificationsResponseDto
                {
                    Success = false,
                    QueuedCount = 0,
                    Message = "スキャンIDが指定されていません。"
                });
            }

            var scan = await _db.Scans.FirstOrDefaultAsync(s => s.Id == scanId);
            if (scan == null)
            {
                return NotFound(new SendNotificationsResponseDto
                {
                    Success = false,
                    QueuedCount = 0,
                    Message = "指定されたスキャンが見つかりません。"
                });
            }

            var (_, _, studentEmail) = _studentMaster.GetStudentInfo(scan.Last5);
            await _notificationQueue.QueueNotificationAsync(new NotificationTask
            {
                ScanId = scan.Id,
                StudentNumber = scan.Last5,
                StudentName = scan.StudentName,
                StudentCode = scan.StudentCode,
                Location = scan.Location,
                ClientId = scan.ClientId,
                Timestamp = scan.Timestamp,
                ToEmail = studentEmail
            });

            return Ok(new SendNotificationsResponseDto
            {
                Success = true,
                QueuedCount = 1,
                Message = $"{scan.StudentName}（学籍番号: {scan.Last5:D5}）への通知を送信キューに投入しました。"
            });
        }
    }
}
