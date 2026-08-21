using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
                EmailDomain = "tokyo.kosen-ac.jp",
                EnableNotifications = true
            };
        }

        public void Dispose()
        {
            _db.Database.EnsureDeleted();
            _db.Dispose();
        }

        [Fact]
        public void FormatStudentEmail_GeneratesExpectedFormat()
        {
            var optionsWrapper = Options.Create(_options);
            var mockEnv = new MockWebHostEnvironment();
            var service = new StudentMasterService(optionsWrapper, NullLogger<StudentMasterService>.Instance, mockEnv);

            string email = service.FormatStudentEmail(21021);
            Assert.Equal("s21021@tokyo.kosen-ac.jp", email);

            string emailZeroPadded = service.FormatStudentEmail(123);
            Assert.Equal("s00123@tokyo.kosen-ac.jp", emailZeroPadded);
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
            Assert.Equal("s21021@tokyo.kosen-ac.jp", task1.ToEmail);
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
        public async Task NotificationQueue_CanQueueAndDequeueAsync()
        {
            var queue = new NotificationQueue();
            var task = new NotificationTask
            {
                ScanId = "task-1",
                StudentNumber = 21021,
                ToEmail = "s21021@tokyo.kosen-ac.jp"
            };

            await queue.QueueNotificationAsync(task);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var dequeued = await queue.DequeueNotificationAsync(cts.Token);

            Assert.Equal("task-1", dequeued.ScanId);
            Assert.Equal("s21021@tokyo.kosen-ac.jp", dequeued.ToEmail);
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
