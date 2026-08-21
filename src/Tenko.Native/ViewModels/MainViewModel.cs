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
        private string _searchText = string.Empty;
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
            // 依存サービスを受け取り、初期データとコマンドを準備する。
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
            ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty);
        }

        public string ManualInput
        {
            get => _manualInput;
            set => SetProperty(ref _manualInput, value);
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                {
                    RefreshHistoryView();
                    OnPropertyChanged(nameof(IsFiltered));
                }
            }
        }

        public bool IsFiltered => !string.IsNullOrWhiteSpace(SearchText);

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
        public ICommand ClearSearchCommand { get; }

        // 履歴ファイルの内容を UI コレクションへ反映する。
        private void LoadHistory()
        {
            _allHistory = _historyService.LoadHistory();
            foreach (var record in _allHistory)
            {
                var (name, code) = _studentService.GetStudentInfo(record.Last5);
                record.StudentName = name;
                record.StudentCode = code;
            }
            RefreshHistoryView();
        }

        // 指定したレコードが現在の検索条件に合致するか判定する。
        private bool MatchesSearch(ScanRecord record)
        {
            if (string.IsNullOrWhiteSpace(SearchText)) return true;

            string lowerSearch = SearchText.ToLower();
            return record.StudentName.ToLower().Contains(lowerSearch) ||
                   record.StudentCode.ToLower().Contains(lowerSearch) ||
                   record.Last5.ToString("D5").Contains(lowerSearch) ||
                   record.Barcode.Contains(lowerSearch);
        }

        // 現在のロケーションに一致し、かつ検索条件に合致する履歴のみを UI コレクションへ表示する。
        private void RefreshHistoryView()
        {
            History.Clear();
            var query = _allHistory.Where(h => h.Location == CurrentLocation);

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                query = query.Where(MatchesSearch);
            }

            foreach (var item in query.ToList())
            {
                History.Add(item);
            }
        }

        // ロケーションが選択されているか確認し、未選択なら通知を出す。
        private bool EnsureLocationSelected()
        {
            if (string.IsNullOrEmpty(CurrentLocation))
            {
                _notificationService.Warning("スキャン場所を選択してください。");
                return false;
            }
            return true;
        }

        // 共通の例外ハンドリングと通知処理を行う。
        private void ExecuteWithNotify(Action action, string? successMessage = null, string errorPrefix = "処理失敗")
        {
            try
            {
                action();
                if (!string.IsNullOrEmpty(successMessage)) _notificationService.Success(successMessage);
            }
            catch (Exception ex)
            {
                _notificationService.Error($"{errorPrefix}: {ex.Message}");
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
            if (!EnsureLocationSelected()) return;
            
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

            // 重複チェック (同一ロケーションで同一の学籍番号下5桁)
            if (!ushort.TryParse(ManualInput.Length >= 5 ? ManualInput.Substring(ManualInput.Length - 5) : ManualInput, out ushort last5))
            {
                _notificationService.Error("番号の解析に失敗しました。");
                return;
            }

            if (_allHistory.Any(h => h.Location == CurrentLocation && h.Last5 == last5))
            {
                _notificationService.Warning("この番号は既にスキャン済みです。");
                ManualInput = string.Empty;
                return;
            }

            ProcessScan(ManualInput);
            ManualInput = string.Empty;
        }

        // スキャン情報を履歴と bin に追記する。
        private void ProcessScan(string barcode)
        {
            if (!EnsureLocationSelected()) return;

            ExecuteWithNotify(() =>
            {
                ushort last5 = ushort.Parse(barcode.Length >= 5 ? barcode.Substring(barcode.Length - 5) : barcode);
                var (name, code) = _studentService.GetStudentInfo(last5);
                var record = new ScanRecord
                {
                    Id = $"{DateTimeOffset.Now.ToUnixTimeMilliseconds()}_{last5:D5}_{Guid.NewGuid().ToString("N").Substring(0, 8)}",
                    Timestamp = DateTime.Now,
                    Barcode = barcode,
                    Last5 = last5,
                    StudentName = name,
                    StudentCode = code,
                    Location = CurrentLocation
                };

                _allHistory.Insert(0, record);
                if (MatchesSearch(record)) History.Insert(0, record);

                // 2秒間ハイライト
                record.IsRecentlyAdded = true;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (s, e) => { record.IsRecentlyAdded = false; timer.Stop(); };
                timer.Start();

                _historyService.SaveHistory(_allHistory);
                _scanFileService.AppendLast5(CurrentLocation, last5);
            }, errorPrefix: "スキャン処理失敗");
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

            ExecuteWithNotify(() =>
            {
                _allHistory.Remove(record);
                History.Remove(record);
                _historyService.SaveHistory(_allHistory);
                _scanFileService.RemoveLast5(record.Location, record.Last5);
            }, "レコードを削除しました。", "レコード削除失敗");
        }

        // 現在ロケーションの履歴と bin ファイルを削除する。
        private void DeleteAll()
        {
            if (!EnsureLocationSelected()) return;

            var result = MessageBox.Show(
                $"現在の「{CurrentLocation}」の履歴とバイナリデータを削除しますか？\n他のデータは削除されません。",
                "履歴削除の確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            // 履歴保存や削除の失敗でアプリが落ちないよう、通知付きでまとめて実行する。
            ExecuteWithNotify(() =>
            {
                _allHistory.RemoveAll(h => h.Location == CurrentLocation);
                History.Clear();
                _historyService.SaveHistory(_allHistory);
                _scanFileService.DeleteBin(CurrentLocation);
                CheckBinFile();
            }, successMessage: $"現在の「{CurrentLocation}」の履歴を削除しました。", errorPrefix: "履歴削除失敗");
        }

        // 現在ロケーションの既存 bin を別名へ退避し、履歴を初期化する。
        private void RenameBin(string? newName)
        {
            if (string.IsNullOrEmpty(newName) || !EnsureLocationSelected()) return;
            
            // ファイル名として不正な文字を置換
            var invalidChars = Path.GetInvalidFileNameChars();
            string sanitized = new string(newName.Select(c => invalidChars.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());

            ExecuteWithNotify(() =>
            {
                _scanFileService.RenameBin(CurrentLocation, sanitized);
                _allHistory.RemoveAll(h => h.Location == CurrentLocation);
                History.Clear();
                _historyService.SaveHistory(_allHistory);
                
                ShowBinWarning = false;
                ShowCompleteModal = false;
            }, $"ファイルを ids_{CurrentLocation}_{sanitized}.bin に退避しました。", "名前変更失敗");
        }

        // 現在の履歴を CSV 形式で出力する。
        private void ExportCsv()
        {
            if (History.Count == 0) return;
            // 通知に表示するファイル名と実際の出力を一致させるため、ここで固定する。
            string filename = $"scan_{CurrentLocation}_{DateTime.Now:yyyyMMddHHmm}.csv";
            ExecuteWithNotify(() =>
            {
                using (var writer = new StreamWriter(filename))
                {
                    writer.WriteLine("Timestamp,ID");
                    foreach (var r in History) writer.WriteLine($"{r.FormattedTimestamp},{r.Last5:D5}");
                }
            }, successMessage: $"{filename} を出力しました。", errorPrefix: "CSV出力失敗");
        }

        // 現在の履歴を Last5 の連続バイナリとして出力する。
        private void ExportBin()
        {
            if (History.Count == 0) return;
            // 出力時刻を固定し、表示名と生成ファイルを一致させる。
            string filename = $"ids_{CurrentLocation}_{DateTime.Now:yyyyMMddHHmm}.bin";
            ExecuteWithNotify(() =>
            {
                var data = History.SelectMany(r => BitConverter.GetBytes(r.Last5)).ToArray();
                File.WriteAllBytes(filename, data);
            }, successMessage: $"{filename} を出力しました。", errorPrefix: "BIN出力失敗");
        }

        private DispatcherTimer? _notificationTimer;

        // 通知を短時間表示し、一定時間後に自動で非表示へ戻す。
        private void ShowNotification(string message, NotificationType type)
        {
            NotificationMessage = message;
            NotificationType = type;
            IsNotificationVisible = true;

            _notificationTimer?.Stop();
            if (_notificationTimer == null)
            {
                _notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _notificationTimer.Tick += (s, e) => { IsNotificationVisible = false; _notificationTimer.Stop(); };
            }
            _notificationTimer.Start();
        }
    }
}
