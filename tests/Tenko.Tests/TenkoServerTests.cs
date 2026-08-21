using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
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
            var optionsWrapper = Options.Create(_options);
            var mockEnv = new MockWebHostEnvironment();
            var studentMaster = new StudentMasterService(optionsWrapper, NullLogger<StudentMasterService>.Instance, mockEnv);
            var queue = new NotificationQueue();
            var notifState = new NotificationStateService(new MockOptionsMonitor<TenkoServerOptions>(_options));

            var controller = new ScansController(_db, queue, notifState, studentMaster, NullLogger<ScansController>.Instance);

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
                        StudentName = null, // 個人情報なし
                        StudentCode = null, // 個人情報なし
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

            // DB に保存されたか確認
            Assert.Equal(2, await _db.Scans.CountAsync());

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
        public async Task DashboardController_ComputesSummaryAndExports()
        {
            var optionsWrapper = Options.Create(_options);
            var mockEnv = new MockWebHostEnvironment();
            var studentMaster = new StudentMasterService(optionsWrapper, NullLogger<StudentMasterService>.Instance, mockEnv);

            // テストデータを DB へ挿入
            _db.Scans.AddRange(
                new TenkoServer.Data.Models.ScanEntity
                {
                    Id = "scan-101",
                    Timestamp = new DateTime(2026, 8, 21, 9, 0, 0),
                    Barcode = "21021",
                    Last5 = 21021,
                    StudentName = "太郎 花子",
                    StudentCode = "4D23",
                    Location = "2棟2階",
                    ScanDate = "2026-08-21"
                },
                new TenkoServer.Data.Models.ScanEntity
                {
                    Id = "scan-102",
                    Timestamp = new DateTime(2026, 8, 21, 9, 30, 0),
                    Barcode = "23213",
                    Last5 = 23213,
                    StudentName = "次郎 美咲",
                    StudentCode = "2M15",
                    Location = "本部横",
                    ScanDate = "2026-08-21"
                }
            );
            await _db.SaveChangesAsync();

            var notifState = new NotificationStateService(new MockOptionsMonitor<TenkoServerOptions>(_options));
            var queue = new NotificationQueue();
            var controller = new DashboardController(_db, studentMaster, notifState, queue);

            // サマリー取得
            var summaryResult = await controller.GetSummary("2026-08-21");
            var okSummary = Assert.IsType<OkObjectResult>(summaryResult.Result);
            var summary = Assert.IsType<DashboardSummaryDto>(okSummary.Value);

            Assert.Equal(2, summary.TotalScansToday);
            Assert.Equal(2, summary.UniqueStudentsToday);
            Assert.Equal(2, summary.ScansByLocation.Count);
            Assert.True(summary.ScansByLocation.ContainsKey("2棟2階"));
            Assert.True(summary.ScansByLocation.ContainsKey("本部横"));

            // スキャン一覧取得
            var scansResult = await controller.GetScans("2026-08-21", null, "花子");
            var okScans = Assert.IsType<OkObjectResult>(scansResult.Result);
            var scansList = Assert.IsType<List<ScanItemDto>>(okScans.Value);
            Assert.Single(scansList);
            Assert.Equal("太郎 花子", scansList[0].StudentName);
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
            Assert.Contains("太郎 花子", csvContent);
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
                Assert.Equal(2, studentMaster.GetAllStudents().Count);

                var notifState = new NotificationStateService(new MockOptionsMonitor<TenkoServerOptions>(_options));
                var queue = new NotificationQueue();
                var scansController = new ScansController(_db, queue, notifState, studentMaster, NullLogger<ScansController>.Instance);
                var dashboard = new DashboardController(_db, studentMaster, notifState, queue);

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
                Assert.Contains("太郎 花子", csvText);
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
        /// テスト用の学生マスタ (students.enc + students.passphrase) を一時ディレクトリに生成する。
        /// v2 形式 (AES-CBC + HMAC-SHA256, PBKDF2 200k) をツールと同じ手順で暗号化する。
        /// </summary>
        private static string CreateStudentMasterDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "TenkoMaster_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "data"));

            const string passphrase = "test-passphrase";
            const int keySize = 32;
            string csv = "student_number,name,code\n21021,太郎 花子,4D23\n23213,次郎 美咲,2M15\n";

            byte[] salt = new byte[16];
            byte[] iv = new byte[16];

            using var kdf = new Rfc2898DeriveBytes(passphrase, salt, 200_000, HashAlgorithmName.SHA256);
            byte[] encKey = kdf.GetBytes(keySize);
            byte[] macKey = kdf.GetBytes(keySize);

            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = encKey;
            aes.IV = iv;
            using var encryptor = aes.CreateEncryptor();
            byte[] plain = Encoding.UTF8.GetBytes(csv);
            byte[] cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);

            byte[] magic = Encoding.ASCII.GetBytes("TNKS");
            byte[] payload = new byte[magic.Length + 1 + salt.Length + iv.Length + cipher.Length];
            int offset = 0;
            Array.Copy(magic, 0, payload, offset, magic.Length);
            offset += magic.Length;
            payload[offset++] = 2; // FileVersionAesCbcHmac
            Array.Copy(salt, 0, payload, offset, salt.Length);
            offset += salt.Length;
            Array.Copy(iv, 0, payload, offset, iv.Length);
            offset += iv.Length;
            Array.Copy(cipher, 0, payload, offset, cipher.Length);

            using var hmac = new HMACSHA256(macKey);
            byte[] mac = hmac.ComputeHash(payload);

            byte[] output = new byte[payload.Length + mac.Length];
            Array.Copy(payload, 0, output, 0, payload.Length);
            Array.Copy(mac, 0, output, payload.Length, mac.Length);

            File.WriteAllBytes(Path.Combine(dir, "data", "students.enc"), output);
            File.WriteAllText(Path.Combine(dir, "data", "students.passphrase"), passphrase);
            return dir;
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
                    StudentName = $"氏名{i}",
                    StudentCode = $"code-{i}",
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
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);

            var items = doc.RootElement.EnumerateArray().ToList();
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
            Assert.Equal(5, secondDoc.RootElement.EnumerateArray().Count());

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
