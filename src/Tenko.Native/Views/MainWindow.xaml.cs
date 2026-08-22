using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Tenko.Native.Services;
using Tenko.Native.ViewModels;

namespace Tenko.Native
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;

        public MainWindow(MainViewModel viewModel, NotificationService notificationService)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            // 警告・エラー通知時の赤フラッシュアニメーション
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

            // 起動直後に入力欄へフォーカスを移す
            Loaded += (s, e) => ManualInputBox.Focus();
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
