using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Tenko.Native.Generated;
using Tenko.Native.Models;

namespace Tenko.Native.Services
{
    public enum SyncStatus
    {
        Disabled,
        Idle,
        Syncing,
        Synced,
        Pending
    }

    public class ServerSyncService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly List<ScanRecord> _pendingRecords = new();
        private readonly object _lock = new();
        private readonly DispatcherTimer? _retryTimer;
        private bool _isSyncing = false;

        public event Action<SyncStatus, string>? OnStatusChanged;

        public SyncStatus CurrentStatus { get; private set; } = SyncStatus.Disabled;
        public string StatusMessage { get; private set; } = string.Empty;
        public int PendingCount
        {
            get { lock (_lock) return _pendingRecords.Count; }
        }

        public ServerSyncService(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            if (EmbeddedServerConfig.IsEnabled && !string.IsNullOrWhiteSpace(EmbeddedServerConfig.ServerUrl))
            {
                UpdateStatus(SyncStatus.Idle, "☁ 待機中");

                // 定期リトライタイマー (30秒間隔)
                _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _retryTimer.Tick += async (s, e) => await SyncPendingAsync();
                _retryTimer.Start();
            }
            else
            {
                UpdateStatus(SyncStatus.Disabled, "");
            }
        }

        /// <summary>
        /// 新しいスキャンレコードを送信待ちに追加し、即時非同期送信をトリガーする。
        /// </summary>
        public void EnqueueRecord(ScanRecord record)
        {
            if (!EmbeddedServerConfig.IsEnabled || string.IsNullOrWhiteSpace(EmbeddedServerConfig.ServerUrl))
            {
                return;
            }

            lock (_lock)
            {
                if (!_pendingRecords.Any(r => r.Id == record.Id))
                {
                    _pendingRecords.Add(record);
                }
            }

            // 非同期で即時送信
            _ = Task.Run(SyncPendingAsync);
        }

        /// <summary>
        /// 溜まっている未送信レコードを一括送信する。
        /// </summary>
        public async Task SyncPendingAsync()
        {
            if (!EmbeddedServerConfig.IsEnabled || string.IsNullOrWhiteSpace(EmbeddedServerConfig.ServerUrl))
            {
                return;
            }

            List<ScanRecord> batch;
            lock (_lock)
            {
                if (_isSyncing || _pendingRecords.Count == 0) return;
                _isSyncing = true;
                batch = _pendingRecords.ToList();
            }

            UpdateStatus(SyncStatus.Syncing, "☁ 送信中...");

            try
            {
                string endpoint = EmbeddedServerConfig.ServerUrl.TrimEnd('/') + "/api/v1/scans";

                var payload = new
                {
                    clientId = EmbeddedServerConfig.ClientId,
                    records = batch.Select(r => new
                    {
                        id = r.Id,
                        timestamp = r.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss"),
                        barcode = r.Barcode,
                        last5 = r.Last5,
                        location = r.Location
                    }).ToList()
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                if (!string.IsNullOrWhiteSpace(EmbeddedServerConfig.ApiKey))
                {
                    request.Headers.Add("X-API-Key", EmbeddedServerConfig.ApiKey);
                }
                request.Content = JsonContent.Create(payload);

                var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    lock (_lock)
                    {
                        var sentIds = new HashSet<string>(batch.Select(b => b.Id));
                        _pendingRecords.RemoveAll(r => sentIds.Contains(r.Id));
                    }

                    int remaining = PendingCount;
                    if (remaining == 0)
                    {
                        UpdateStatus(SyncStatus.Synced, "☁ 同期済");
                    }
                    else
                    {
                        UpdateStatus(SyncStatus.Pending, $"☁ 未送信 {remaining}件");
                    }
                }
                else
                {
                    int remaining = PendingCount;
                    UpdateStatus(SyncStatus.Pending, $"☁ 未送信 {remaining}件 (HTTP {(int)response.StatusCode})");
                    Debug.WriteLine($"[ServerSyncService] Sync failed: HTTP {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                int remaining = PendingCount;
                UpdateStatus(SyncStatus.Pending, $"☁ 未送信 {remaining}件 (オフライン)");
                Debug.WriteLine($"[ServerSyncService] Network exception: {ex.Message}");
            }
            finally
            {
                lock (_lock)
                {
                    _isSyncing = false;
                }
            }
        }

        private void UpdateStatus(SyncStatus status, string message)
        {
            CurrentStatus = status;
            StatusMessage = message;
            OnStatusChanged?.Invoke(status, message);
        }

        public void Dispose()
        {
            _retryTimer?.Stop();
            _httpClient?.Dispose();
        }
    }
}
