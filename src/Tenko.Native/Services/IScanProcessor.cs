using System.Collections.Generic;
using Tenko.Native.Models;

namespace Tenko.Native.Services
{
    public enum ScanResultStatus
    {
        Success,
        IgnoredDebounce,
        DuplicateWarning,
        ValidationError,
        LocationNotSet,
        Error
    }

    public class ScanResult
    {
        public ScanResultStatus Status { get; set; }
        public ScanRecord? Record { get; set; }
        public string? Message { get; set; }

        public static ScanResult Ok(ScanRecord record) => new() { Status = ScanResultStatus.Success, Record = record };
        public static ScanResult Debounced() => new() { Status = ScanResultStatus.IgnoredDebounce };
        public static ScanResult Duplicate(string message = "この番号は既にスキャン済みです。") => new() { Status = ScanResultStatus.DuplicateWarning, Message = message };
        public static ScanResult Validation(string message) => new() { Status = ScanResultStatus.ValidationError, Message = message };
        public static ScanResult LocationMissing(string message = "スキャン場所を選択してください。") => new() { Status = ScanResultStatus.LocationNotSet, Message = message };
        public static ScanResult Failed(string message) => new() { Status = ScanResultStatus.Error, Message = message };
    }

    public interface IScanProcessor
    {
        List<ScanRecord> LoadHistory();
        ScanResult ProcessScan(string barcode, string location, List<ScanRecord> allHistory);
        void DeleteRecord(ScanRecord record, List<ScanRecord> allHistory);
        void DeleteAllForLocation(string location, List<ScanRecord> allHistory);
        string RenameLocationBin(string location, string newName, List<ScanRecord> allHistory);
        bool CheckBinExists(string location);
    }
}
