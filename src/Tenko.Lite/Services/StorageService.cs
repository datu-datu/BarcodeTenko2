using System;
using System.Diagnostics;
using System.IO;
using Tenko.Lite.Infrastructure;

namespace Tenko.Lite.Services
{
    public class StorageService
    {
        private readonly string _baseDir;
        private readonly string _dataDir;
        private readonly string _scansDir;

        public StorageService(string? baseDir = null)
        {
            _baseDir = baseDir ?? AppDomain.CurrentDomain.BaseDirectory;
            _dataDir = Path.Combine(_baseDir, "data");
            _scansDir = Path.Combine(_baseDir, "scans");

            EnsureDirectories();
        }

        private void EnsureDirectories()
        {
            if (!Directory.Exists(_dataDir)) Directory.CreateDirectory(_dataDir);
            if (!Directory.Exists(_scansDir)) Directory.CreateDirectory(_scansDir);
        }

        public string GetDataPath(string fileName) => Path.Combine(_dataDir, fileName);
        public string GetScanPath(string fileName) => Path.Combine(_scansDir, fileName);

        public bool Exists(string path) => File.Exists(path);

        public T? LoadJson<T>(string path)
        {
            if (!Exists(path)) return default;
            try
            {
                return JsonHelper.Deserialize<T>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StorageService] JSON load failed for '{path}': {ex.Message}");
                return default;
            }
        }

        public void SaveJson<T>(string path, T value, bool indent = false)
        {
            // 一時ファイルへ書き出してから置換し、書き込み途中のクラッシュによる破損を防ぐ
            string tempPath = path + ".tmp";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonHelper.SerializeToStream(stream, value, indent);
            }
            File.Move(tempPath, path, true);
        }

        public byte[] ReadAllBytes(string path) => Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
        public void WriteAllBytes(string path, byte[] data) => File.WriteAllBytes(path, data);
        public void AppendAllBytes(string path, byte[] data)
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
            stream.Write(data, 0, data.Length);
        }
        
        public void Delete(string path) { if (Exists(path)) File.Delete(path); }
        public void Move(string source, string dest) { if (Exists(source)) File.Move(source, dest); }
    }
}
