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
using TenkoServer.Data.Models;
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
            // 未点呼は「現在のセッション」単位で判定する。
            // Scans テーブルにはアクティブセッションのデータのみが存在するため、
            // セッション締め後は同じ学生が再度未点呼リストに現れる（仕様）。
            string targetDate = string.IsNullOrWhiteSpace(date) ? DateTime.Today.ToString("yyyy-MM-dd") : date;
            var scannedNumbers = await _db.Scans
                .Where(s => s.ScanDate == targetDate)
                .Select(s => s.Last5)
                .Distinct()
                .ToListAsync();

            var unverified = _studentMaster.GetUnverifiedStudents(scannedNumbers);
            return Ok(unverified);
        }

        /// <summary>
        /// 現在のセッションを締めて、全アクティブデータを ArchivedScans へ退避する。
        /// 退避後、Scans は空になるため重複チェックがリセットされ、
        /// 同じ学生でも次のセッションで再度点呼できるようになる (BUG-02 対策)。
        /// </summary>
        [HttpPost("sessions/close")]
        public async Task<ActionResult<CloseSessionResponseDto>> CloseSession([FromBody] CloseSessionRequestDto? request)
        {
            var actives = await _db.Scans.OrderBy(s => s.Timestamp).ToListAsync();

            if (actives.Count == 0)
            {
                return Ok(new CloseSessionResponseDto
                {
                    Success = true,
                    MovedCount = 0,
                    Message = "締め対象の点呼データがありません。"
                });
            }

            string sessionId = Guid.NewGuid().ToString("N");
            DateTime closedAt = DateTime.UtcNow;
            string? label = string.IsNullOrWhiteSpace(request?.Label) ? null : request!.Label!.Trim();

            await using var transaction = await _db.Database.BeginTransactionAsync();
            try
            {
                _db.ArchivedScans.AddRange(actives.Select(s => new ArchivedScanEntity
                {
                    Id = s.Id,
                    Timestamp = s.Timestamp,
                    Barcode = s.Barcode,
                    Last5 = s.Last5,
                    StudentName = s.StudentName,
                    StudentCode = s.StudentCode,
                    Location = s.Location,
                    ClientId = s.ClientId,
                    ReceivedAt = s.ReceivedAt,
                    ScanDate = s.ScanDate,
                    SessionId = sessionId,
                    SessionLabel = label,
                    ClosedAt = closedAt
                }));
                _db.Scans.RemoveRange(actives);
                await _db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }

            return Ok(new CloseSessionResponseDto
            {
                Success = true,
                SessionId = sessionId,
                MovedCount = actives.Count,
                Message = $"{actives.Count} 件の点呼データを「{label ?? "名称未設定"}」セッションとしてアーカイブしました。"
            });
        }

        /// <summary>
        /// 締め済みセッションの一覧を返す。
        /// </summary>
        [HttpGet("sessions")]
        public async Task<ActionResult<List<SessionSummaryDto>>> GetSessions()
        {
            var sessions = await _db.ArchivedScans
                .GroupBy(a => new { a.SessionId, a.SessionLabel, a.ClosedAt })
                .Select(g => new SessionSummaryDto
                {
                    SessionId = g.Key.SessionId,
                    Label = g.Key.SessionLabel,
                    ClosedAt = g.Key.ClosedAt,
                    ScanCount = g.Count()
                })
                .OrderByDescending(s => s.ClosedAt)
                .ToListAsync();

            return Ok(sessions);
        }

        [HttpGet("export/csv")]
        public async Task<IActionResult> ExportCsv([FromQuery] string? date, [FromQuery] string? location, [FromQuery] string? session = null)
        {
            var records = await CollectExportRecordsAsync(date, location, session);
            string targetDate = ResolveTargetDate(date);

            var sb = new StringBuilder();
            sb.AppendLine("Timestamp,StudentNumber,StudentName,StudentCode,Location,Barcode,ClientId");
            foreach (var r in records)
            {
                sb.AppendLine($"{r.Timestamp:yyyy-MM-dd HH:mm:ss},{r.Last5:D5},\"{r.StudentName}\",\"{r.StudentCode}\",\"{r.Location}\",\"{r.Barcode}\",\"{r.ClientId}\"");
            }

            string filename = BuildExportFilename("tenko_export", targetDate, location, session, ".csv");
            return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", filename);
        }

        [HttpGet("export/bin")]
        public async Task<IActionResult> ExportBin([FromQuery] string? date, [FromQuery] string? location, [FromQuery] string? session = null)
        {
            var records = await CollectExportRecordsAsync(date, location, session);
            string targetDate = ResolveTargetDate(date);

            var bytes = records.SelectMany(r => BitConverter.GetBytes(r.Last5)).ToArray();
            string filename = BuildExportFilename("ids", targetDate, location, session, ".bin");
            return File(bytes, "application/octet-stream", filename);
        }

        /// <summary>
        /// エクスポート対象レコードを取得する。
        /// - session 指定時: 該当アーカイブセッションのみ
        /// - 未指定時: 対象日のアクティブ + アーカイブを合算（締め済み過日データも DL 可能にする）
        /// </summary>
        private async Task<List<ScanEntity>> CollectExportRecordsAsync(string? date, string? location, string? session)
        {
            if (!string.IsNullOrWhiteSpace(session))
            {
                var archivedOnly = await _db.ArchivedScans
                    .Where(a => a.SessionId == session)
                    .OrderBy(a => a.Timestamp)
                    .ToListAsync();
                return archivedOnly.Select(ToScanShape).ToList();
            }

            string targetDate = ResolveTargetDate(date);

            IQueryable<ScanEntity> activeQuery = _db.Scans.Where(s => s.ScanDate == targetDate);
            IQueryable<ArchivedScanEntity> archivedQuery = _db.ArchivedScans.Where(a => a.ScanDate == targetDate);

            if (!string.IsNullOrWhiteSpace(location))
            {
                activeQuery = activeQuery.Where(s => s.Location == location);
                archivedQuery = archivedQuery.Where(a => a.Location == location);
            }

            var activeList = await activeQuery.OrderBy(s => s.Timestamp).ToListAsync();
            var archivedList = await archivedQuery.OrderBy(a => a.Timestamp).ToListAsync();

            return activeList.Concat(archivedList.Select(ToScanShape))
                .OrderBy(s => s.Timestamp)
                .ToList();
        }

        private static ScanEntity ToScanShape(ArchivedScanEntity a) => new()
        {
            Id = a.Id,
            Timestamp = a.Timestamp,
            Barcode = a.Barcode,
            Last5 = a.Last5,
            StudentName = a.StudentName,
            StudentCode = a.StudentCode,
            Location = a.Location,
            ClientId = a.ClientId,
            ReceivedAt = a.ReceivedAt,
            ScanDate = a.ScanDate
        };

        private static string ResolveTargetDate(string? date)
            => string.IsNullOrWhiteSpace(date) ? DateTime.Today.ToString("yyyy-MM-dd") : date;

        private static string BuildExportFilename(string prefix, string targetDate, string? location, string? session, string extension)
        {
            if (!string.IsNullOrWhiteSpace(session))
            {
                string shortId = session.Length > 8 ? session.Substring(0, 8) : session;
                return $"{prefix}_session_{shortId}{extension}";
            }
            return $"{prefix}_{targetDate}_{(string.IsNullOrWhiteSpace(location) ? "all" : location)}{extension}";
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
                await _notificationQueue.QueueNotificationAsync(new NotificationTask
                {
                    ScanId = s.Id,
                    StudentNumber = s.Last5,
                    StudentName = s.StudentName,
                    StudentCode = s.StudentCode,
                    Location = s.Location,
                    ClientId = s.ClientId,
                    Timestamp = s.Timestamp
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

            await _notificationQueue.QueueNotificationAsync(new NotificationTask
            {
                ScanId = scan.Id,
                StudentNumber = scan.Last5,
                StudentName = scan.StudentName,
                StudentCode = scan.StudentCode,
                Location = scan.Location,
                ClientId = scan.ClientId,
                Timestamp = scan.Timestamp
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
