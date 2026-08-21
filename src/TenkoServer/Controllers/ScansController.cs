using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TenkoServer.Data;
using TenkoServer.Data.Models;
using TenkoServer.Models.DTOs;
using TenkoServer.Services;

namespace TenkoServer.Controllers
{
    [ApiController]
    [Route("api/v1/scans")]
    public class ScansController : ControllerBase
    {
        private readonly TenkoDbContext _db;
        private readonly INotificationQueue _notificationQueue;
        private readonly INotificationStateService _notificationState;
        private readonly ILogger<ScansController> _logger;

        public ScansController(
            TenkoDbContext db,
            INotificationQueue notificationQueue,
            INotificationStateService notificationState,
            ILogger<ScansController> logger)
        {
            _db = db;
            _notificationQueue = notificationQueue;
            _notificationState = notificationState;
            _logger = logger;
        }

        [HttpPost]
        public async Task<ActionResult<ScanBatchResponseDto>> PostScans([FromBody] ScanBatchRequestDto request)
        {
            if (request == null || request.Records == null || request.Records.Count == 0)
            {
                return BadRequest(new ScanBatchResponseDto
                {
                    Success = false,
                    Message = "No records provided."
                });
            }

            int acceptedCount = 0;
            int duplicateCount = 0;
            int notificationCount = 0;

            // 同一日内で通知を送信済みの学生 (バッチ内重複対策のローカルセット)
            var notifiedToday = new HashSet<(string ScanDate, ushort Last5)>();

            foreach (var record in request.Records)
            {
                if (string.IsNullOrWhiteSpace(record.Id))
                {
                    record.Id = $"{DateTimeOffset.Now.ToUnixTimeMilliseconds()}_{record.Last5:D5}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
                }

                string scanDate = record.Timestamp != default
                    ? record.Timestamp.ToString("yyyy-MM-dd")
                    : DateTime.Today.ToString("yyyy-MM-dd");
                string location = record.Location ?? string.Empty;

                // 同一ID (再送の冪等性保証) または (同一日付・同一学籍番号・同一Location) の重複チェック。
                // Location違いのスキャンは誤スキャン削除後の正規打刻などを保全するため受け入れる。
                bool alreadyExists = await _db.Scans.AnyAsync(s =>
                    s.Id == record.Id ||
                    (s.ScanDate == scanDate && s.Last5 == record.Last5 && s.Location == location));

                if (alreadyExists)
                {
                    duplicateCount++;
                    continue;
                }

                // 個人情報保護: サーバーは学籍番号のみを扱う。
                // クライアントから送られた氏名・出席番号は保存せず破棄する。
                var entity = new ScanEntity
                {
                    Id = record.Id,
                    Timestamp = record.Timestamp != default ? record.Timestamp : DateTime.Now,
                    Barcode = record.Barcode,
                    Last5 = record.Last5,
                    StudentName = string.Empty,
                    StudentCode = string.Empty,
                    Location = location,
                    ClientId = request.ClientId ?? string.Empty,
                    ReceivedAt = DateTime.UtcNow,
                    ScanDate = scanDate
                };

                _db.Scans.Add(entity);
                acceptedCount++;

                // 自動送信モードが有効な場合のみ、当日初回のスキャンに限定して通知キューへ投入する
                if (_notificationState.IsAutoSendEnabled)
                {
                    var notifyKey = (scanDate, entity.Last5);
                    bool alreadyNotified = notifiedToday.Contains(notifyKey) ||
                        await _db.Scans.AnyAsync(s => s.ScanDate == scanDate && s.Last5 == entity.Last5);

                    if (!alreadyNotified)
                    {
                        await _notificationQueue.QueueNotificationAsync(new NotificationTask
                        {
                            ScanId = entity.Id,
                            StudentNumber = entity.Last5,
                            Location = entity.Location,
                            ClientId = entity.ClientId,
                            Timestamp = entity.Timestamp
                        });
                        notifiedToday.Add(notifyKey);
                        notificationCount++;
                    }
                }
            }

            if (acceptedCount > 0)
            {
                await _db.SaveChangesAsync();
                _logger.LogInformation("Accepted {AcceptedCount} scans, {DuplicateCount} duplicates, {QueuedCount} notifications from client '{ClientId}'",
                    acceptedCount, duplicateCount, notificationCount, request.ClientId);
            }

            return Ok(new ScanBatchResponseDto
            {
                Success = true,
                AcceptedCount = acceptedCount,
                DuplicateCount = duplicateCount,
                NotificationQueuedCount = notificationCount,
                Message = $"Processed {request.Records.Count} records."
            });
        }

        /// <summary>
        /// 誤スキャンレコードの削除 (端末からの同期用)。
        /// ScanId 指定のみを許可し、かつ要求元 ClientId と一致するレコードのみ削除するため、
        /// 他端末が登録した正当なスキャンが消えることはない。
        /// 存在しない Id / 他端末の Id はエラーではなく no-op として扱う。
        /// </summary>
        [HttpPost("delete")]
        public async Task<ActionResult<ScanDeleteResponseDto>> DeleteScans([FromBody] ScanDeleteRequestDto? request)
        {
            if (request == null || request.Ids == null || request.Ids.Count == 0)
            {
                return BadRequest(new ScanDeleteResponseDto
                {
                    Success = false,
                    DeletedCount = 0,
                    Message = "No ids provided."
                });
            }

            string clientId = request.ClientId ?? string.Empty;

            var targets = await _db.Scans
                .Where(s => request.Ids.Contains(s.Id) && s.ClientId == clientId)
                .ToListAsync();

            if (targets.Count > 0)
            {
                _db.Scans.RemoveRange(targets);
                await _db.SaveChangesAsync();
                _logger.LogInformation("Deleted {DeletedCount} scan(s) requested by client '{ClientId}' (requested {RequestedCount}).",
                    targets.Count, clientId, request.Ids.Count);
            }

            return Ok(new ScanDeleteResponseDto
            {
                Success = true,
                DeletedCount = targets.Count,
                Message = $"Deleted {targets.Count} record(s)."
            });
        }
    }
}
