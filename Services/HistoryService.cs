using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Tenko.Native.Models;

namespace Tenko.Native.Services
{
    public class HistoryService
    {
        private readonly string _historyPath;

        public HistoryService()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _historyPath = Path.Combine(baseDir, "data", "history.json");
        }

        // history.json を読み込み、復元可能な履歴一覧を返す。
        public List<ScanRecord> LoadHistory()
        {
            if (File.Exists(_historyPath))
            {
                try
                {
                    string json = File.ReadAllText(_historyPath);
                    return JsonSerializer.Deserialize<List<ScanRecord>>(json) ?? new List<ScanRecord>();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HistoryService] LoadHistory failed: {ex.Message}");
                    return new List<ScanRecord>();
                }
            }
            return new List<ScanRecord>();
        }

        // 履歴一覧を history.json へ上書き保存する。
        public void SaveHistory(List<ScanRecord> history)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                string json = JsonSerializer.Serialize(history, options);
                File.WriteAllText(_historyPath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HistoryService] SaveHistory failed: {ex.Message}");
            }
        }
    }
}
