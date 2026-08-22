using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Tenko.Native.Generated;
using Tenko.Native.Infrastructure;
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
        /// <summary>
        /// 未送信レコードの永続化先ファイル名 (data/ 配下)
        /// </summary>
        public const string PersistFileName = "sync_queue.json";

        /// <summary>
        /// サーバー削除待ちレコード Id の永続化先ファイル名 (data/ 配下)
        /// </summary>
        public const string DeletePersistFileName = "sync_deletes.json";

        private readonly HttpClient _httpClient;
        private readonly List<ScanRecord> _pendingRecords = new();
        private readonly List<string> _pendingDeletions = new();
        private readonly object _lock = new();
        private readonly object _persistLock = new();
        private readonly string? _persistPath;
        private readonly string? _deletePersistPath;
        private readonly DispatcherTimer? _retryTimer;
        private readonly SemaphoreSlim _syncSemaphore = new(1, 1);
        private readonly SemaphoreSlim _deleteSemaphore = new(1, 1);

        public event Action<SyncStatus, string>? OnStatusChanged;

        public SyncStatus CurrentStatus { get; private set; } = SyncStatus.Disabled;
        public string StatusMessage { get; private set; } = string.Empty;
        public int PendingCount
        {
            get { lock (_lock) return _pendingRecords.Count; }
        }

        public ServerSyncService(HttpClient? httpClient = null, string? persistFilePath = null)
        {
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            _persistPath = persistFilePath;

            if (!string.IsNullOrEmpty(_persistPath))
            {
                string? dir = Path.GetDirectoryName(_persistPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    _deletePersistPath = Path.Combine(dir, DeletePersistFileName);
                }
            }

            if (EmbeddedServerConfig.IsEnabled && !string.IsNullOrWhiteSpace(EmbeddedServerConfig.ServerUrl))
            {
                // 前回終了時に送信できていなかったレコードを復元する
                LoadPendingRecords();
                LoadPendingDeletions();
                int restored = PendingCount;
                if (restored > 0 || PendingDeletionCount > 0)
                {
                    UpdateStatus(SyncStatus.Pending, $"☁ 未送信 {restored}件");
                }
                else
                {
                    UpdateStatus(SyncStatus.Idle, "☁ 待機中");
                }

                // 定期リトライタイマー (30秒間隔・未送信/削除待ちが無い場合は通信なしで即リターン)
                _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _retryTimer.Tick += async (s, e) => { await SyncPendingAsync(); await FlushDeletionsAsync(); };
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

            bool added = false;
            lock (_lock)
            {
                if (!_pendingRecords.Any(r => r.Id == record.Id))
                {
                    _pendingRecords.Add(record);
                    added = true;
                }
            }

            if (added)
            {
                SavePendingRecords();
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

            await _syncSemaphore.WaitAsync();
            try
            {
                List<ScanRecord> batch;
                lock (_lock)
                {
                    if (_pendingRecords.Count == 0) return;
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
                        bool removed = false;
                        lock (_lock)
                        {
                            var sentIds = new HashSet<string>(batch.Select(b => b.Id));
                            removed = _pendingRecords.RemoveAll(r => sentIds.Contains(r.Id)) > 0;
                        }

                        if (removed)
                        {
                            SavePendingRecords();
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
            }
            finally
            {
                _syncSemaphore.Release();
            }
        }

        /// <summary>
        /// 指定 Id のレコードがまだ未送信キューに残っていれば除外する。
        /// サーバーへ未到達のレコードは削除要求を送る必要がないため、
        /// 呼び出し側は false が返った場合のみ EnqueueDeletion を呼び出すこと。
        /// 同期無効環境では常に true を返す (サーバー反映不要)。
        /// </summary>
        public bool RemovePendingRecord(string id)
        {
            if (!EmbeddedServerConfig.IsEnabled || string.IsNullOrWhiteSpace(EmbeddedServerConfig.ServerUrl))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(id)) return false;

            bool removed;
            lock (_lock)
            {
                removed = _pendingRecords.RemoveAll(r => r.Id == id) > 0;
            }

            if (removed)
            {
                SavePendingRecords();
                int remaining = PendingCount;
                if (remaining == 0 && PendingDeletionCount == 0)
                {
                    UpdateStatus(SyncStatus.Synced, "☁ 同期済");
                }
                else
                {
                    UpdateStatus(SyncStatus.Pending, $"☁ 未送信 {remaining}件");
                }
            }

            return removed;
        }

        /// <summary>
        /// 既にサーバーへ送信済みの可能性があるレコード Id を削除送信待ちキューへ追加し、即時に削除要求を試みる。
        /// オフライン時は sync_deletes.json に永続化され、オンライン復帰後に再送される。
        /// </summary>
        public void EnqueueDeletion(string id)
        {
            if (!EmbeddedServerConfig.IsEnabled || string.IsNullOrWhiteSpace(EmbeddedServerConfig.ServerUrl))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(id)) return;

            bool added = false;
            lock (_lock)
            {
                if (!_pendingDeletions.Contains(id))
                {
                    _pendingDeletions.Add(id);
                    added = true;
                }
            }

            if (added)
            {
                SavePendingDeletions();
            }

            _ = Task.Run(FlushDeletionsAsync);
        }

        public int PendingDeletionCount
        {
            get { lock (_lock) return _pendingDeletions.Count; }
        }

        /// <summary>
        /// 削除待ち Id を POST /api/v1/scans/delete へ一括送信する。
        /// 成功した Id だけをキューから除去するため、失敗時は自動リトライ対象として保持される。
        /// </summary>
        public async Task FlushDeletionsAsync()
        {
            if (!EmbeddedServerConfig.IsEnabled || string.IsNullOrWhiteSpace(EmbeddedServerConfig.ServerUrl))
            {
                return;
            }

            await _deleteSemaphore.WaitAsync();
            try
            {
                List<string> batch;
                lock (_lock)
                {
                    if (_pendingDeletions.Count == 0) return;
                    batch = _pendingDeletions.ToList();
                }

                try
                {
                    string endpoint = EmbeddedServerConfig.ServerUrl.TrimEnd('/') + "/api/v1/scans/delete";

                    var payload = new
                    {
                        clientId = EmbeddedServerConfig.ClientId,
                        ids = batch
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
                        bool removed = false;
                        lock (_lock)
                        {
                            var sentIds = new HashSet<string>(batch);
                            removed = _pendingDeletions.RemoveAll(i => sentIds.Contains(i)) > 0;
                        }

                        if (removed)
                        {
                            SavePendingDeletions();
                        }
                    }
                }
                catch (Exception ex)
                {
                    // オフライン等の場合はキューに保持し、次回のリトライで再送する
                    Debug.WriteLine($"[ServerSyncService] Deletion flush failed: {ex.Message}");
                }

                int pendingRecords = PendingCount;
                if (pendingRecords == 0 && PendingDeletionCount == 0)
                {
                    UpdateStatus(SyncStatus.Synced, "☁ 同期済");
                }
                else if (PendingDeletionCount > 0)
                {
                    UpdateStatus(SyncStatus.Pending, $"☁ 未送信 {pendingRecords}件");
                }
            }
            finally
            {
                _deleteSemaphore.Release();
            }
        }

        private void UpdateStatus(SyncStatus status, string message)
        {
            CurrentStatus = status;
            StatusMessage = message;
            OnStatusChanged?.Invoke(status, message);
        }

        /// <summary>
        /// data/sync_queue.json から未送信レコードを復元する。
        /// サーバー側は Id 重複排除を持つため、再送による二重記録は発生しない。
        /// </summary>
        private void LoadPendingRecords()
        {
            if (string.IsNullOrEmpty(_persistPath) || !File.Exists(_persistPath))
            {
                return;
            }

            try
            {
                var loaded = JsonHelper.Deserialize<List<ScanRecord>>(File.ReadAllText(_persistPath));
                if (loaded == null || loaded.Count == 0)
                {
                    return;
                }

                lock (_lock)
                {
                    foreach (var record in loaded)
                    {
                        if (!_pendingRecords.Any(r => r.Id == record.Id))
                        {
                            _pendingRecords.Add(record);
                        }
                    }
                }

                Debug.WriteLine($"[ServerSyncService] Restored {loaded.Count} pending record(s) from {_persistPath}.");
            }
            catch (Exception ex)
            {
                // 破損していた場合はキューを諦めて空の状態で起動する（ローカル bin/history にはデータが残る）
                Debug.WriteLine($"[ServerSyncService] Failed to load pending queue: {ex.Message}");
            }
        }

        /// <summary>
        /// 未送信レコードを data/sync_queue.json へ保存する（一時ファイル経由の原子的書き込み）。
        /// </summary>
        private void SavePendingRecords()
        {
            if (string.IsNullOrEmpty(_persistPath))
            {
                return;
            }

            try
            {
                List<ScanRecord> snapshot;
                lock (_lock)
                {
                    snapshot = _pendingRecords.ToList();
                }

                lock (_persistLock)
                {
                    string? dir = Path.GetDirectoryName(_persistPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    string tempPath = _persistPath + ".tmp";
                    File.WriteAllText(tempPath, JsonHelper.Serialize(snapshot));
                    File.Move(tempPath, _persistPath, true);
                }
            }
            catch (Exception ex)
            {
                // 永続化に失敗してもメモリ上のキューと同期動作は継続する
                Debug.WriteLine($"[ServerSyncService] Failed to persist pending queue: {ex.Message}");
            }
        }

        /// <summary>
        /// 削除待ち Id を data/sync_deletes.json へ保存する（一時ファイル経由の原子的書き込み）。
        /// </summary>
        private void SavePendingDeletions()
        {
            if (string.IsNullOrEmpty(_deletePersistPath))
            {
                return;
            }

            try
            {
                List<string> snapshot;
                lock (_lock)
                {
                    snapshot = _pendingDeletions.ToList();
                }

                lock (_persistLock)
                {
                    string? dir = Path.GetDirectoryName(_deletePersistPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    string tempPath = _deletePersistPath + ".tmp";
                    File.WriteAllText(tempPath, JsonHelper.Serialize(snapshot));
                    File.Move(tempPath, _deletePersistPath, true);
                }
            }
            catch (Exception ex)
            {
                // 永続化に失敗してもメモリ上のキューと同期動作は継続する
                Debug.WriteLine($"[ServerSyncService] Failed to persist deletion queue: {ex.Message}");
            }
        }

        private void LoadPendingDeletions()
        {
            if (string.IsNullOrEmpty(_deletePersistPath) || !File.Exists(_deletePersistPath))
            {
                return;
            }

            try
            {
                var loaded = JsonHelper.Deserialize<List<string>>(File.ReadAllText(_deletePersistPath));
                if (loaded == null || loaded.Count == 0)
                {
                    return;
                }

                lock (_lock)
                {
                    foreach (var id in loaded)
                    {
                        if (!_pendingDeletions.Contains(id))
                        {
                            _pendingDeletions.Add(id);
                        }
                    }
                }

                Debug.WriteLine($"[ServerSyncService] Restored {loaded.Count} pending deletion(s) from {_deletePersistPath}.");
            }
            catch (Exception ex)
            {
                // 破損していた場合は削除キューを諦めて空の状態で起動する
                Debug.WriteLine($"[ServerSyncService] Failed to load pending deletions: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _retryTimer?.Stop();
            _httpClient?.Dispose();
            _syncSemaphore.Dispose();
            _deleteSemaphore.Dispose();
        }
    }
}
