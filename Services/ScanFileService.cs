using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Tenko.Native.Services
{
    public class ScanFileService
    {
        // ロケーションに対応する bin ファイルパスを組み立てる。
        private string GetFilePath(string location)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(baseDir, "scans", $"ids_{location}.bin");
        }

        // 対象ロケーションの bin ファイルが存在し、かつ空でないかを返す。
        public bool Exists(string location)
        {
            if (string.IsNullOrEmpty(location)) return false;
            string path = GetFilePath(location);
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }

        // Last5 を 4byte 値として末尾へ追記する。
        public void AppendLast5(string location, int last5)
        {
            if (string.IsNullOrEmpty(location)) return;
            string path = GetFilePath(location);
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(last5);
            }
        }

        // 指定した Last5 の最後の一致を 1 件だけ削除する。
        public void RemoveLast5(string location, int last5)
        {
            if (string.IsNullOrEmpty(location)) return;
            string path = GetFilePath(location);
            if (!File.Exists(path)) return;

            try
            {
                byte[] allBytes = File.ReadAllBytes(path);
                if (allBytes.Length % 4 != 0)
                {
                    // 壊れている場合は読み飛ばすか、例外を投げて上位でハンドリングする
                    return;
                }

                List<int> values = new List<int>();
                using (var ms = new MemoryStream(allBytes))
                using (var reader = new BinaryReader(ms))
                {
                    while (ms.Position < ms.Length)
                    {
                        values.Add(reader.ReadInt32());
                    }
                }

                // 最後に追加された同値を削除するため、末尾側から探索する。
                int index = values.LastIndexOf(last5);
                if (index >= 0)
                {
                    values.RemoveAt(index);
                    // 追記中かもしれないので本来はロック等考慮すべきだが、
                    // 現状はシンプルに書き戻す。
                    using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var writer = new BinaryWriter(stream))
                    {
                        foreach (var v in values)
                        {
                            writer.Write(v);
                        }
                    }
                }
            }
            catch (IOException)
            {
                // ファイル使用中などのエラー
            }
        }

        // 対象ロケーションの bin ファイルを削除する。
        public void DeleteBin(string location)
        {
            if (string.IsNullOrEmpty(location)) return;
            string path = GetFilePath(location);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        // scans 配下の ids_*.bin をすべて削除する。
        public void DeleteAllBins()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string scansDir = Path.Combine(baseDir, "scans");
            if (Directory.Exists(scansDir))
            {
                foreach (var file in Directory.GetFiles(scansDir, "ids_*.bin"))
                {
                    File.Delete(file);
                }
            }
        }

        // 現在ロケーションの bin を別名へ退避する。
        public void RenameBin(string oldLocation, string newFileName)
        {
            // Note: newFileName is expected to be just the suffix part.
            // Requirement: Include the original location in the filename.
            string oldPath = GetFilePath(oldLocation);
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string newPath = Path.Combine(baseDir, "scans", $"ids_{oldLocation}_{newFileName}.bin");

            if (File.Exists(oldPath))
            {
                if (File.Exists(newPath))
                {
                    throw new IOException("Target file already exists.");
                }
                File.Move(oldPath, newPath);
            }
        }
    }
}
