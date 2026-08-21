using System;
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
        private readonly IStudentMasterService _studentMaster;
        private readonly ILogger<ScansController> _logger;

        public ScansController(
            TenkoDbContext db,
            INotificationQueue notificationQueue,
            INotificationStateService notificationState,
            IStudentMasterService studentMaster,
            ILogger<ScansController> logger)
        {
            _db = db;
            _notificationQueue = notificationQueue;
            _notificationState = notificationState;
            _studentMaster = studentMaster;
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

            foreach (var record in request.Records)
            {
                if (string.IsNullOrWhiteSpace(record.Id))
                {
                    record.Id = $"{DateTimeOffset.Now.ToUnixTimeMilliseconds()}_{record.Last5:D5}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
                }

                string scanDate = record.Timestamp != default
                    ? record.Timestamp.ToString("yyyy-MM-dd")
                    : DateTime.Today.ToString("yyyy-MM-dd");

                // 同一ID または (同一日付 かつ 同一学籍番号) の重複チェック
                bool alreadyExists = await _db.Scans.AnyAsync(s =>
                    s.Id == record.Id ||
                    (s.ScanDate == scanDate && s.Last5 == record.Last5));

                if (alreadyExists)
                {
                    duplicateCount++;
                    continue;
                }

                // サーバー側学生マスタから氏名・出席番号・メールアドレスを解決
                var (masterName, masterCode, studentEmail) = _studentMaster.GetStudentInfo(record.Last5);
                string studentName = !string.IsNullOrWhiteSpace(masterName) ? masterName : (!string.IsNullOrWhiteSpace(record.StudentName) ? record.StudentName : "未登録");
                string studentCode = !string.IsNullOrWhiteSpace(masterCode) ? masterCode : (!string.IsNullOrWhiteSpace(record.StudentCode) ? record.StudentCode : string.Empty);

                var entity = new ScanEntity
                {
                    Id = record.Id,
                    Timestamp = record.Timestamp != default ? record.Timestamp : DateTime.Now,
                    Barcode = record.Barcode,
                    Last5 = record.Last5,
                    StudentName = studentName,
                    StudentCode = studentCode,
                    Location = record.Location ?? string.Empty,
                    ClientId = request.ClientId ?? string.Empty,
                    ReceivedAt = DateTime.UtcNow,
                    ScanDate = scanDate
                };

                _db.Scans.Add(entity);
                acceptedCount++;

                // 自動送信モードが有効な場合のみ通知キューへ投入
                if (_notificationState.IsAutoSendEnabled)
                {
                    await _notificationQueue.QueueNotificationAsync(new NotificationTask
                    {
                        ScanId = entity.Id,
                        StudentNumber = entity.Last5,
                        StudentName = studentName,
                        StudentCode = studentCode,
                        Location = entity.Location,
                        ClientId = entity.ClientId,
                        Timestamp = entity.Timestamp,
                        ToEmail = studentEmail
                    });
                    notificationCount++;
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
    }
}
