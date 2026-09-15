using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tenko.Lite.Models;

namespace Tenko.Lite.Services
{
    public class ScanProcessor : IScanProcessor
    {
        private const int ScanDebounceSeconds = 3;

        private readonly HistoryService _historyService;
        private readonly ScanFileService _scanFileService;
        private readonly ServerSyncService? _serverSyncService;
        private ushort _lastScannedNumber;
        private DateTime _lastScannedAt = DateTime.MinValue;

        public ScanProcessor(
            HistoryService historyService,
            ScanFileService scanFileService,
            ServerSyncService? serverSyncService = null)
        {
            _historyService = historyService;
            _scanFileService = scanFileService;
            _serverSyncService = serverSyncService;
        }

        public List<ScanRecord> LoadHistory()
        {
            return _historyService.LoadHistory();
        }

        public ScanResult ProcessScan(string barcode, string location, List<ScanRecord> allHistory)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                return ScanResult.LocationMissing();
            }

            if (string.IsNullOrWhiteSpace(barcode))
            {
                return ScanResult.Validation("バーコードを読み取るか入力してください。");
            }

            if (!barcode.All(char.IsDigit))
            {
                return ScanResult.Validation("数字のみ入力可能です。");
            }

            if (barcode.Length != 5 && barcode.Length != 10)
            {
                return ScanResult.Validation("5桁または10桁の数字を入力してください。");
            }

            // 5桁/10桁のいずれでも下5桁を切り出して解析する
            if (!ushort.TryParse(barcode.AsSpan(barcode.Length - 5), out ushort last5))
            {
                return ScanResult.Validation("オーバーフローです");
            }

            // 同一学籍番号の短時間連続読み取り (スキャナの誤読ノイズ・チャタリング) は即時無視
            DateTime now = DateTime.Now;
            if (_lastScannedNumber == last5 && (now - _lastScannedAt).TotalSeconds < ScanDebounceSeconds)
            {
                return ScanResult.Debounced();
            }

            // ロケーション内の重複チェック
            if (allHistory.Any(h => h.Location == location && h.Last5 == last5))
            {
                return ScanResult.Duplicate();
            }

            _lastScannedNumber = last5;
            _lastScannedAt = now;

            var record = new ScanRecord
            {
                Id = $"{DateTimeOffset.Now.ToUnixTimeMilliseconds()}_{last5:D5}_{Guid.NewGuid().ToString("N").Substring(0, 8)}",
                Timestamp = now,
                Barcode = barcode,
                Last5 = last5,
                Location = location
            };

            allHistory.Insert(0, record);

            // 片方だけ書き込まれた状態を残さないよう、失敗時は全ロールバックして呼び出し側へ通知する
            bool binAppended = false;
            try
            {
                _scanFileService.AppendLast5(location, last5);
                binAppended = true;
                _historyService.SaveHistory(allHistory);
            }
            catch
            {
                allHistory.RemoveAll(h => h.Id == record.Id);

                // SaveHistory は成功時のみ置換されるため、失敗時に戻す必要があるのは追記済みの BIN だけ
                if (binAppended)
                {
                    try { _scanFileService.RemoveLast5(location, last5); } catch { /* ロールバック失敗は無視 */ }
                }

                throw;
            }

            _serverSyncService?.EnqueueRecord(record);

            return ScanResult.Ok(record);
        }

        public bool DeleteRecord(ScanRecord record, List<ScanRecord> allHistory, out bool binMismatch)
        {
            binMismatch = false;
            if (record == null) return false;

            // 削除前に履歴と BIN の件数を突き合わせ、対応関係が崩れていないか確認する
            int historyCount = allHistory.Count(h => h.Location == record.Location && h.Last5 == record.Last5);
            int binCount = _scanFileService.CountLast5(record.Location, record.Last5);

            if (allHistory.RemoveAll(h => h.Id == record.Id) == 0)
            {
                return false;
            }

            _historyService.SaveHistory(allHistory);

            if (!_scanFileService.RemoveLast5(record.Location, record.Last5) || binCount != historyCount)
            {
                binMismatch = true;
            }

            PropagateServerDeletions(new[] { record.Id });
            return true;
        }

        public void DeleteAllForLocation(string location, List<ScanRecord> allHistory)
        {
            if (string.IsNullOrEmpty(location)) return;

            var removedIds = allHistory
                .Where(h => h.Location == location)
                .Select(h => h.Id)
                .ToList();

            // 件数分ループせず、まとめて 1 回で伝播する
            PropagateServerDeletions(removedIds);

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

        /// <summary>
        /// 削除 ID 群をまとめてサーバーへ伝播する。送信キューから除去できなかった ID のみ削除キューへ積む
        /// </summary>
        private void PropagateServerDeletions(IReadOnlyCollection<string> recordIds)
        {
            if (_serverSyncService == null || recordIds.Count == 0) return;

            var removed = _serverSyncService.RemovePendingRecords(recordIds);
            var remaining = recordIds.Where(id => !removed.Contains(id)).ToList();
            if (remaining.Count > 0)
            {
                _serverSyncService.EnqueueDeletions(remaining);
            }
        }
    }
}
