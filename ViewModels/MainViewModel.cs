using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Tenko.Native.Models;
using Tenko.Native.Services;

namespace Tenko.Native.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly SettingsService _settingsService;
        private readonly HistoryService _historyService;
        private readonly ScanFileService _scanFileService;
        private readonly NotificationService _notificationService;
        private readonly StudentService _studentService;

        private string _manualInput = string.Empty;
        private string _currentLocation = string.Empty;
        private bool _showBinWarning = false;
        private bool _showCompleteModal = false;
        private string _notificationMessage = string.Empty;
        private NotificationType _notificationType = NotificationType.Success;
        private bool _isNotificationVisible = false;
        private List<ScanRecord> _allHistory = new();

        public ObservableCollection<ScanRecord> History { get; } = new();
        public ObservableCollection<string> Locations { get; } = new();

        public MainViewModel(
            SettingsService settingsService,
            HistoryService historyService,
            ScanFileService scanFileService,
            NotificationService notificationService,
            StudentService studentService)
        {
            _settingsService = settingsService;
            _historyService = historyService;
            _scanFileService = scanFileService;
            _notificationService = notificationService;
            _studentService = studentService;

            foreach (var loc in _settingsService.Locations)
            {
                Locations.Add(loc);
            }

            _currentLocation = _settingsService.Location;
            
            _notificationService.OnNotification += (s, e) => ShowNotification(e.Message, e.Type);

            LoadHistory();
            CheckBinFile();

            SubmitCommand = new RelayCommand(_ => SubmitManualInput());
            DeleteRecordCommand = new RelayCommand<ScanRecord>(record => DeleteRecord(record));
            DeleteAllCommand = new RelayCommand(_ => DeleteAll());
            ExportCsvCommand = new RelayCommand(_ => ExportCsv());
            ExportBinCommand = new RelayCommand(_ => ExportBin());
            RenameBinCommand = new RelayCommand<string>(newName => RenameBin(newName));
            OpenCompleteModalCommand = new RelayCommand(_ => { if (IsLocationSet) ShowCompleteModal = true; });
        }

        public string ManualInput
        {
            get => _manualInput;
            set => SetProperty(ref _manualInput, value);
        }

        public string CurrentLocation
        {
            get => _currentLocation;
            set
            {
                if (SetProperty(ref _currentLocation, value))
                {
                    _settingsService.Location = value;
                    OnPropertyChanged(nameof(IsLocationSet));
                    CheckBinFile();
                    RefreshHistoryView();
                }
            }
        }

        public bool IsLocationSet => !string.IsNullOrEmpty(CurrentLocation);

        public bool ShowBinWarning
        {
            get => _showBinWarning;
            set => SetProperty(ref _showBinWarning, value);
        }

        public bool ShowCompleteModal
        {
            get => _showCompleteModal;
            set => SetProperty(ref _showCompleteModal, value);
        }

        public string NotificationMessage
        {
            get => _notificationMessage;
            set => SetProperty(ref _notificationMessage, value);
        }

        public NotificationType NotificationType
        {
            get => _notificationType;
            set => SetProperty(ref _notificationType, value);
        }

        public bool IsNotificationVisible
        {
            get => _isNotificationVisible;
            set => SetProperty(ref _isNotificationVisible, value);
        }

        public ICommand SubmitCommand { get; }
        public ICommand DeleteRecordCommand { get; }
        public ICommand DeleteAllCommand { get; }
        public ICommand ExportCsvCommand { get; }
        public ICommand ExportBinCommand { get; }
        public ICommand RenameBinCommand { get; }
        public ICommand OpenCompleteModalCommand { get; }

        // 履歴ファイルの内容を UI コレクションへ反映する。
        private void LoadHistory()
        {
            _allHistory = _historyService.LoadHistory();
            foreach (var record in _allHistory)
            {
                var student = _studentService.GetStudent(record.Last5);
                if (student != null)
                {
                    record.Name = student.Name;
                    record.Code = student.Code;
                }
            }
            RefreshHistoryView();
        }

        // 現在のロケーションに一致する履歴のみを UI コレクションへ表示する。
        private void RefreshHistoryView()
        {
            History.Clear();
            var filtered = _allHistory.Where(h => h.Location == CurrentLocation).ToList();
            foreach (var item in filtered)
            {
                History.Add(item);
            }
        }

        // 現在ロケーションの bin ファイル有無を確認し、警告表示状態を更新する。
        private void CheckBinFile()
        {
            ShowBinWarning = _scanFileService.Exists(CurrentLocation);
        }

        // 手入力バーコードを検証し、要件を満たす場合のみスキャン処理を実行する。
        private void SubmitManualInput()
        {
            if (string.IsNullOrWhiteSpace(ManualInput)) return;

            if (string.IsNullOrEmpty(CurrentLocation))
            {
                _notificationService.Warning("スキャン場所を選択してください。");
                return;
            }
            
            // 数字以外は受け付けない。
            if (!ManualInput.All(char.IsDigit))
            {
                _notificationService.Error("数字のみ入力可能です。");
                return;
            }

            // 仕様上、5桁または10桁のみ許可する。
            if (ManualInput.Length != 5 && ManualInput.Length != 10)
            {
                _notificationService.Error("5桁または10桁の数字を入力してください。");
                return;
            }

            if (!ushort.TryParse(ManualInput.Length >= 5 ? ManualInput.Substring(ManualInput.Length - 5) : ManualInput, out ushort last5))
            {
                _notificationService.Error("入力値が正しくありません。");
                return;
            }

            // 重複チェック (同一ロケーションで同一の学籍番号下5桁)
            if (History.Any(h => h.Last5 == last5))
            {
                _notificationService.Warning($"学籍番号 {last5:D5} は既にスキャン済みです。");
                ManualInput = string.Empty;
                return;
            }

            ProcessScan(ManualInput);
            ManualInput = string.Empty;
        }

        // スキャン情報を履歴と bin に追記する。
        private void ProcessScan(string barcode)
        {
            if (string.IsNullOrEmpty(CurrentLocation))
            {
                _notificationService.Warning("スキャン場所を選択してください。");
                return;
            }

            try
            {
                if (!ushort.TryParse(barcode.Length >= 5 ? barcode.Substring(barcode.Length - 5) : barcode, out ushort last5))
                {
                    _notificationService.Error("スキャンデータ解析失敗");
                    return;
                }
                var record = new ScanRecord
                {
                    // 同一ミリ秒の衝突回避のため Guid 断片を付与する。
                    Id = $"{DateTimeOffset.Now.ToUnixTimeMilliseconds()}_{last5:D5}_{Guid.NewGuid().ToString("N").Substring(0, 8)}",
                    Timestamp = DateTime.Now,
                    Barcode = barcode,
                    Last5 = last5,
                    Location = CurrentLocation,
                    IsRecentlyAdded = true
                };

                var student = _studentService.GetStudent(last5);
                if (student != null)
                {
                    record.Name = student.Name;
                    record.Code = student.Code;
                }

                _allHistory.Insert(0, record);
                History.Insert(0, record);

                // Highlight for 2 seconds
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (s, e) =>
                {
                    record.IsRecentlyAdded = false;
                    timer.Stop();
                };
                timer.Start();

                _historyService.SaveHistory(_allHistory);
                _scanFileService.AppendLast5(CurrentLocation, last5);
            }
            catch (Exception ex)
            {
                _notificationService.Error($"スキャン処理失敗: {ex.Message}");
            }
        }

        // 指定レコードを履歴と bin から削除する。
        private void DeleteRecord(ScanRecord? record)
        {
            if (record == null) return;

            var result = MessageBox.Show(
                $"このレコードを削除しますか？\n時刻: {record.FormattedTimestamp}\nバーコード: {record.Barcode}",
                "レコード削除の確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                _allHistory.Remove(record);
                History.Remove(record);
                _historyService.SaveHistory(_allHistory);
                _scanFileService.RemoveLast5(record.Location, record.Last5);
                _notificationService.Success("レコードを削除しました。");
            }
            catch (Exception ex)
            {
                _notificationService.Error($"レコード削除失敗: {ex.Message}");
            }
        }

        // 現在ロケーションの履歴と bin ファイルを削除する。
        private void DeleteAll()
        {
            if (string.IsNullOrEmpty(CurrentLocation)) return;

            var result = MessageBox.Show(
                $"現在の「{CurrentLocation}」の履歴とバイナリデータを削除しますか？\n他のデータは削除されません。",
                "履歴削除の確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            _allHistory.RemoveAll(h => h.Location == CurrentLocation);
            History.Clear();
            _historyService.SaveHistory(_allHistory);
            _scanFileService.DeleteBin(CurrentLocation);
            CheckBinFile();
            _notificationService.Success($"現在の「{CurrentLocation}」の履歴を削除しました。");
        }

        // 現在ロケーションの既存 bin を別名へ退避し、履歴を初期化する。
        private void RenameBin(string? newName)
        {
            if (string.IsNullOrEmpty(newName)) return;
            if (string.IsNullOrEmpty(CurrentLocation)) return;
            
            // ファイル名として不正な文字を置換する。
            var invalidChars = Path.GetInvalidFileNameChars();
            string sanitized = new string(newName
                .Select(c => invalidChars.Contains(c) || char.IsControl(c) ? '_' : c)
                .ToArray());

            try
            {
                _scanFileService.RenameBin(CurrentLocation, sanitized);
                
                // 退避後は現在ロケーションの新規計測として履歴をリセットする。
                _allHistory.RemoveAll(h => h.Location == CurrentLocation);
                History.Clear();
                _historyService.SaveHistory(_allHistory);
                
                ShowBinWarning = false;
                ShowCompleteModal = false;
                _notificationService.Success($"ファイルを ids_{CurrentLocation}_{sanitized}.bin に退避しました。");
            }
            catch (Exception ex)
            {
                _notificationService.Error($"名前変更失敗: {ex.Message}");
            }
        }

        // 現在の履歴を CSV 形式で出力する。
        private void ExportCsv()
        {
            if (History.Count == 0) return;
            try
            {
                string filename = $"scan_{CurrentLocation}_{DateTime.Now:yyyyMMddHHmm}.csv";
                using (var writer = new StreamWriter(filename))
                {
                    writer.WriteLine("Timestamp,ID");
                    foreach (var r in History)
                    {
                        writer.WriteLine($"{r.FormattedTimestamp},{r.Last5:D5}");
                    }
                }
                _notificationService.Success($"{filename} を出力しました。");
            }
            catch (Exception ex)
            {
                _notificationService.Error($"CSV出力失敗: {ex.Message}");
            }
        }

        // 現在の履歴を Last5 の連続バイナリとして出力する。
        private void ExportBin()
        {
            if (History.Count == 0) return;
            try
            {
                string filename = $"ids_{CurrentLocation}_{DateTime.Now:yyyyMMddHHmm}.bin";
                using (var stream = new FileStream(filename, FileMode.Create))
                using (var writer = new BinaryWriter(stream))
                {
                    foreach (var r in History)
                    {
                        writer.Write(r.Last5);
                    }
                }
                _notificationService.Success($"{filename} を出力しました。");
            }
            catch (Exception ex)
            {
                _notificationService.Error($"BIN出力失敗: {ex.Message}");
            }
        }

        private DispatcherTimer? _notificationTimer;

        // 通知を短時間表示し、一定時間後に自動で非表示へ戻す。
        private void ShowNotification(string message, NotificationType type)
        {
            // Reset to None first to ensure the DataTrigger in XAML can re-fire if the type is the same.
            NotificationType = NotificationType.None;

            NotificationMessage = message;
            NotificationType = type;
            IsNotificationVisible = true;

            _notificationTimer?.Stop();
            _notificationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(2000)
            };
            _notificationTimer.Tick += (s, e) =>
            {
                IsNotificationVisible = false;
                NotificationMessage = string.Empty;
                NotificationType = NotificationType.None;
                _notificationTimer?.Stop();
            };
            _notificationTimer.Start();
        }
    }
}
