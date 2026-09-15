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

        /// <summary>
        /// 末尾から走査して最後に一致する 2 バイトを除去する。除去できた場合のみ true を返す
        /// </summary>
        public bool RemoveLast5(string location, ushort last5)
        {
            if (string.IsNullOrEmpty(location)) return false;
            string path = GetFilePath(location);
            byte[] data = _storage.ReadAllBytes(path);
            if (data.Length == 0 || data.Length % 2 != 0) return false;

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
            if (target < 0) return false;

            var result = new byte[data.Length - 2];
            int headLength = target * 2;
            Buffer.BlockCopy(data, 0, result, 0, headLength);
            Buffer.BlockCopy(data, headLength + 2, result, headLength, data.Length - headLength - 2);
            _storage.WriteAllBytes(path, result);
            return true;
        }

        /// <summary>
        /// 指定値が BIN 内に何個含まれるかを数える（履歴との件数突き合わせ用）
        /// </summary>
        public int CountLast5(string location, ushort last5)
        {
            if (string.IsNullOrEmpty(location)) return 0;
            byte[] data = _storage.ReadAllBytes(GetFilePath(location));
            if (data.Length == 0 || data.Length % 2 != 0) return 0;

            int count = 0;
            for (int i = 0; i < data.Length / 2; i++)
            {
                if (BitConverter.ToUInt16(data, i * 2) == last5)
                {
                    count++;
                }
            }
            return count;
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
