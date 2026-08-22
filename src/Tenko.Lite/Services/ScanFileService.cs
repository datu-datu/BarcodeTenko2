using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Tenko.Lite.Services
{
    public class ScanFileService
    {
        private readonly StorageService _storage;

        public ScanFileService(StorageService storage) => _storage = storage;

        private string GetFilePath(string location) => _storage.GetScanPath($"ids_{location}.bin");

        public bool Exists(string location)
        {
            if (string.IsNullOrEmpty(location)) return false;
            string path = GetFilePath(location);
            return _storage.Exists(path) && new FileInfo(path).Length > 0;
        }

        public void AppendLast5(string location, ushort last5)
        {
            if (string.IsNullOrEmpty(location)) return;
            _storage.AppendAllBytes(GetFilePath(location), BitConverter.GetBytes(last5));
        }

        public void RemoveLast5(string location, ushort last5)
        {
            if (string.IsNullOrEmpty(location)) return;
            string path = GetFilePath(location);
            byte[] data = _storage.ReadAllBytes(path);
            if (data.Length == 0 || data.Length % 2 != 0) return;

            var values = new List<ushort>();
            for (int i = 0; i < data.Length; i += 2) values.Add(BitConverter.ToUInt16(data, i));

            int index = values.LastIndexOf(last5);
            if (index >= 0)
            {
                values.RemoveAt(index);
                _storage.WriteAllBytes(path, values.SelectMany(BitConverter.GetBytes).ToArray());
            }
        }

        public void DeleteBin(string location) => _storage.Delete(GetFilePath(location));

        public void DeleteAllBins()
        {
            foreach (var file in _storage.GetScanFiles("ids_*.bin")) _storage.Delete(file);
        }

        public void RenameBin(string oldLocation, string newFileName)
        {
            string oldPath = GetFilePath(oldLocation);
            string newPath = _storage.GetScanPath($"ids_{oldLocation}_{newFileName}.bin");
            if (_storage.Exists(oldPath)) _storage.Move(oldPath, newPath);
        }
    }
}
