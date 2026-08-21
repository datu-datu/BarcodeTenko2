using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Tenko.Native.Services;
using Tenko.Native.ViewModels;

namespace Tenko.Native
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private DispatcherTimer? _clockTimer;
        private DateTime? _nearestDeadline;
        private FileSystemWatcher? _deadlineWatcher;

        // 画面初期化とサービスの組み立て、各種イベントを登録する。
        public MainWindow()
        {
            InitializeComponent();

            var storage = new StorageService();
            var settingsService = new SettingsService(storage);
            var historyService = new HistoryService(storage);
            var scanFileService = new ScanFileService(storage);
            var notificationService = new NotificationService();
            var studentService = new StudentService(storage);

            _viewModel = new MainViewModel(
                settingsService,
                historyService,
                scanFileService,
                notificationService,
                studentService
            );

            this.DataContext = _viewModel;

            notificationService.OnNotification += (s, e) =>
            {
                if (e.Type == NotificationType.Warning || e.Type == NotificationType.Error)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (FindResource("FlashRedStoryboard") is Storyboard sb)
                        {
                            sb.Begin(ManualInputBox);
                        }
                    });
                }
            };

            // 起動直後に入力欄へフォーカスを移す。
            this.Loaded += (s, e) => ManualInputBox.Focus();

            // 画面左下の時計表示を定期更新する。
            _clockTimer = new DispatcherTimer();
            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += ClockTimer_Tick;
            _clockTimer.Start();

            // 締切の初期読み込みと data/time.json の更新監視を開始する。
            LoadDeadline(storage);
            try
            {
                string dataDir = storage.GetDataPath(string.Empty);
                if (Directory.Exists(dataDir))
                {
                    _deadlineWatcher = new FileSystemWatcher(dataDir, "time.json");
                    _deadlineWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
                    _deadlineWatcher.Changed += (s, e) => Dispatcher.Invoke(() => LoadDeadline(storage));
                    _deadlineWatcher.Created += (s, e) => Dispatcher.Invoke(() => LoadDeadline(storage));
                    _deadlineWatcher.Deleted += (s, e) => Dispatcher.Invoke(() => LoadDeadline(storage));
                    _deadlineWatcher.Renamed += (s, e) => Dispatcher.Invoke(() => LoadDeadline(storage));
                    _deadlineWatcher.EnableRaisingEvents = true;
                }
            }
            catch
            {
                // 監視設定に失敗してもアプリは継続する。
            }
        }

        // 画面終了時にタイマーや監視を確実に停止する。
        protected override void OnClosed(EventArgs e)
        {
            if (_clockTimer != null)
            {
                _clockTimer.Tick -= ClockTimer_Tick;
                _clockTimer.Stop();
                _clockTimer = null;
            }

            if (_deadlineWatcher != null)
            {
                try
                {
                    _deadlineWatcher.EnableRaisingEvents = false;
                    _deadlineWatcher.Dispose();
                }
                catch { }
                _deadlineWatcher = null;
            }

            base.OnClosed(e);
        }

        // 時計表示と締切表示を更新する。
        private void ClockTimer_Tick(object? sender, EventArgs? e)
        {
            CurrentTimeText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // 締切がある場合は表示文言を更新する（将来の表示変更にも対応）。
            if (_nearestDeadline != null)
            {
                // 近さによるフォーマット変更にも対応できるよう毎回更新する。
                DeadlineText.Text = "締切: " + _nearestDeadline.Value.ToString("yyyy-MM-dd HH:mm");
                DeadlineText.Visibility = Visibility.Visible;
            }
        }

        // time.json を読み込み、次の締切を決定して表示する。
        private void LoadDeadline(StorageService storage)
        {
            try
            {
                string dataPath = storage.GetDataPath("time.json");
                if (!storage.Exists(dataPath))
                {
                    DeadlineText.Visibility = Visibility.Collapsed;
                    _nearestDeadline = null;
                    return;
                }

                string content = File.ReadAllText(dataPath).Trim();
                var tokens = new List<string>();
                try
                {
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in root.EnumerateArray())
                            if (el.ValueKind == JsonValueKind.String)
                                tokens.Add(el.GetString() ?? string.Empty);
                    }
                    else if (root.ValueKind == JsonValueKind.String)
                    {
                        tokens.Add(root.GetString() ?? string.Empty);
                    }
                    else
                    {
                        tokens.AddRange(content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
                    }
                }
                catch (JsonException)
                {
                    tokens.AddRange(content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
                }

                var parsed = new List<DateTime>();
                string[] patterns = new[] { "yyyy-MM-dd-HH-mm", "yyyy-MM-dd-HH-mm-ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss" };
                foreach (var t in tokens)
                {
                    var s = t.Trim().Trim('"');
                    DateTime dt;
                    bool ok = false;
                    foreach (var p in patterns)
                    {
                        if (DateTime.TryParseExact(s, p, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out dt))
                        {
                            // 比較の一貫性のため Local として扱う。
                            if (dt.Kind == DateTimeKind.Unspecified) dt = DateTime.SpecifyKind(dt, DateTimeKind.Local);
                            parsed.Add(dt);
                            ok = true;
                            break;
                        }
                    }
                    if (!ok)
                    {
                        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out dt))
                        {
                            if (dt.Kind == DateTimeKind.Unspecified) dt = DateTime.SpecifyKind(dt, DateTimeKind.Local);
                            parsed.Add(dt);
                        }
                    }
                }

                // 重複を除去し、昇順で決定的に並べ替える。
                parsed = parsed.Distinct().OrderBy(d => d.Ticks).ToList();

                if (parsed.Count == 0)
                {
                    DeadlineText.Visibility = Visibility.Collapsed;
                    _nearestDeadline = null;
                    return;
                }

                var now = DateTime.Now;
                // 現在時刻より未来の中で最も近い締切を選ぶ。
                var upcoming = parsed.Where(d => d > now).OrderBy(d => d.Ticks).FirstOrDefault();
                if (upcoming != default(DateTime))
                {
                    _nearestDeadline = upcoming;
                    DeadlineText.Text = "締切: " + _nearestDeadline.Value.ToString("yyyy-MM-dd HH:mm");
                    DeadlineText.Visibility = Visibility.Visible;
                }
                else
                {
                    // 未来の締切がない場合は表示を隠す。
                    DeadlineText.Visibility = Visibility.Collapsed;
                    _nearestDeadline = null;
                }
            }
            catch
            {
                DeadlineText.Visibility = Visibility.Collapsed;
                _nearestDeadline = null;
            }
        }

        // Enter でスキャン処理を実行し、入力欄へ再フォーカスする。
        private void ManualInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _viewModel.SubmitCommand.Execute(null);
                ManualInputBox.Focus();
                ManualInputBox.SelectAll();
            }
        }

        // 設定モーダルを表示する。
        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsModal.Visibility = Visibility.Visible;
        }

        // 設定モーダルを閉じて入力欄へ戻す。
        private void CloseSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsModal.Visibility = Visibility.Collapsed;
            ManualInputBox.Focus();
        }

        // ファイル名変更を実行し、入力欄へ戻す。
        private void RenameButton_Click(object sender, RoutedEventArgs e)
        {
            string newName = RenameTextBox.Text;
            if (!string.IsNullOrWhiteSpace(newName))
            {
                _viewModel.RenameBinCommand.Execute(newName);
                ManualInputBox.Focus();
            }
        }

        // 警告表示を閉じる。
        private void DismissWarning_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ShowBinWarning = false;
            ManualInputBox.Focus();
        }

        // 完了モーダルのファイル名変更を確定する。
        private void CompleteRenameButton_Click(object sender, RoutedEventArgs e)
        {
            string newName = CompleteRenameTextBox.Text;
            if (!string.IsNullOrWhiteSpace(newName))
            {
                _viewModel.RenameBinCommand.Execute(newName);
                _viewModel.ShowCompleteModal = false;
                CompleteRenameTextBox.Text = string.Empty;
                ManualInputBox.Focus();
            }
        }

        // 完了モーダルを閉じ、入力欄へ戻す。
        private void CancelComplete_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ShowCompleteModal = false;
            ManualInputBox.Focus();
        }
    }
}
