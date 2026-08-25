using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Tenko.Lite.Services;
using Tenko.Lite.ViewModels;

namespace Tenko.Lite
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly NotificationService _notificationService;

        public MainWindow(MainViewModel viewModel, NotificationService notificationService)
        {
            InitializeComponent();

            _viewModel = viewModel;
            _notificationService = notificationService;
            DataContext = _viewModel;

            // イベント購読
            _notificationService.OnNotification += OnNotificationReceived;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _notificationService.OnNotification -= OnNotificationReceived;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            Loaded -= MainWindow_Loaded;
            Closed -= MainWindow_Closed;
        }

        private void OnNotificationReceived(object? sender, NotificationEventArgs e)
        {
            // 警告・エラー通知時の赤フラッシュアニメーション
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
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            TryFocusManualInput();
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MainViewModel.ShowInitialLocationModal)
                                or nameof(MainViewModel.ShowSettingsModal)
                                or nameof(MainViewModel.ShowBinWarning)
                                or nameof(MainViewModel.ShowCompleteModal))
            {
                Dispatcher.Invoke(TryFocusManualInput);
            }
        }

        /// <summary>
        /// いずれのモーダルも開いていない場合に入力欄へフォーカスを移す
        /// </summary>
        private void TryFocusManualInput()
        {
            bool isAnyModalOpen = _viewModel.ShowInitialLocationModal ||
                                  _viewModel.ShowSettingsModal ||
                                  _viewModel.ShowBinWarning ||
                                  _viewModel.ShowCompleteModal;

            if (!isAnyModalOpen && ManualInputBox.IsEnabled)
            {
                ManualInputBox.Focus();
            }
        }

        // Enter キーでスキャン処理を実行し、入力欄へ再フォーカス
        private void ManualInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _viewModel.SubmitCommand.Execute(null);
                ManualInputBox.Focus();
                ManualInputBox.SelectAll();
            }
        }
    }
}
