using System.Windows;
using System.Windows.Input;
using ScanViewer.ViewModels;

namespace ScanViewer;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0 && DataContext is MainViewModel vm)
            {
                vm.HandleFileDrop(files);
            }
        }
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Scan Data Viewer (点呼データ閲覧ツール) v2.0\n\n" +
            "点呼バイナリデータ (.bin) の解析・確認ツールです。\n" +
            "・重複スキャンの自動ハイライト＆件数集計\n" +
            "・名簿マスター (students.enc) 未登録コードの検出\n" +
            "・ドラッグ＆ドロップでのファイル読み込み\n" +
            "・CSVエクスポート (BOM付UTF-8) & TSVコピー\n\n" +
            "BarcodeTenko2 ユーティリティ",
            "バージョン情報",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}