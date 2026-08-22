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
        public string GetBaseDataPath(string fileName) => Path.Combine(_dataDir, fileName);

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
            File.WriteAllText(path, JsonHelper.Serialize(value, indent));
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

        public string[] GetScanFiles(string pattern) => Directory.Exists(_scansDir) ? Directory.GetFiles(_scansDir, pattern) : Array.Empty<string>();
    }
}
