using System.Collections.Generic;
using Tenko.Native.Models;

namespace Tenko.Native.Services
{
    public interface IExportService
    {
        string ExportCsv(string location, IEnumerable<ScanRecord> records, string? outputDir = null);
        string ExportBin(string location, IEnumerable<ScanRecord> records, string? outputDir = null);
    }
}
