using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TenkoServer.Controllers;
using TenkoServer.Data;
using TenkoServer.Data.Models;
using TenkoServer.Models;
using TenkoServer.Models.DTOs;
using TenkoServer.Services;
using Xunit;

namespace Tenko.Tests
{
    public class TenkoServerTests : IDisposable
    {
        private readonly string _dbName;
        private readonly TenkoDbContext _db;
        private readonly TenkoServerOptions _options;

        public TenkoServerTests()
        {
            _dbName = "TestDb_" + Guid.NewGuid().ToString("N");
            var dbOptions = new DbContextOptionsBuilder<TenkoDbContext>()
                .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), _dbName + ".db")}")
                .Options;

            _db = new TenkoDbContext(dbOptions);
            _db.Database.EnsureCreated();

            _options = new TenkoServerOptions
            {
                ApiKey = "test-api-key",
                AdminPassword = "test-admin-password",
                EnableNotifications = true
            };
        }

        public void Dispose()
        {
            _db.Database.EnsureDeleted();
            _db.Dispose();
        }

        [Fact]
        public async Task ScansController_AcceptsAndDeduplicatesRecords()
        {
            var queue = new NotificationQueue();
            var notifState = new NotificationStateService(new MockOptionsMonitor<TenkoServerOptions>(_options));

            var controller = new ScansController(_db, queue, notifState, new ScanAcceptanceService(), NullLogger<ScansController>.Instance);

            var batch = new ScanBatchRequestDto
            {
                ClientId = "client-building2",
                Records = new List<ScanItemDto>
                {
                    new ScanItemDto
                    {
                        Id = "scan-1",
                        Barcode = "21021",
                        Last5 = 21021,
                        StudentName = "太郎 花子", // クライアントから送ってもサーバーは保存しない
                        StudentCode = "4D23",
                        Location = "2棟2階",
                        Timestamp = new DateTime(2026, 8, 21, 10, 0, 0)
                    },
                    new ScanItemDto
                    {
                        Id = "scan-2",
                        Barcode = "23213",
                        Last5 = 23213,
                        StudentName = null, // 個人情報なし
                        StudentCode = null, // 個人情報なし
                        Location = "2棟2階",
                        Timestamp = new DateTime(2026, 8, 21, 10, 5, 0)
                    }
                }
            };

            // 初回投入
            var actionResult = await controller.PostScans(batch);
            var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
            var response = Assert.IsType<ScanBatchResponseDto>(okResult.Value);

            Assert.True(response.Success);
            Assert.Equal(2, response.AcceptedCount);
            Assert.Equal(0, response.DuplicateCount);
            Assert.Equal(2, response.NotificationQueuedCount);

            // DB に保存されたか確認 (氏名・出席番号はクライアントから送られていても破棄される)
            Assert.Equal(2, await _db.Scans.CountAsync());
            var saved = await _db.Scans.ToListAsync();
            Assert.All(saved, s =>
            {
                Assert.Equal(string.Empty, s.StudentName);
                Assert.Equal(string.Empty, s.StudentCode);
            });

            // キューから通知タスクを取り出せるか確認
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var task1 = await queue.DequeueNotificationAsync(cts.Token);
            Assert.Equal(21021, task1.StudentNumber);
            Assert.Equal("2棟2階", task1.Location);

            // 同一データを再送信（重複排除テスト）
            var duplicateActionResult = await controller.PostScans(batch);
            var dupOkResult = Assert.IsType<OkObjectResult>(duplicateActionResult.Result);
            var dupResponse = Assert.IsType<ScanBatchResponseDto>(dupOkResult.Value);

            Assert.True(dupResponse.Success);
            Assert.Equal(0, dupResponse.AcceptedCount);
            Assert.Equal(2, dupResponse.DuplicateCount);
            Assert.Equal(0, dupResponse.NotificationQueuedCount);
        }

        [Fact]
        public async Task ScansController_AcceptsDifferentLocation_AndSuppressesDuplicateNotification()
        {
            var queue = new NotificationQueue();
            var notifState = new NotificationStateService(new MockOptionsMonitor<TenkoServerOptions>(_options));

            var controller = new ScansController(_db, queue, notifState, new ScanAcceptanceService(), NullLogger<ScansController>.Instance);

            ScanBatchRequestDto CreateBatch(string id, string location) => new ScanBatchRequestDto
            {
                ClientId = "terminal-A",
                Records = new List<ScanItemDto>
                {
                    new ScanItemDto
                    {
                        Id = id,
                        Barcode = "21021",
                        Last5 = 21021,
                        Location = location,
                        Timestamp = new DateTime(2026, 8, 22, 9, 0, 0)
                    }
                }
            };

            // 1件目: 当日初回スキャン -> 受理 + 通知キュー投入
            var r1 = Assert.IsType<OkObjectResult>((await controller.PostScans(CreateBatch("s-1", "2棟2階"))).Result);
            var resp1 = Assert.IsType<ScanBatchResponseDto>(r1.Value);
            Assert.Equal(1, resp1.AcceptedCount);
            Assert.Equal(1, resp1.NotificationQueuedCount);

            // 2件目: 同日・同学籍番号だが Location 違い -> 受理されるが通知は抑止される
            var r2 = Assert.IsType<OkObjectResult>((await controller.PostScans(CreateBatch("s-2", "5棟1階"))).Result);
            var resp2 = Assert.IsType<ScanBatchResponseDto>(r2.Value);
            Assert.Equal(1, resp2.AcceptedCount);
            Assert.Equal(0, resp2.DuplicateCount);
            Assert.Equal(0, resp2.NotificationQueuedCount);

            // 3件目: 同日・同学籍番号・同一 Location -> 重複拒否
            var r3 = Assert.IsType<OkObjectResult>((await controller.PostScans(CreateBatch("s-3", "2棟2階"))).Result);
            var resp3 = Assert.IsType<ScanBatchResponseDto>(r3.Value);
            Assert.Equal(0, resp3.AcceptedCount);
            Assert.Equal(1, resp3.DuplicateCount);
            Assert.Equal(0, resp3.NotificationQueuedCount);

            Assert.Equal(2, await _db.Scans.CountAsync());
        }

        [Fact]
        public async Task ScansController_DeleteScans_RemovesOnlyMatchingClientRecords()
        {
            var queue = new NotificationQueue();
            var notifState = new NotificationStateService(new MockOptionsMonitor<TenkoServerOptions>(_options));

            var controller = new ScansController(_db, queue, notifState, new ScanAcceptanceService(), NullLogger<ScansController>.Instance);

            DateTime ts = new DateTime(2026, 8, 22, 9, 0, 0);
            _db.Scans.Add(new ScanEntity
            {
                Id = "own-1", Timestamp = ts, Barcode = "21021", Last5 = 21021,
                StudentName = "A", StudentCode = "c1", Location = "2棟2階",
                ClientId = "terminal-A", ReceivedAt = DateTime.UtcNow, ScanDate = "2026-08-22"
            });
            _db.Scans.Add(new ScanEntity
            {
                Id = "other-1", Timestamp = ts, Barcode = "23213", Last5 = 23213,
                StudentName = "B", StudentCode = "c2", Location = "2棟2階",
                ClientId = "terminal-B", ReceivedAt = DateTime.UtcNow, ScanDate = "2026-08-22"
            });
            await _db.SaveChangesAsync();

            var result = await controller.DeleteScans(new ScanDeleteRequestDto
            {
                ClientId = "terminal-A",
                Ids = new List<string> { "own-1", "other-1", "unknown-1" }
            });

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var response = Assert.IsType<ScanDeleteResponseDto>(ok.Value);
            Assert.True(response.Success);
            // 自端末のレコードのみ削除。他端末のレコードと存在しない Id は no-op
            Assert.Equal(1, response.DeletedCount);

            Assert.Null(await _db.Scans.FindAsync("own-1"));
            Assert.NotNull(await _db.Scans.FindAsync("other-1"));
        }

        [Fact]
        public async Task DashboardController_ComputesSummaryAndExports()
        {
            var optionsWrapper = Options.Create(_options);
            var mockEnv = new MockWebHostEnvironment();
            var studentMaster = new StudentMasterService(optionsWrapper, NullLogger<StudentMasterService>.Instance, mockEnv);

            // テストデータを DB へ挿入 (新仕様では氏名・出席番号は保存されない)
            _db.Scans.AddRange(
                new TenkoServer.Data.Models.ScanEntity
                {
                    Id = "scan-101",
                    Timestamp = new DateTime(2026, 8, 21, 9, 0, 0),
                    Barcode = "21021",
                    Last5 = 21021,
                    StudentName = string.Empty,
                    StudentCode = string.Empty,
                    Location = "2棟2階",
                    ScanDate = "2026-08-21"
                },
                new TenkoServer.Data.Models.ScanEntity
                {
                    Id = "scan-102",
                    Timestamp = new DateTime(2026, 8, 21, 9, 30, 0),
                    Barcode = "23213",
                    Last5 = 23213,
                    StudentName = string.Empty,
                    StudentCode = string.Empty,
                    Location = "本部横",
                    ScanDate = "2026-08-21"
                }
            );
            await _db.SaveChangesAsync();

            var notifState = new NotificationStateService(new MockOptionsMonitor<TenkoServerOptions>(_options));
            var queue = new NotificationQueue();
            var controller = new DashboardController(_db, studentMaster, notifState, queue, new ScanAcceptanceService(), NullLogger<DashboardController>.Instance);

            // サマリー取得
            var summaryResult = await controller.GetSummary("2026-08-21");
            var okSummary = Assert.IsType<OkObjectResult>(summaryResult.Result);
            var summary = Assert.IsType<DashboardSummaryDto>(okSummary.Value);

            Assert.Equal(2, summary.TotalScansToday);
            Assert.Equal(2, summary.UniqueStudentsToday);
            Assert.Equal(2, summary.ScansByLocation.Count);
            Assert.True(summary.ScansByLocation.ContainsKey("2棟2階"));
            Assert.True(summary.ScansByLocation.ContainsKey("本部横"));

            // スキャン一覧取得 (学籍番号で検索)
            var scansResult = await controller.GetScans("2026-08-21", null, "21021");
            var okScans = Assert.IsType<OkObjectResult>(scansResult.Result);
            var scansList = Assert.IsType<List<ScanItemDto>>(okScans.Value);
            Assert.Single(scansList);
            Assert.Equal(21021, scansList[0].Last5);
            Assert.False(scansList[0].NotificationSent);

            // 通知モード切り替え & 手動一括送信テスト
            var settingsResult = controller.GetNotificationSettings();
            var okSettings = Assert.IsType<OkObjectResult>(settingsResult.Result);
            var settings = Assert.IsType<NotificationSettingsDto>(okSettings.Value);
            Assert.True(settings.IsAutoSend);

            controller.UpdateNotificationSettings(new UpdateNotificationSettingsRequestDto { IsAutoSend = false });
            Assert.False(notifState.IsAutoSendEnabled);

            // CSV エクスポート
            var csvResult = await controller.ExportCsv("2026-08-21", null);
            var fileContentResult = Assert.IsType<FileContentResult>(csvResult);
            string csvContent = System.Text.Encoding.UTF8.GetString(fileContentResult.FileContents);
            Assert.Contains("21021", csvContent);

            // BIN エクスポート (UInt16 Little Endian: 2レコード = 4バイト)
            var binResult = await controller.ExportBin("2026-08-21", null);
            var binFile = Assert.IsType<FileContentResult>(binResult);
            Assert.Equal(4, binFile.FileContents.Length);
            Assert.Equal(21021, BitConverter.ToUInt16(binFile.FileContents, 0));
            Assert.Equal(23213, BitConverter.ToUInt16(binFile.FileContents, 2));
        }

        [Fact]
        public async Task DashboardController_SessionClose_ArchivesAndAllowsRescan()
        {
            string masterDir = CreateStudentMasterDirectory();
            try
            {
                var env = new MockWebHostEnvironment { ContentRootPath = masterDir };
                var studentMaster = new StudentMasterService(Options.Create(_options), NullLogger<StudentMasterService>.Instance, env);
                Assert.Equal(2, studentMaster.TotalCount);

                var notifState = new NotificationStateService(new MockOptionsMonitor<TenkoServerOptions>(_options));
                var queue = new NotificationQueue();
                var scansController = new ScansController(_db, queue, notifState, new ScanAcceptanceService(), NullLogger<ScansController>.Instance);
                var dashboard = new DashboardController(_db, studentMaster, notifState, queue, new ScanAcceptanceService(), NullLogger<DashboardController>.Instance);

                string today = DateTime.Today.ToString("yyyy-MM-dd");

                static ScanBatchRequestDto BatchOf(string id, ushort last5, int hour) => new ScanBatchRequestDto
                {
                    ClientId = "client-test",
                    Records = new List<ScanItemDto>
                    {
                        new ScanItemDto
                        {
                            Id = id,
                            Barcode = last5.ToString(),
                            Last5 = last5,
                            Location = "2棟2階",
                            Timestamp = DateTime.Today.AddHours(hour)
                        }
                    }
                };

                // 1) 午前セッション: 点呼は受諾される
                var r1 = await scansController.PostScans(BatchOf("am-scan-1", 21021, 9));
                var resp1 = Assert.IsType<ScanBatchResponseDto>(Assert.IsType<OkObjectResult>(r1.Result).Value);
                Assert.Equal(1, resp1.AcceptedCount);

                // 同一セッション内の再スキャンは重複扱い
                var r2 = await scansController.PostScans(BatchOf("am-scan-1", 21021, 9));
                var resp2 = Assert.IsType<ScanBatchResponseDto>(Assert.IsType<OkObjectResult>(r2.Result).Value);
                Assert.Equal(0, resp2.AcceptedCount);
                Assert.Equal(1, resp2.DuplicateCount);

                // 締め前: 21021 は現在セッションで点呼済み → 未点呼リストに含まれない
                var uvBefore = await dashboard.GetUnverified(today);
                var uvListBefore = Assert.IsType<List<UnverifiedStudentDto>>(Assert.IsType<OkObjectResult>(uvBefore.Result).Value);
                Assert.DoesNotContain(uvListBefore, u => u.StudentNumber == 21021);

                // 通知キューを空にする
                await queue.DequeueNotificationAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);

                // 2) セッション締め → 全データがアーカイブへ退避される
                var closeResult = await dashboard.CloseSession(new CloseSessionRequestDto { Label = "午前" });
                var closeResp = Assert.IsType<CloseSessionResponseDto>(Assert.IsType<OkObjectResult>(closeResult.Result).Value);
                Assert.True(closeResp.Success);
                Assert.Equal(1, closeResp.MovedCount);
                Assert.False(string.IsNullOrEmpty(closeResp.SessionId));

                Assert.Equal(0, await _db.Scans.CountAsync());
                Assert.Equal(1, await _db.ArchivedScans.CountAsync());
                var archivedRow = await _db.ArchivedScans.SingleAsync();
                Assert.Equal("am-scan-1", archivedRow.Id);
                Assert.Equal("午前", archivedRow.SessionLabel);
                Assert.Equal(closeResp.SessionId, archivedRow.SessionId);
                Assert.Equal(today, archivedRow.ScanDate);

                // 3) 締め直後（再スキャン前）: 21021 は現在セッションで未点呼扱いに戻る
                var uvReopen = await dashboard.GetUnverified(today);
                var uvListReopen = Assert.IsType<List<UnverifiedStudentDto>>(Assert.IsType<OkObjectResult>(uvReopen.Result).Value);
                Assert.Contains(uvListReopen, u => u.StudentNumber == 21021);

                // 4) 午後セッション: 同一学生の再スキャンが受諾され、通知もキューに入る (BUG-02 解消)
                var r3 = await scansController.PostScans(BatchOf("pm-scan-1", 21021, 13));
                var resp3 = Assert.IsType<ScanBatchResponseDto>(Assert.IsType<OkObjectResult>(r3.Result).Value);
                Assert.Equal(1, resp3.AcceptedCount);
                Assert.Equal(0, resp3.DuplicateCount);
                Assert.Equal(1, resp3.NotificationQueuedCount);

                // 5) 午後スキャン後は 21021 は再び点呼済みになる
                var uvAfterPm = await dashboard.GetUnverified(today);
                var uvListAfterPm = Assert.IsType<List<UnverifiedStudentDto>>(Assert.IsType<OkObjectResult>(uvAfterPm.Result).Value);
                Assert.DoesNotContain(uvListAfterPm, u => u.StudentNumber == 21021);

                // 6) サマリーはアクティブセッション基準（締め後なので午後の1件のみ）
                var summaryResult = await dashboard.GetSummary(today);
                var summary = Assert.IsType<DashboardSummaryDto>(Assert.IsType<OkObjectResult>(summaryResult.Result).Value);
                Assert.Equal(1, summary.TotalScansToday);
                Assert.Equal(1, summary.UniqueStudentsToday);

                // 7) セッション一覧
                var sessionsResult = await dashboard.GetSessions();
                var sessions = Assert.IsType<List<SessionSummaryDto>>(Assert.IsType<OkObjectResult>(sessionsResult.Result).Value);
                Assert.Single(sessions);
                Assert.Equal("午前", sessions[0].Label);
                Assert.Equal(1, sessions[0].ScanCount);
                Assert.Equal(closeResp.SessionId, sessions[0].SessionId);

                // 8) エクスポート: session 指定時はアーカイブのみ
                var csvSession = await dashboard.ExportCsv(null, null, closeResp.SessionId);
                var csvFile = Assert.IsType<FileContentResult>(csvSession);
                string csvText = Encoding.UTF8.GetString(csvFile.FileContents);
                Assert.Contains("21021", csvText);
                Assert.Equal(2, csvText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length); // header + 1 row

                // 9) エクスポート: date 指定時はアクティブ + アーカイブを合算（午前+午後 = 2件）
                var binDate = await dashboard.ExportBin(today, null, null);
                var binFile = Assert.IsType<FileContentResult>(binDate);
                Assert.Equal(4, binFile.FileContents.Length);
                Assert.Equal(21021, BitConverter.ToUInt16(binFile.FileContents, 0));
                Assert.Equal(21021, BitConverter.ToUInt16(binFile.FileContents, 2));
            }
            finally
            {
                if (Directory.Exists(masterDir)) Directory.Delete(masterDir, true);
            }
        }

        /// <summary>
        /// テスト用の学生マスタ (students.txt) を一時ディレクトリに生成する。
        /// </summary>
        private static string CreateStudentMasterDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "TenkoMaster_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "data"));

            File.WriteAllLines(Path.Combine(dir, "data", "students.txt"), new[] { "21021", "23213" });
            return dir;
        }

        [Fact]
        public async Task ScansController_PostScans_Returns503_WhenAcceptanceDisabled()
        {
            var queue = new NotificationQueue();
            var notifState = new NotificationStateService(new MockOptionsMonitor<TenkoServerOptions>(_options));
            var acceptance = new ScanAcceptanceService { IsAcceptingScans = false };

            var controller = new ScansController(_db, queue, notifState, acceptance, NullLogger<ScansController>.Instance);

            var batch = new ScanBatchRequestDto
            {
                ClientId = "client-test",
                Records = new List<ScanItemDto>
                {
                    new ScanItemDto
                    {
                        Id = "rejected-1",
                        Barcode = "21021",
                        Last5 = 21021,
                        Location = "2棟2階",
                        Timestamp = DateTime.Now
                    }
                }
            };

            var actionResult = await controller.PostScans(batch);

            // 受付停止中は 503 で拒否され、クライアント側でデータが保持・再送される
            var statusCodeResult = Assert.IsType<ObjectResult>(actionResult.Result);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusCodeResult.StatusCode);
            var response = Assert.IsType<ScanBatchResponseDto>(statusCodeResult.Value);
            Assert.False(response.Success);
            Assert.Equal(0, response.AcceptedCount);

            // DB には何も保存されない
            Assert.Equal(0, await _db.Scans.CountAsync());

            // 受付を再開すると受理される
            acceptance.IsAcceptingScans = true;
            var okResult = Assert.IsType<OkObjectResult>((await controller.PostScans(batch)).Result);
            var okResponse = Assert.IsType<ScanBatchResponseDto>(okResult.Value);
            Assert.True(okResponse.Success);
            Assert.Equal(1, okResponse.AcceptedCount);
            Assert.Equal(1, await _db.Scans.CountAsync());
        }

        [Fact]
        public async Task DashboardController_DeleteHistory_RemovesActiveScansOnly()
        {
            var studentMaster = new StudentMasterService(
                Options.Create(_options), NullLogger<StudentMasterService>.Instance, new MockWebHostEnvironment());
            var notifState = new NotificationStateService(new MockOptionsMonitor<TenkoServerOptions>(_options));
            var queue = new NotificationQueue();
            var dashboard = new DashboardController(_db, studentMaster, notifState, queue, new ScanAcceptanceService(), NullLogger<DashboardController>.Instance);

            _db.Scans.AddRange(
                new ScanEntity
                {
                    Id = "del-1", Timestamp = new DateTime(2026, 8, 21, 9, 0, 0), Barcode = "21021", Last5 = 21021,
                    StudentName = "", StudentCode = "", Location = "2棟2階", ScanDate = "2026-08-21"
                },
                new ScanEntity
                {
                    Id = "del-2", Timestamp = new DateTime(2026, 8, 22, 9, 0, 0), Barcode = "23213", Last5 = 23213,
                    StudentName = "", StudentCode = "", Location = "2棟2階", ScanDate = "2026-08-22"
                });
            _db.ArchivedScans.Add(new ArchivedScanEntity
            {
                Id = "arch-1", Timestamp = new DateTime(2026, 8, 20, 9, 0, 0), Barcode = "11111", Last5 = 11111,
                StudentName = "", StudentCode = "", Location = "2棟2階", ReceivedAt = DateTime.UtcNow,
                ScanDate = "2026-08-20", SessionId = "session-x", ClosedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            // 条件未指定は BadRequest
            var badResult = await dashboard.DeleteHistory(new DeleteHistoryRequestDto());
            Assert.IsType<BadRequestObjectResult>(badResult.Result);

            // ID 指定削除
            var byIds = await dashboard.DeleteHistory(new DeleteHistoryRequestDto { ScanIds = new List<string> { "del-1" } });
            var byIdsResp = Assert.IsType<DeleteResponseDto>(Assert.IsType<OkObjectResult>(byIds.Result).Value);
            Assert.Equal(1, byIdsResp.DeletedCount);
            Assert.Null(await _db.Scans.FindAsync("del-1"));
            Assert.NotNull(await _db.Scans.FindAsync("del-2"));

            // 日付指定削除
            var byDate = await dashboard.DeleteHistory(new DeleteHistoryRequestDto { Date = "2026-08-22" });
            var byDateResp = Assert.IsType<DeleteResponseDto>(Assert.IsType<OkObjectResult>(byDate.Result).Value);
            Assert.Equal(1, byDateResp.DeletedCount);
            Assert.Equal(0, await _db.Scans.CountAsync());

            // アーカイブは影響を受けない
            Assert.Equal(1, await _db.ArchivedScans.CountAsync());
        }

        [Fact]
        public async Task DashboardController_SessionManagement_DeletesAndRestoresArchives()
        {
            var studentMaster = new StudentMasterService(
                Options.Create(_options), NullLogger<StudentMasterService>.Instance, new MockWebHostEnvironment());
            var notifState = new NotificationStateService(new MockOptionsMonitor<TenkoServerOptions>(_options));
            var queue = new NotificationQueue();
            var dashboard = new DashboardController(_db, studentMaster, notifState, queue, new ScanAcceptanceService(), NullLogger<DashboardController>.Instance);

            _db.ArchivedScans.Add(new ArchivedScanEntity
            {
                Id = "restore-1", Timestamp = new DateTime(2026, 8, 21, 9, 0, 0), Barcode = "21021", Last5 = 21021,
                StudentName = "", StudentCode = "", Location = "2棟2階", ReceivedAt = DateTime.UtcNow,
                ScanDate = "2026-08-21", SessionId = "session-r", SessionLabel = "午前", ClosedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            // 存在しないセッションは 404
            var missing = await dashboard.RestoreSession("no-such-session");
            Assert.IsType<NotFoundObjectResult>(missing.Result);

            // 復元: アーカイブ -> アクティブ
            var restore = await dashboard.RestoreSession("session-r");
            var restoreResp = Assert.IsType<RestoreSessionResponseDto>(Assert.IsType<OkObjectResult>(restore.Result).Value);
            Assert.True(restoreResp.Success);
            Assert.Equal(1, restoreResp.RestoredCount);

            Assert.Equal(0, await _db.ArchivedScans.CountAsync());
            var restored = await _db.Scans.SingleAsync(s => s.Id == "restore-1");
            Assert.Equal(21021, restored.Last5);
            Assert.Equal("2026-08-21", restored.ScanDate);

            // 再度締めてから削除
            var close = await dashboard.CloseSession(new CloseSessionRequestDto { Label = "午後" });
            var closeResp = Assert.IsType<CloseSessionResponseDto>(Assert.IsType<OkObjectResult>(close.Result).Value);
            Assert.Equal(1, await _db.ArchivedScans.CountAsync());

            var delete = await dashboard.DeleteSession(closeResp.SessionId!);
            var deleteResp = Assert.IsType<DeleteResponseDto>(Assert.IsType<OkObjectResult>(delete.Result).Value);
            Assert.Equal(1, deleteResp.DeletedCount);
            Assert.Equal(0, await _db.ArchivedScans.CountAsync());
            Assert.Equal(0, await _db.Scans.CountAsync());

            // 全削除 (空でも成功)
            var deleteAll = await dashboard.DeleteAllSessions();
            var deleteAllResp = Assert.IsType<DeleteResponseDto>(Assert.IsType<OkObjectResult>(deleteAll.Result).Value);
            Assert.True(deleteAllResp.Success);
        }

        [Fact]
        public void DashboardController_ScanAcceptance_TogglesState()
        {
            var studentMaster = new StudentMasterService(
                Options.Create(_options), NullLogger<StudentMasterService>.Instance, new MockWebHostEnvironment());
            var notifState = new NotificationStateService(new MockOptionsMonitor<TenkoServerOptions>(_options));
            var queue = new NotificationQueue();
            var acceptance = new ScanAcceptanceService();
            var dashboard = new DashboardController(_db, studentMaster, notifState, queue, acceptance, NullLogger<DashboardController>.Instance);

            // 既定値は許可
            var initial = Assert.IsType<ScanAcceptanceDto>(Assert.IsType<OkObjectResult>(dashboard.GetScanAcceptance().Result).Value);
            Assert.True(initial.IsAcceptingScans);

            // 停止へ切替
            var updated = Assert.IsType<ScanAcceptanceDto>(
                Assert.IsType<OkObjectResult>(
                    dashboard.UpdateScanAcceptance(new UpdateScanAcceptanceRequestDto { IsAcceptingScans = false }).Result).Value);
            Assert.False(updated.IsAcceptingScans);
            Assert.False(acceptance.IsAcceptingScans);

            // 再開
            dashboard.UpdateScanAcceptance(new UpdateScanAcceptanceRequestDto { IsAcceptingScans = true });
            Assert.True(acceptance.IsAcceptingScans);
        }

        [Fact]
        public async Task NotificationQueue_CanQueueAndDequeueAsync()
        {
            var queue = new NotificationQueue();
            var task = new NotificationTask
            {
                ScanId = "task-1",
                StudentNumber = 21021
            };

            await queue.QueueNotificationAsync(task);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var dequeued = await queue.DequeueNotificationAsync(cts.Token);

            Assert.Equal("task-1", dequeued.ScanId);
            Assert.Equal(21021, dequeued.StudentNumber);
        }

        [Fact]
        public async Task NotificationBackgroundService_BatchesAndSendsMinimalPayload()
        {
            var capturedBodies = new ConcurrentQueue<string>();
            var captureHandler = new CapturingWebhookHandler(capturedBodies);

            var services = new ServiceCollection();
            services.AddSingleton<TenkoDbContext>(_db);
            services.AddHttpClient("PowerAutomateClient")
                .ConfigurePrimaryHttpMessageHandler(() => captureHandler);
            using var serviceProvider = services.BuildServiceProvider();

            var options = new TenkoServerOptions
            {
                ApiKey = "test-api-key",
                AdminPassword = "test-admin-password",
                PowerAutomateWebhookUrl = "https://example.com/webhook",
                EnableNotifications = true,
                MaxNotificationsPerBatch = 20,
                NotificationBatchWindowSeconds = 1
            };

            var queue = new NotificationQueue();
            using var service = new NotificationBackgroundService(
                queue,
                serviceProvider,
                serviceProvider.GetRequiredService<IHttpClientFactory>(),
                new MockOptionsMonitor<TenkoServerOptions>(options),
                NullLogger<NotificationBackgroundService>.Instance);

            await service.StartAsync(CancellationToken.None);

            for (int i = 0; i < 25; i++)
            {
                await queue.QueueNotificationAsync(new NotificationTask
                {
                    ScanId = $"scan-{i}",
                    StudentNumber = (ushort)(20000 + i),
                    Location = "2棟2階",
                    ClientId = $"terminal-{i}",
                    Timestamp = new DateTime(2026, 8, 21, 10, 0, 0).AddMinutes(i)
                });
            }

            // 1バッチ目は最大20件で即送信、2バッチ目は窓期限（1秒）経過で送信される
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (capturedBodies.Count < 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            await service.StopAsync(CancellationToken.None);

            Assert.Equal(2, capturedBodies.Count);

            string firstBody = capturedBodies.ToArray()[0];
            using var doc = JsonDocument.Parse(firstBody);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.True(doc.RootElement.TryGetProperty("items", out var itemsProperty));
            Assert.Equal(JsonValueKind.Array, itemsProperty.ValueKind);

            var items = itemsProperty.EnumerateArray().ToList();
            Assert.Equal(20, items.Count);
            foreach (var item in items)
            {
                var propertyNames = item.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                Assert.Equal(new[] { "location", "studentNumber", "timestamp" }, propertyNames);
            }
            Assert.Equal("2棟2階", items[0].GetProperty("location").GetString());
            Assert.Equal(20000, items[0].GetProperty("studentNumber").GetInt32());
            Assert.Equal("2026-08-21 10:00:00", items[0].GetProperty("timestamp").GetString());

            // 個人情報（メールアドレス・氏名・出席番号・端末ID）が含まれていないこと
            string secondBody = capturedBodies.ToArray()[1];
            foreach (string body in new[] { firstBody, secondBody })
            {
                Assert.DoesNotContain("\"to\"", body);
                Assert.DoesNotContain("studentName", body);
                Assert.DoesNotContain("studentCode", body);
                Assert.DoesNotContain("clientId", body);
                Assert.DoesNotContain("@tokyo.kosen-ac.jp", body);
                Assert.DoesNotContain("氏名", body);
                Assert.DoesNotContain("terminal-", body);
                Assert.DoesNotContain("code-", body);
            }

            using var secondDoc = JsonDocument.Parse(secondBody);
            Assert.True(secondDoc.RootElement.TryGetProperty("items", out var secondItems));
            Assert.Equal(5, secondItems.EnumerateArray().Count());

            // 送信結果が DB ログに記録されていること
            Assert.Equal(25, _db.NotificationLogs.Count());
            Assert.All(_db.NotificationLogs.ToList(), log => Assert.True(log.IsSuccess));
        }

        [Fact]
        public async Task ServerSyncService_SuccessResponse_ClearsQueueAndUpdatesStatus()
        {
            var mockHandler = new MockHttpMessageHandler((req) =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Contains("/api/v1/scans", req.RequestUri?.ToString());
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"success\":true,\"acceptedCount\":1}")
                };
            });

            using var httpClient = new HttpClient(mockHandler);
            using var syncService = new Tenko.Native.Services.ServerSyncService(httpClient);

            var record = new Tenko.Native.Models.ScanRecord
            {
                Id = "test-sync-1",
                Barcode = "21021",
                Last5 = 21021,
                StudentName = "太郎 花子",
                Location = "2棟2階",
                Timestamp = DateTime.Now
            };

            syncService.EnqueueRecord(record);
            await syncService.SyncPendingAsync();

            Assert.Equal(0, syncService.PendingCount);
            if (Tenko.Native.Generated.EmbeddedServerConfig.IsEnabled)
            {
                Assert.Equal(Tenko.Native.Services.SyncStatus.Synced, syncService.CurrentStatus);
            }
        }

        [Fact]
        public async Task ServerSyncService_FailureResponse_RetainsQueue()
        {
            var mockHandler = new MockHttpMessageHandler((req) =>
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
            });

            using var httpClient = new HttpClient(mockHandler);
            using var syncService = new Tenko.Native.Services.ServerSyncService(httpClient);

            var record = new Tenko.Native.Models.ScanRecord
            {
                Id = "test-sync-failed",
                Barcode = "21021",
                Last5 = 21021,
                StudentName = "太郎 花子",
                Location = "2棟2階",
                Timestamp = DateTime.Now
            };

            syncService.EnqueueRecord(record);
            await syncService.SyncPendingAsync();

            if (Tenko.Native.Generated.EmbeddedServerConfig.IsEnabled)
            {
                Assert.Equal(1, syncService.PendingCount);
                Assert.Equal(Tenko.Native.Services.SyncStatus.Pending, syncService.CurrentStatus);
            }
        }

        [Fact]
        public async Task ServerSyncService_PersistsPendingQueueAcrossRestarts()
        {
            if (!Tenko.Native.Generated.EmbeddedServerConfig.IsEnabled)
            {
                return; // サーバー設定が埋め込まれていないビルドでは同期しない
            }

            string persistPath = Path.Combine(Path.GetTempPath(), "TenkoSync_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var record = new Tenko.Native.Models.ScanRecord
                {
                    Id = "persist-test-1",
                    Barcode = "21021",
                    Last5 = 21021,
                    StudentName = "太郎 花子",
                    Location = "2棟2階",
                    Timestamp = DateTime.Now
                };

                // 1) 送信失敗環境でエンキューすると、即座にファイルへ永続化される
                using (var failingService = new Tenko.Native.Services.ServerSyncService(
                    new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable))),
                    persistPath))
                {
                    failingService.EnqueueRecord(record);

                    Assert.True(File.Exists(persistPath));
                    Assert.Contains("persist-test-1", File.ReadAllText(persistPath));
                    Assert.Equal(1, failingService.PendingCount);
                }

                // 2) 新しいインスタンス（=アプリ再起動相当）でキューが復元される
                using (var restoredService = new Tenko.Native.Services.ServerSyncService(
                    new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"success\":true}")
                    })),
                    persistPath))
                {
                    Assert.Equal(1, restoredService.PendingCount);

                    await restoredService.SyncPendingAsync();

                    Assert.Equal(0, restoredService.PendingCount);
                    Assert.DoesNotContain("persist-test-1", File.ReadAllText(persistPath));
                }
            }
            finally
            {
                if (File.Exists(persistPath)) File.Delete(persistPath);
            }
        }

        [Fact]
        public async Task ServerSyncService_DeletionQueue_PersistsAcrossRestarts_AndFlushes()
        {
            if (!Tenko.Native.Generated.EmbeddedServerConfig.IsEnabled)
            {
                return; // サーバー設定が埋め込まれていないビルドでは同期しない
            }

            string dir = Path.Combine(Path.GetTempPath(), "TenkoDelTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string syncPath = Path.Combine(dir, "sync_queue.json");
            string deletePath = Path.Combine(dir, "sync_deletes.json");
            try
            {
                // 1) 削除要求を登録するが、削除APIは失敗させる -> sync_deletes.json に永続化される
                var failingHandler = new MockHttpMessageHandler(req =>
                    req.RequestUri?.AbsolutePath.EndsWith("/api/v1/scans/delete") == true
                        ? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
                        : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                        {
                            Content = new StringContent("{\"success\":true,\"acceptedCount\":1}")
                        });

                using (var service = new Tenko.Native.Services.ServerSyncService(new HttpClient(failingHandler), syncPath))
                {
                    service.EnqueueDeletion("del-test-1");

                    Assert.Equal(1, service.PendingDeletionCount);
                    Assert.True(File.Exists(deletePath));
                    Assert.Contains("del-test-1", File.ReadAllText(deletePath));
                }

                // 2) 再起動相当: 削除キューが復元され、成功ハンドラで送信されると消える
                string? capturedBody = null;
                var okHandler = new MockHttpMessageHandler(req =>
                {
                    if (req.RequestUri?.AbsolutePath.EndsWith("/api/v1/scans/delete") == true)
                    {
                        capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                    }
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"success\":true,\"deletedCount\":1}")
                    };
                });

                using (var restored = new Tenko.Native.Services.ServerSyncService(new HttpClient(okHandler), syncPath))
                {
                    Assert.Equal(1, restored.PendingDeletionCount);

                    await restored.FlushDeletionsAsync();

                    Assert.Equal(0, restored.PendingDeletionCount);
                    Assert.NotNull(capturedBody);
                    Assert.Contains("del-test-1", capturedBody!);
                    Assert.Contains("\"clientId\"", capturedBody!);
                    Assert.DoesNotContain("del-test-1", File.ReadAllText(deletePath));
                }
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ServerSyncService_RemovePendingRecord_SkipsUploadOfUnsentRecord()
        {
            if (!Tenko.Native.Generated.EmbeddedServerConfig.IsEnabled)
            {
                return; // サーバー設定が埋め込まれていないビルドでは同期しない
            }

            string dir = Path.Combine(Path.GetTempPath(), "TenkoDelTest2_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string syncPath = Path.Combine(dir, "sync_queue.json");
            try
            {
                var record = new Tenko.Native.Models.ScanRecord
                {
                    Id = "unsent-del-1",
                    Barcode = "21021",
                    Last5 = 21021,
                    Location = "2棟2階",
                    Timestamp = DateTime.Now
                };

                // 1) 送信失敗環境でエンキューした未送信レコードを、削除操作でキューから除外できる
                var failingHandler = new MockHttpMessageHandler(_ =>
                    new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));

                using (var service = new Tenko.Native.Services.ServerSyncService(new HttpClient(failingHandler), syncPath))
                {
                    service.EnqueueRecord(record);

                    Assert.Equal(1, service.PendingCount);
                    Assert.True(service.RemovePendingRecord(record.Id));
                    Assert.Equal(0, service.PendingCount);
                }

                // 2) 再起動後も除外済みレコードはアップロードされない
                var capturedBodies = new ConcurrentQueue<string>();
                var okHandler = new MockHttpMessageHandler(req =>
                {
                    capturedBodies.Enqueue(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"success\":true,\"acceptedCount\":0}")
                    };
                });

                using (var restored = new Tenko.Native.Services.ServerSyncService(new HttpClient(okHandler), syncPath))
                {
                    Assert.Equal(0, restored.PendingCount);

                    await restored.SyncPendingAsync();

                    Assert.Equal(0, capturedBodies.Count);
                }
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }

    internal class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    internal sealed class CapturingWebhookHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _bodies;

        public CapturingWebhookHandler(ConcurrentQueue<string> bodies)
        {
            _bodies = bodies;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            _bodies.Enqueue(body);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    internal class MockWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "TenkoServer";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = AppDomain.CurrentDomain.BaseDirectory;
        public string EnvironmentName { get; set; } = "Testing";
    }

    internal class MockOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public T CurrentValue { get; private set; }

        public MockOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
