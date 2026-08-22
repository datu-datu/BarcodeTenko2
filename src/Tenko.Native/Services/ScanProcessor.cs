using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tenko.Native.Models;

namespace Tenko.Native.Services
{
    public class ScanProcessor : IScanProcessor
    {
        private const int ScanDebounceSeconds = 3;

        private readonly HistoryService _historyService;
        private readonly ScanFileService _scanFileService;
        private readonly StudentService _studentService;
        private readonly ServerSyncService? _serverSyncService;
        private readonly Dictionary<ushort, DateTime> _recentScanAt = new();

        public ScanProcessor(
            HistoryService historyService,
            ScanFileService scanFileService,
            StudentService studentService,
            ServerSyncService? serverSyncService = null)
        {
            _historyService = historyService;
            _scanFileService = scanFileService;
            _studentService = studentService;
            _serverSyncService = serverSyncService;
        }

        public List<ScanRecord> LoadHistory()
        {
            var records = _historyService.LoadHistory();
            foreach (var record in records)
            {
                var (name, code) = _studentService.GetStudentInfo(record.Last5);
                record.StudentName = name;
                record.StudentCode = code;
            }
            return records;
        }

        public ScanResult ProcessScan(string barcode, string location, List<ScanRecord> allHistory)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                return ScanResult.LocationMissing();
            }

            if (string.IsNullOrWhiteSpace(barcode))
            {
                return ScanResult.Validation("バーコードを入力してください。");
            }

            if (!barcode.All(char.IsDigit))
            {
                return ScanResult.Validation("数字のみ入力可能です。");
            }

            if (barcode.Length != 5 && barcode.Length != 10)
            {
                return ScanResult.Validation("5桁または10桁の数字を入力してください。");
            }

            string last5Str = barcode.Length >= 5 ? barcode.Substring(barcode.Length - 5) : barcode;
            if (!ushort.TryParse(last5Str, out ushort last5))
            {
                return ScanResult.Validation("番号の解析に失敗しました。");
            }

            // 同一学籍番号の短時間連続読み取り (スキャナの誤読ノイズ) は即時無視
            DateTime now = DateTime.Now;
            if (_recentScanAt.TryGetValue(last5, out DateTime lastAt) &&
                (now - lastAt).TotalSeconds < ScanDebounceSeconds)
            {
                return ScanResult.Debounced();
            }

            // ロケーション内の重複チェック
            if (allHistory.Any(h => h.Location == location && h.Last5 == last5))
            {
                return ScanResult.Duplicate();
            }

            // 古いデバウンスキーのクリーンアップ
            if (_recentScanAt.Count > 512)
            {
                foreach (var key in _recentScanAt.Where(kv => (now - kv.Value).TotalMinutes > 10).Select(kv => kv.Key).ToList())
                {
                    _recentScanAt.Remove(key);
                }
            }
            _recentScanAt[last5] = now;

            var (name, code) = _studentService.GetStudentInfo(last5);
            var record = new ScanRecord
            {
                Id = $"{DateTimeOffset.Now.ToUnixTimeMilliseconds()}_{last5:D5}_{Guid.NewGuid().ToString("N").Substring(0, 8)}",
                Timestamp = now,
                Barcode = barcode,
                Last5 = last5,
                StudentName = name,
                StudentCode = code,
                Location = location
            };

            allHistory.Insert(0, record);
            _historyService.SaveHistory(allHistory);
            _scanFileService.AppendLast5(location, last5);
            _serverSyncService?.EnqueueRecord(record);

            return ScanResult.Ok(record);
        }

        public void DeleteRecord(ScanRecord record, List<ScanRecord> allHistory)
        {
            if (record == null) return;

            allHistory.Remove(record);
            _historyService.SaveHistory(allHistory);
            _scanFileService.RemoveLast5(record.Location, record.Last5);
            PropagateServerDeletion(record.Id);
        }

        public void DeleteAllForLocation(string location, List<ScanRecord> allHistory)
        {
            if (string.IsNullOrEmpty(location)) return;

            var removedIds = allHistory
                .Where(h => h.Location == location)
                .Select(h => h.Id)
                .ToList();

            foreach (var id in removedIds)
            {
                PropagateServerDeletion(id);
            }

            allHistory.RemoveAll(h => h.Location == location);
            _historyService.SaveHistory(allHistory);
            _scanFileService.DeleteBin(location);
        }

        public string RenameLocationBin(string location, string newName, List<ScanRecord> allHistory)
        {
            if (string.IsNullOrEmpty(location) || string.IsNullOrEmpty(newName))
            {
                throw new ArgumentException("ロケーションおよびファイル名を入力してください。");
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            string sanitized = new string(newName.Select(c => invalidChars.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());

            _scanFileService.RenameBin(location, sanitized);
            allHistory.RemoveAll(h => h.Location == location);
            _historyService.SaveHistory(allHistory);

            return sanitized;
        }

        public bool CheckBinExists(string location)
        {
            if (string.IsNullOrEmpty(location)) return false;
            return _scanFileService.Exists(location);
        }

        private void PropagateServerDeletion(string recordId)
        {
            if (_serverSyncService == null || string.IsNullOrWhiteSpace(recordId)) return;
            if (!_serverSyncService.RemovePendingRecord(recordId))
            {
                _serverSyncService.EnqueueDeletion(recordId);
            }
        }
    }
}
