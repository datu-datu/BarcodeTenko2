using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tenko.Lite.Models;

namespace Tenko.Lite.Services
{
    public class ExportService : IExportService
    {
        public string ExportCsv(string location, IEnumerable<ScanRecord> records, string? outputDir = null)
        {
            var recordList = records.ToList();
            if (recordList.Count == 0)
            {
                throw new InvalidOperationException("エクスポート対象のデータが存在しません。");
            }

            string fileName = $"scan_{location}_{DateTime.Now:yyyyMMddHHmm}.csv";
            string fullPath = string.IsNullOrEmpty(outputDir) ? fileName : Path.Combine(outputDir, fileName);

            // Excel で日本語ヘッダを正しく開けるよう BOM 付き UTF-8 で書き出す
            using (var writer = new StreamWriter(fullPath, false, new System.Text.UTF8Encoding(true)))
            {
                writer.WriteLine("時刻,学籍番号");
                foreach (var r in recordList)
                {
                    writer.WriteLine($"{r.FormattedTimestamp},{r.Last5:D5}");
                }
            }

            return fileName;
        }

        public string ExportBin(string location, IEnumerable<ScanRecord> records, string? outputDir = null)
        {
            var recordList = records.ToList();
            if (recordList.Count == 0)
            {
                throw new InvalidOperationException("エクスポート対象のデータが存在しません。");
            }

            string fileName = $"ids_{location}_{DateTime.Now:yyyyMMddHHmm}.bin";
            string fullPath = string.IsNullOrEmpty(outputDir) ? fileName : Path.Combine(outputDir, fileName);

            var data = recordList.SelectMany(r => BitConverter.GetBytes(r.Last5)).ToArray();
            File.WriteAllBytes(fullPath, data);

            return fileName;
        }
    }
}
