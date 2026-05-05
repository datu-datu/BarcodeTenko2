using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Tenko.Native.Models
{
    public class ScanRecord : INotifyPropertyChanged
    {
        public string Id { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public int Last5 { get; set; }
        public string Location { get; set; } = string.Empty;

        // Metadata resolved from student data
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;

        private bool _isRecentlyAdded;
        [JsonIgnore]
        public bool IsRecentlyAdded
        {
            get => _isRecentlyAdded;
            set
            {
                if (_isRecentlyAdded != value)
                {
                    _isRecentlyAdded = value;
                    OnPropertyChanged();
                }
            }
        }

        // Display helper
        public string FormattedTimestamp => Timestamp.ToString("MM/dd_HH:mm:ss");

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
