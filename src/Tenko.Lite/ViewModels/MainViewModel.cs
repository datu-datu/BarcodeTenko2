using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Tenko.Lite.Common;
using Tenko.Lite.Models;
using Tenko.Lite.Services;

namespace Tenko.Lite.ViewModels
{
    public class MainViewModel : ViewModelBase, IDisposable
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
        private bool _disposed;

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
            _clockService.OnTick += OnClockTick;

            // サーバー同期ステータスの購読
            if (_serverSyncService != null)
            {
                _syncStatusText = _serverSyncService.StatusMessage;
                _serverSyncService.OnStatusChanged += OnSyncStatusChanged;
            }

            // 通知サービスの購読
            _notificationService.OnNotification += OnNotificationReceived;

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
            // CheckBinFile();

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

        /// <summary>
        /// 現在場所の総件数（絞り込みの影響を受けない累計）
        /// </summary>
        public int CurrentLocationCount => _allHistory.Count(h => h.Location == CurrentLocation);

        /// <summary>
        /// 絞り込み後の表示件数
        /// </summary>
        public int FilteredCount => History.Count;

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

        /// <summary>
        /// エクスポート先ディレクトリ。未指定ならマイドキュメント配下の「Tenko出力」を使う
        /// </summary>
        public string? ExportDirectory { get; set; }

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

        private void OnClockTick(DateTime time) => RunOnUi(() => UpdateClockString(time));

        private void OnSyncStatusChanged(SyncStatus status, string message) => RunOnUi(() => SyncStatusText = message);

        private void OnNotificationReceived(object? sender, NotificationEventArgs e)
            => RunOnUi(() => ShowNotification(e.Message, e.Type));

        /// <summary>
        /// 現在のスレッドが UI スレッドでない場合のみディスパッチャへ委譲する
        /// </summary>
        private static void RunOnUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
            }
            else if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                // 終了処理中は Invoke が例外になるため何もしない
            }
            else
            {
                dispatcher.Invoke(action);
            }
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
        private static bool MatchesSearch(ScanRecord record, string searchLower)
        {
            return record.Last5.ToString("D5").Contains(searchLower) ||
                   record.Barcode.Contains(searchLower);
        }

        private void RefreshHistoryView()
        {
            History.Clear();

            string searchLower = IsFiltered ? SearchText.ToLower() : string.Empty;

            foreach (var item in _allHistory)
            {
                if (item.Location != CurrentLocation) continue;
                if (searchLower.Length > 0 && !MatchesSearch(item, searchLower)) continue;

                History.Add(item);
            }

            OnPropertyChanged(nameof(CurrentLocationCount));
            OnPropertyChanged(nameof(FilteredCount));
        }

        public void SubmitManualInput()
        {
            if (string.IsNullOrWhiteSpace(ManualInput)) return;

            string input = ManualInput;

            ScanResult result;
            try
            {
                result = _scanProcessor.ProcessScan(input, CurrentLocation, _allHistory);
            }
            catch (Exception ex)
            {
                // 保存失敗時も入力値は消さず、再試行できるようにする
                Debug.WriteLine($"[MainViewModel] スキャン保存に失敗: {ex}");
                _notificationService.Error("保存に失敗しました。ディスクの空き容量と書き込み権限を確認してください。");
                return;
            }

            switch (result.Status)
            {
                case ScanResultStatus.Success:
                    if (result.Record != null)
                    {
                        if (!IsFiltered || MatchesSearch(result.Record, SearchText.ToLower()))
                        {
                            History.Insert(0, result.Record);
                        }

                        // ハイライトフラグを付与 (フェードアウトは XAML Storyboard で実行)
                        result.Record.IsRecentlyAdded = true;

                        OnPropertyChanged(nameof(CurrentLocationCount));
                        OnPropertyChanged(nameof(FilteredCount));
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
                $"この1件を削除しますか？\n時刻: {record.FormattedTimestamp}\nバーコード: {record.Barcode}",
                "削除の確認",
                MessageBoxImage.Question);

            if (!confirmed) return;

            try
            {
                if (!_scanProcessor.DeleteRecord(record, _allHistory, out bool binMismatch))
                {
                    // 表示中のインスタンスと実データがずれている可能性があるため表示を作り直す
                    RefreshHistoryView();
                    _notificationService.Warning("対象の履歴が見つかりませんでした。表示を更新しました。");
                    return;
                }

                // 参照ではなく Id で一致する要素を表示からも取り除く
                var target = History.FirstOrDefault(h => h.Id == record.Id);
                if (target != null)
                {
                    History.Remove(target);
                }

                OnPropertyChanged(nameof(CurrentLocationCount));
                OnPropertyChanged(nameof(FilteredCount));

                if (binMismatch)
                {
                    _notificationService.Warning("履歴は削除しましたが、BINファイル側の対応データを特定できませんでした。");
                }
                else
                {
                    _notificationService.Success("1件削除しました。");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainViewModel] 削除に失敗: {ex}");
                _notificationService.Error("削除に失敗しました。");
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
                OnPropertyChanged(nameof(CurrentLocationCount));
                OnPropertyChanged(nameof(FilteredCount));
                CheckBinFile();
                _notificationService.Success($"現在の「{CurrentLocation}」の履歴を削除しました。");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainViewModel] 履歴削除に失敗: {ex}");
                _notificationService.Error("履歴の削除に失敗しました。");
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
                _notificationService.Success($"「ids_{CurrentLocation}_{sanitized}.bin」として保存しました。scansフォルダをご確認ください。");
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
                string outputDir = GetExportDirectory();
                string fileName = _exportService.ExportCsv(CurrentLocation, History, outputDir);
                _notificationService.Success($"{Path.Combine(outputDir, fileName)} に出力しました。");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainViewModel] CSV出力に失敗: {ex}");
                _notificationService.Error("CSVの出力に失敗しました。");
            }
        }

        /// <summary>
        /// 書き込み可能な出力先を返し、無ければ作成する。未指定ならマイドキュメント配下を使う
        /// </summary>
        private string GetExportDirectory()
        {
            string dir = string.IsNullOrWhiteSpace(ExportDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tenko出力")
                : ExportDirectory;

            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            return dir;
        }

        private void ExportBin()
        {
            if (History.Count == 0) return;

            try
            {
                string outputDir = GetExportDirectory();
                string fileName = _exportService.ExportBin(CurrentLocation, History, outputDir);
                _notificationService.Success($"{Path.Combine(outputDir, fileName)} に出力しました。");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainViewModel] BIN出力に失敗: {ex}");
                _notificationService.Error("BINの出力に失敗しました。");
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

        /// <summary>
        /// サービスへの購読とタイマーを解放する
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _clockService.OnTick -= OnClockTick;
            _notificationService.OnNotification -= OnNotificationReceived;
            if (_serverSyncService != null)
            {
                _serverSyncService.OnStatusChanged -= OnSyncStatusChanged;
            }

            _notificationTimer?.Stop();
            _notificationTimer = null;

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
