using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Tenko.Lite.Common;
using Tenko.Lite.Models;
using Tenko.Lite.Services;

namespace Tenko.Lite.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly IScanProcessor _scanProcessor;
        private readonly SettingsService _settingsService;
        private readonly NotificationService _notificationService;
        private readonly IClockService _clockService;
        private readonly IExportService _exportService;
        private readonly IDialogService _dialogService;
        private readonly ServerSyncService? _serverSyncService;

        private string _manualInput = string.Empty;
        private string _searchText = string.Empty;
        private string _currentLocation = string.Empty;
        private string _currentTimeString = string.Empty;
        private bool _showInitialLocationModal = false;
        private bool _showSettingsModal = false;
        private bool _showBinWarning = false;
        private bool _showCompleteModal = false;
        private string _renameTargetName = string.Empty;
        private string _completeRenameTargetName = string.Empty;
        private string _notificationMessage = string.Empty;
        private NotificationType _notificationType = NotificationType.Success;
        private bool _isNotificationVisible = false;
        private string _syncStatusText = string.Empty;

        private List<ScanRecord> _allHistory = new();
        private DispatcherTimer? _notificationTimer;

        public ObservableCollection<ScanRecord> History { get; } = new();
        public ObservableCollection<string> Locations { get; } = new();

        public MainViewModel(
            IScanProcessor scanProcessor,
            SettingsService settingsService,
            NotificationService notificationService,
            IClockService clockService,
            IExportService exportService,
            IDialogService dialogService,
            ServerSyncService? serverSyncService = null)
        {
            _scanProcessor = scanProcessor;
            _settingsService = settingsService;
            _notificationService = notificationService;
            _clockService = clockService;
            _exportService = exportService;
            _dialogService = dialogService;
            _serverSyncService = serverSyncService;

            // ロケーション一覧と初期ロケーションの反映
            foreach (var loc in _settingsService.Locations)
            {
                Locations.Add(loc);
            }
            _currentLocation = _settingsService.Location;

            // 時計サービスからの現在時刻購読
            UpdateClockString(_clockService.Now);
            _clockService.OnTick += time =>
            {
                if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
                {
                    Application.Current.Dispatcher.Invoke(() => UpdateClockString(time));
                }
                else
                {
                    UpdateClockString(time);
                }
            };

            // サーバー同期ステータスの購読
            if (_serverSyncService != null)
            {
                _syncStatusText = _serverSyncService.StatusMessage;
                _serverSyncService.OnStatusChanged += (status, msg) =>
                {
                    if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
                    {
                        Application.Current.Dispatcher.Invoke(() => SyncStatusText = msg);
                    }
                    else
                    {
                        SyncStatusText = msg;
                    }
                };
            }

            // 通知サービスの購読
            _notificationService.OnNotification += (s, e) => ShowNotification(e.Message, e.Type);

            // コマンド初期化
            SubmitCommand = new RelayCommand(_ => SubmitManualInput());
            DeleteRecordCommand = new RelayCommand<ScanRecord>(record => DeleteRecord(record));
            DeleteAllCommand = new RelayCommand(_ => DeleteAll());
            ExportCsvCommand = new RelayCommand(_ => ExportCsv());
            ExportBinCommand = new RelayCommand(_ => ExportBin());
            ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty);

            OpenSettingsCommand = new RelayCommand(_ => ShowSettingsModal = true);
            CloseSettingsCommand = new RelayCommand(_ => ShowSettingsModal = false);

            OpenCompleteModalCommand = new RelayCommand(_ =>
            {
                if (IsLocationSet)
                {
                    CompleteRenameTargetName = string.Empty;
                    ShowCompleteModal = true;
                }
            });
            CloseCompleteModalCommand = new RelayCommand(_ => ShowCompleteModal = false);
            CompleteRenameCommand = new RelayCommand(_ => CompleteRename());

            RenameWarningCommand = new RelayCommand(_ => ExecuteRenameWarning());
            DismissWarningCommand = new RelayCommand(_ => ShowBinWarning = false);

            StartSessionCommand = new RelayCommand(_ =>
            {
                if (IsLocationSet)
                {
                    ShowInitialLocationModal = false;
                    _notificationService.Success($"「{CurrentLocation}」で点呼を開始しました。");
                }
                else
                {
                    _notificationService.Warning("スキャン場所を選択してください。");
                }
            });

            // 初期データ読み込み
            LoadHistory();
            CheckBinFile();

            // 場所が未設定の場合は初回場所選択モーダルを表示
            if (!IsLocationSet)
            {
                ShowInitialLocationModal = true;
            }
        }

        #region Properties

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

        public string CurrentTimeString
        {
            get => _currentTimeString;
            private set => SetProperty(ref _currentTimeString, value);
        }

        public bool ShowInitialLocationModal
        {
            get => _showInitialLocationModal;
            set => SetProperty(ref _showInitialLocationModal, value);
        }

        public bool ShowSettingsModal
        {
            get => _showSettingsModal;
            set => SetProperty(ref _showSettingsModal, value);
        }

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

        public string RenameTargetName
        {
            get => _renameTargetName;
            set => SetProperty(ref _renameTargetName, value);
        }

        public string CompleteRenameTargetName
        {
            get => _completeRenameTargetName;
            set => SetProperty(ref _completeRenameTargetName, value);
        }

        public string SyncStatusText
        {
            get => _syncStatusText;
            set => SetProperty(ref _syncStatusText, value);
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

        #endregion

        #region Commands

        public ICommand SubmitCommand { get; }
        public ICommand DeleteRecordCommand { get; }
        public ICommand DeleteAllCommand { get; }
        public ICommand ExportCsvCommand { get; }
        public ICommand ExportBinCommand { get; }
        public ICommand ClearSearchCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand CloseSettingsCommand { get; }
        public ICommand OpenCompleteModalCommand { get; }
        public ICommand CloseCompleteModalCommand { get; }
        public ICommand CompleteRenameCommand { get; }
        public ICommand RenameWarningCommand { get; }
        public ICommand DismissWarningCommand { get; }
        public ICommand StartSessionCommand { get; }

        #endregion

        #region Methods

        private void UpdateClockString(DateTime time)
        {
            CurrentTimeString = time.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private void LoadHistory()
        {
            _allHistory = _scanProcessor.LoadHistory();
            RefreshHistoryView();
        }

        private void CheckBinFile()
        {
            ShowBinWarning = _scanProcessor.CheckBinExists(CurrentLocation);
        }

        // Lite版: 学籍番号(下5桁)とバーコードのみで検索判定
        private bool MatchesSearch(ScanRecord record)
        {
            if (string.IsNullOrWhiteSpace(SearchText)) return true;

            string lowerSearch = SearchText.ToLower();
            return record.Last5.ToString("D5").Contains(lowerSearch) ||
                   record.Barcode.Contains(lowerSearch);
        }

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

        public void SubmitManualInput()
        {
            if (string.IsNullOrWhiteSpace(ManualInput)) return;

            string input = ManualInput;
            var result = _scanProcessor.ProcessScan(input, CurrentLocation, _allHistory);

            switch (result.Status)
            {
                case ScanResultStatus.Success:
                    if (result.Record != null)
                    {
                        if (MatchesSearch(result.Record))
                        {
                            History.Insert(0, result.Record);
                        }

                        result.Record.IsRecentlyAdded = true;
                        if (Application.Current?.Dispatcher != null)
                        {
                            var timer = new DispatcherTimer(DispatcherPriority.Background, Application.Current.Dispatcher)
                            {
                                Interval = TimeSpan.FromSeconds(2)
                            };
                            timer.Tick += (s, e) =>
                            {
                                result.Record.IsRecentlyAdded = false;
                                timer.Stop();
                            };
                            timer.Start();
                        }
                    }
                    ManualInput = string.Empty;
                    break;

                case ScanResultStatus.IgnoredDebounce:
                    ManualInput = string.Empty;
                    break;

                case ScanResultStatus.DuplicateWarning:
                    _notificationService.Warning(result.Message ?? "この番号は既にスキャン済みです。");
                    ManualInput = string.Empty;
                    break;

                case ScanResultStatus.LocationNotSet:
                    _notificationService.Warning(result.Message ?? "スキャン場所を選択してください。");
                    break;

                case ScanResultStatus.ValidationError:
                case ScanResultStatus.Error:
                    _notificationService.Error(result.Message ?? "処理に失敗しました。");
                    break;
            }
        }

        private void DeleteRecord(ScanRecord? record)
        {
            if (record == null) return;

            bool confirmed = _dialogService.Confirm(
                $"このレコードを削除しますか？\n時刻: {record.FormattedTimestamp}\nバーコード: {record.Barcode}",
                "レコード削除の確認",
                MessageBoxImage.Question);

            if (!confirmed) return;

            try
            {
                _scanProcessor.DeleteRecord(record, _allHistory);
                History.Remove(record);
                _notificationService.Success("レコードを削除しました。");
            }
            catch (Exception ex)
            {
                _notificationService.Error($"レコード削除失敗: {ex.Message}");
            }
        }

        private void DeleteAll()
        {
            if (!IsLocationSet)
            {
                _notificationService.Warning("スキャン場所を選択してください。");
                return;
            }

            bool confirmed = _dialogService.Confirm(
                $"現在の「{CurrentLocation}」の履歴とバイナリデータを削除しますか？\n他のデータは削除されません。",
                "履歴削除の確認",
                MessageBoxImage.Warning);

            if (!confirmed) return;

            try
            {
                _scanProcessor.DeleteAllForLocation(CurrentLocation, _allHistory);
                History.Clear();
                CheckBinFile();
                _notificationService.Success($"現在の「{CurrentLocation}」の履歴を削除しました。");
            }
            catch (Exception ex)
            {
                _notificationService.Error($"履歴削除失敗: {ex.Message}");
            }
        }

        private void CompleteRename()
        {
            if (string.IsNullOrWhiteSpace(CompleteRenameTargetName) || !IsLocationSet) return;

            try
            {
                string sanitized = _scanProcessor.RenameLocationBin(CurrentLocation, CompleteRenameTargetName, _allHistory);
                History.Clear();
                ShowBinWarning = false;
                ShowCompleteModal = false;
                CompleteRenameTargetName = string.Empty;
                _notificationService.Success($"ファイルを ids_{CurrentLocation}_{sanitized}.bin に退避しました。");
            }
            catch (Exception ex)
            {
                _notificationService.Error($"名前変更失敗: {ex.Message}");
            }
        }

        private void ExecuteRenameWarning()
        {
            if (string.IsNullOrWhiteSpace(RenameTargetName) || !IsLocationSet) return;

            try
            {
                string sanitized = _scanProcessor.RenameLocationBin(CurrentLocation, RenameTargetName, _allHistory);
                History.Clear();
                ShowBinWarning = false;
                RenameTargetName = string.Empty;
                _notificationService.Success($"ファイルを ids_{CurrentLocation}_{sanitized}.bin に退避しました。");
            }
            catch (Exception ex)
            {
                _notificationService.Error($"名前変更失敗: {ex.Message}");
            }
        }

        private void ExportCsv()
        {
            if (History.Count == 0) return;

            try
            {
                string fileName = _exportService.ExportCsv(CurrentLocation, History);
                _notificationService.Success($"{fileName} を出力しました。");
            }
            catch (Exception ex)
            {
                _notificationService.Error($"CSV出力失敗: {ex.Message}");
            }
        }

        private void ExportBin()
        {
            if (History.Count == 0) return;

            try
            {
                string fileName = _exportService.ExportBin(CurrentLocation, History);
                _notificationService.Success($"{fileName} を出力しました。");
            }
            catch (Exception ex)
            {
                _notificationService.Error($"BIN出力失敗: {ex.Message}");
            }
        }

        private void ShowNotification(string message, NotificationType type)
        {
            NotificationMessage = message;
            NotificationType = type;
            IsNotificationVisible = true;

            _notificationTimer?.Stop();
            if (Application.Current?.Dispatcher != null)
            {
                if (_notificationTimer == null)
                {
                    _notificationTimer = new DispatcherTimer(DispatcherPriority.Background, Application.Current.Dispatcher)
                    {
                        Interval = TimeSpan.FromSeconds(2)
                    };
                    _notificationTimer.Tick += (s, e) =>
                    {
                        IsNotificationVisible = false;
                        _notificationTimer.Stop();
                    };
                }
                _notificationTimer.Start();
            }
        }

        #endregion
    }
}
