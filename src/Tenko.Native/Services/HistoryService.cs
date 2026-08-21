using System.Collections.Generic;
using Tenko.Native.Models;

namespace Tenko.Native.Services
{
    public class HistoryService
    {
        private readonly StorageService _storage;
        private readonly string _path;

        public HistoryService(StorageService storage)
        {
            _storage = storage;
            _path = _storage.GetDataPath("history.json");
        }

        public List<ScanRecord> LoadHistory() => _storage.LoadJson<List<ScanRecord>>(_path) ?? new();
        public void SaveHistory(List<ScanRecord> history) => _storage.SaveJson(_path, history);
    }
}
