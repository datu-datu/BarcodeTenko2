using System;
using System.IO;

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

            // 末尾から走査して最後に一致する 2 バイトを除去する
            int count = data.Length / 2;
            int target = -1;
            for (int i = count - 1; i >= 0; i--)
            {
                if (BitConverter.ToUInt16(data, i * 2) == last5)
                {
                    target = i;
                    break;
                }
            }
            if (target < 0) return;

            var result = new byte[data.Length - 2];
            int headLength = target * 2;
            Buffer.BlockCopy(data, 0, result, 0, headLength);
            Buffer.BlockCopy(data, headLength + 2, result, headLength, data.Length - headLength - 2);
            _storage.WriteAllBytes(path, result);
        }

        public void DeleteBin(string location) => _storage.Delete(GetFilePath(location));


        public void RenameBin(string oldLocation, string newFileName)
        {
            string oldPath = GetFilePath(oldLocation);
            string newPath = _storage.GetScanPath($"ids_{oldLocation}_{newFileName}.bin");
            if (_storage.Exists(oldPath)) _storage.Move(oldPath, newPath);
        }
    }
}
