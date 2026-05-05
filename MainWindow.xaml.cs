using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
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

        public MainWindow()
        {
            InitializeComponent();

            var settingsService = new SettingsService();
            var historyService = new HistoryService();
            var scanFileService = new ScanFileService();
            var notificationService = new NotificationService();
            var studentService = new StudentService();

            _viewModel = new MainViewModel(
                settingsService,
                historyService,
                scanFileService,
                notificationService,
                studentService
            );

            this.DataContext = _viewModel;

            // 初期フォーカス
            this.Loaded += (s, e) => ManualInputBox.Focus();

            // Set up live clock for bottom-left
            _clockTimer = new DispatcherTimer();
            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += ClockTimer_Tick;
            _clockTimer.Start();

            // Load initial deadline and watch for changes in data/time.json
            LoadDeadline();
            try
            {
                string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (Directory.Exists(dataDir))
                {
                    _deadlineWatcher = new FileSystemWatcher(dataDir, "time.json");
                    _deadlineWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
                    _deadlineWatcher.Changed += (s, e) => Dispatcher.Invoke(() => LoadDeadline());
                    _deadlineWatcher.Created += (s, e) => Dispatcher.Invoke(() => LoadDeadline());
                    _deadlineWatcher.Deleted += (s, e) => Dispatcher.Invoke(() => LoadDeadline());
                    _deadlineWatcher.Renamed += (s, e) => Dispatcher.Invoke(() => LoadDeadline());
                    _deadlineWatcher.EnableRaisingEvents = true;
                }
            }
            catch
            {
                // ignore watcher errors
            }
        }

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

        private void ClockTimer_Tick(object? sender, EventArgs? e)
        {
            CurrentTimeText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // If we have a deadline, update its display if needed (e.g., show overdue styling in future)
            if (_nearestDeadline != null)
            {
                // keep text updated in case we want to change formatting based on proximity in future
                DeadlineText.Text = "締切: " + _nearestDeadline.Value.ToString("yyyy-MM-dd HH:mm");
                DeadlineText.Visibility = Visibility.Visible;
            }
        }

        private void LoadDeadline()
        {
            try
            {
                string dataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "time.json");
                if (!File.Exists(dataPath))
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
                            // ensure local kind for consistent comparison
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

                // remove duplicates and sort ascending to be deterministic
                parsed = parsed.Distinct().OrderBy(d => d.Ticks).ToList();

                if (parsed.Count == 0)
                {
                    DeadlineText.Visibility = Visibility.Collapsed;
                    _nearestDeadline = null;
                    return;
                }

                var now = DateTime.Now;
                // choose the soonest date strictly after now
                var upcoming = parsed.Where(d => d > now).OrderBy(d => d.Ticks).FirstOrDefault();
                if (upcoming != default(DateTime))
                {
                    _nearestDeadline = upcoming;
                    DeadlineText.Text = "締切: " + _nearestDeadline.Value.ToString("yyyy-MM-dd HH:mm");
                    DeadlineText.Visibility = Visibility.Visible;
                }
                else
                {
                    // no upcoming deadlines; hide display
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
        private void ManualInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _viewModel.SubmitCommand.Execute(null);
                ManualInputBox.Focus();
                ManualInputBox.SelectAll();
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsModal.Visibility = Visibility.Visible;
        }

        private void CloseSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsModal.Visibility = Visibility.Collapsed;
            ManualInputBox.Focus();
        }

        private void RenameButton_Click(object sender, RoutedEventArgs e)
        {
            string newName = RenameTextBox.Text;
            if (!string.IsNullOrWhiteSpace(newName))
            {
                _viewModel.RenameBinCommand.Execute(newName);
                ManualInputBox.Focus();
            }
        }

        private void DismissWarning_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ShowBinWarning = false;
            ManualInputBox.Focus();
        }

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

        private void CancelComplete_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ShowCompleteModal = false;
            ManualInputBox.Focus();
        }
    }
}
