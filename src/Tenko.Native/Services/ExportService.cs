using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tenko.Native.Models;

namespace Tenko.Native.Services
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

            using (var writer = new StreamWriter(fullPath))
            {
                writer.WriteLine("Timestamp,ID");
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
