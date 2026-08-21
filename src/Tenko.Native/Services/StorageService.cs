using System;
using System.IO;
using Tenko.Native.Infrastructure;

namespace Tenko.Native.Services
{
    public class StorageService
    {
        private readonly string _baseDir;
        private readonly string _dataDir;
        private readonly string _scansDir;

        // 保存先ディレクトリを初期化し、必要なフォルダを作成する。
        public StorageService()
        {
            _baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _dataDir = Path.Combine(_baseDir, "data");
            _scansDir = Path.Combine(_baseDir, "scans");

            EnsureDirectories();
        }

        // data / scans ディレクトリの存在を保証する。
        private void EnsureDirectories()
        {
            if (!Directory.Exists(_dataDir)) Directory.CreateDirectory(_dataDir);
            if (!Directory.Exists(_scansDir)) Directory.CreateDirectory(_scansDir);
        }

        // data 配下のフルパスを返す。
        public string GetDataPath(string fileName) => Path.Combine(_dataDir, fileName);
        // scans 配下のフルパスを返す。
        public string GetScanPath(string fileName) => Path.Combine(_scansDir, fileName);
        // data 配下のフルパスを返す（既存用途のため別名で保持）。
        public string GetBaseDataPath(string fileName) => Path.Combine(_dataDir, fileName);

        // ファイルの存在を確認する。
        public bool Exists(string path) => File.Exists(path);

        // JSON を読み込んで型に復元する。
        public T? LoadJson<T>(string path)
        {
            if (!Exists(path)) return default;
            try
            {
                return JsonHelper.Deserialize<T>(File.ReadAllText(path));
            }
            catch { return default; }
        }

        // JSON をファイルへ保存する。
        public void SaveJson<T>(string path, T value, bool indent = false)
        {
            File.WriteAllText(path, JsonHelper.Serialize(value, indent));
        }

        // ファイルをバイト配列で読み込む。
        public byte[] ReadAllBytes(string path) => Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
        // ファイルをバイト配列で上書き保存する。
        public void WriteAllBytes(string path, byte[] data) => File.WriteAllBytes(path, data);
        // バイト配列を末尾に追記する。
        public void AppendAllBytes(string path, byte[] data)
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
            stream.Write(data, 0, data.Length);
        }
        
        // ファイルが存在する場合のみ削除する。
        public void Delete(string path) { if (Exists(path)) File.Delete(path); }
        // ファイルが存在する場合のみ移動する。
        public void Move(string source, string dest) { if (Exists(source)) File.Move(source, dest); }

        // scans 配下のファイル一覧を取得する。
        public string[] GetScanFiles(string pattern) => Directory.Exists(_scansDir) ? Directory.GetFiles(_scansDir, pattern) : Array.Empty<string>();
    }
}
