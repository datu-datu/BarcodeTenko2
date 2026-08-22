using System.Collections.Generic;
using Tenko.Lite.Models;

namespace Tenko.Lite.Services
{
    public interface IExportService
    {
        string ExportCsv(string location, IEnumerable<ScanRecord> records, string? outputDir = null);
        string ExportBin(string location, IEnumerable<ScanRecord> records, string? outputDir = null);
    }
}
