using System;
using Tenko.Native.ViewModels;

namespace Tenko.Native.Models
{
    public class ScanRecord : ViewModelBase
    {
        private bool _isRecentlyAdded;

        public string Id { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public ushort Last5 { get; set; }
        public string StudentName { get; set; } = string.Empty;
        public string StudentCode { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;

        public bool IsRecentlyAdded
        {
            get => _isRecentlyAdded;
            set => SetProperty(ref _isRecentlyAdded, value);
        }

        // Display helper
        public string FormattedTimestamp => Timestamp.ToString("MM/dd_HH:mm:ss");
    }
}
