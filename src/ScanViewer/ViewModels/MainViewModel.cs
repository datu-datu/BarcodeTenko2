using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Tenko.Native.Common;
using Tenko.Native.Services;

namespace ScanViewer.ViewModels
{
    public class ScanItem : ViewModelBase
    {
        public int Index { get; set; }
        public ushort Last5 { get; set; }
        public string FormattedLast5 => Last5.ToString("D5");
        public string StudentName { get; set; } = string.Empty;
        public string StudentCode { get; set; } = string.Empty;
        public int ScanCount { get; set; } = 1;
        public bool IsDuplicate => ScanCount > 1;
        public bool IsUnregistered => string.IsNullOrEmpty(StudentName) && string.IsNullOrEmpty(StudentCode);

        public string StatusBadgeText
        {
            get
            {
                if (IsUnregistered) return "未登録";
                if (IsDuplicate) return $"重複 ({ScanCount}回)";
                return "正常";
            }
        }
    }

    public class MainViewModel : ViewModelBase
    {
        private readonly StudentService _studentService;
        private string _selectedFilePath = string.Empty;
        private string _fileName = "ファイル未選択";
        private string _fileSizeText = "-";
        private string _lastModifiedText = "-";
        private string _statusMessage = "準備完了";
        private string _searchText = string.Empty;
        private bool _filterDuplicatesOnly;
        private bool _filterUnregisteredOnly;
        private int _selectedViewIndex = 0; // 0: 詳細テーブル, 1: 簡易タイル

        private int _totalCount;
        private int _uniqueCount;
        private int _duplicateCount;
        private int _unregisteredCount;
        private int _filteredCount;

        private List<ScanItem> _allItems = new();

        public ObservableCollection<ScanItem> DisplayItems { get; } = new();

        // 既存の Items プロパティも後方互換用として DisplayItems を参照
        public ObservableCollection<ScanItem> Items => DisplayItems;

        public MainViewModel() : this(null)
        {
        }

        public MainViewModel(StudentService? studentService)
        {
            if (studentService != null)
            {
                _studentService = studentService;
            }
            else
            {
                var storage = new StorageService();
                _studentService = new StudentService(storage);
            }

            OpenFileCommand = new RelayCommand(_ => SelectFile());
            SelectFileCommand = OpenFileCommand; // 後方互換
            ReloadFileCommand = new RelayCommand(_ => ReloadFile(), _ => IsFileLoaded);
            ExportCsvCommand = new RelayCommand(_ => ExportCsv(), _ => IsFileLoaded && _allItems.Count > 0);
            CopyAllCommand = new RelayCommand(_ => CopyAllToClipboard(), _ => _allItems.Count > 0);
            ClearSearchCommand = new RelayCommand(_ => ClearSearch());
            OpenContainingFolderCommand = new RelayCommand(_ => OpenContainingFolder(), _ => IsFileLoaded);
        }

        #region Properties

        public string SelectedFilePath
        {
            get => _selectedFilePath;
            set
            {
                if (SetProperty(ref _selectedFilePath, value))
                {
                    OnPropertyChanged(nameof(IsFileLoaded));
                }
            }
        }

        public string FileName
        {
            get => _fileName;
            set => SetProperty(ref _fileName, value);
        }

        public string FileSizeText
        {
            get => _fileSizeText;
            set => SetProperty(ref _fileSizeText, value);
        }

        public string LastModifiedText
        {
            get => _lastModifiedText;
            set => SetProperty(ref _lastModifiedText, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public bool IsFileLoaded => !string.IsNullOrEmpty(SelectedFilePath) && File.Exists(SelectedFilePath);

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                {
                    ApplyFilter();
                }
            }
        }

        public bool FilterDuplicatesOnly
        {
            get => _filterDuplicatesOnly;
            set
            {
                if (SetProperty(ref _filterDuplicatesOnly, value))
                {
                    ApplyFilter();
                }
            }
        }

        public bool FilterUnregisteredOnly
        {
            get => _filterUnregisteredOnly;
            set
            {
                if (SetProperty(ref _filterUnregisteredOnly, value))
                {
                    ApplyFilter();
                }
            }
        }

        public int SelectedViewIndex
        {
            get => _selectedViewIndex;
            set => SetProperty(ref _selectedViewIndex, value);
        }

        public int TotalCount
        {
            get => _totalCount;
            private set => SetProperty(ref _totalCount, value);
        }

        public int UniqueCount
        {
            get => _uniqueCount;
            private set => SetProperty(ref _uniqueCount, value);
        }

        public int UnregisteredCount
        {
            get => _unregisteredCount;
            private set
            {
                if (SetProperty(ref _unregisteredCount, value))
                {
                    OnPropertyChanged(nameof(HasUnregistered));
                }
            }
        }

        public int DuplicateCount
        {
            get => _duplicateCount;
            private set
            {
                if (SetProperty(ref _duplicateCount, value))
                {
                    OnPropertyChanged(nameof(HasDuplicates));
                }
            }
        }

        public bool HasDuplicates => DuplicateCount > 0;
        public bool HasUnregistered => UnregisteredCount > 0;

        public int FilteredCount
        {
            get => _filteredCount;
            private set => SetProperty(ref _filteredCount, value);
        }

        public bool HasData => _allItems.Count > 0;

        #endregion

        #region Commands

        public ICommand OpenFileCommand { get; }
        public ICommand SelectFileCommand { get; }
        public ICommand ReloadFileCommand { get; }
        public ICommand ExportCsvCommand { get; }
        public ICommand CopyAllCommand { get; }
        public ICommand ClearSearchCommand { get; }
        public ICommand OpenContainingFolderCommand { get; }

        #endregion

        #region Operations

        public void SelectFile()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "スキャンデータファイル (*.bin)|*.bin|すべてのファイル (*.*)|*.*",
                Title = "点呼スキャンデータファイル (.bin) を選択"
            };

            if (dialog.ShowDialog() == true)
            {
                LoadFile(dialog.FileName);
            }
        }

        public void ReloadFile()
        {
            if (!string.IsNullOrEmpty(SelectedFilePath) && File.Exists(SelectedFilePath))
            {
                LoadFile(SelectedFilePath);
            }
        }

        public void HandleFileDrop(string[] filePaths)
        {
            if (filePaths == null || filePaths.Length == 0) return;

            // 最初に見つかった .bin または最初のファイルを採用
            string? target = filePaths.FirstOrDefault(p => p.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                             ?? filePaths.FirstOrDefault();

            if (!string.IsNullOrEmpty(target) && File.Exists(target))
            {
                LoadFile(target);
            }
        }

        public void LoadFile(string path)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                var fi = new FileInfo(path);

                SelectedFilePath = path;
                FileName = fi.Name;
                FileSizeText = FormatFileSize(data.Length);
                LastModifiedText = fi.LastWriteTime.ToString("yyyy/MM/dd HH:mm:ss");

                LoadFromBytes(data);
                StatusMessage = $"読み込み完了: {fi.Name} ({TotalCount}件のスキャン)";
            }
            catch (Exception ex)
            {
                StatusMessage = $"読み込みエラー: {ex.Message}";
                MessageBox.Show($"ファイルの読み込みに失敗しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void LoadFromBytes(byte[] data)
        {
            var rawList = new List<ushort>();
            for (int i = 0; i < data.Length; i += 2)
            {
                if (i + 1 >= data.Length) break;
                ushort last5 = BitConverter.ToUInt16(data, i);
                rawList.Add(last5);
            }

            // 同一IDの出現回数を集計
            var frequencyMap = rawList.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());

            var items = new List<ScanItem>();
            for (int i = 0; i < rawList.Count; i++)
            {
                ushort last5 = rawList[i];
                var (name, code) = _studentService.GetStudentInfo(last5);
                int count = frequencyMap.TryGetValue(last5, out int c) ? c : 1;

                items.Add(new ScanItem
                {
                    Index = i + 1,
                    Last5 = last5,
                    StudentName = name,
                    StudentCode = code,
                    ScanCount = count
                });
            }

            _allItems = items;

            // KPI集計
            TotalCount = items.Count;
            UniqueCount = frequencyMap.Count;
            DuplicateCount = items.Count(x => x.IsDuplicate);
            UnregisteredCount = items.Count(x => x.IsUnregistered);
            OnPropertyChanged(nameof(HasData));

            ApplyFilter();
        }

        public void ApplyFilter()
        {
            DisplayItems.Clear();
            IEnumerable<ScanItem> query = _allItems;

            if (FilterDuplicatesOnly)
            {
                query = query.Where(i => i.IsDuplicate);
            }

            if (FilterUnregisteredOnly)
            {
                query = query.Where(i => i.IsUnregistered);
            }

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                string search = SearchText.Trim().ToLower();
                query = query.Where(i =>
                    i.StudentName.ToLower().Contains(search) ||
                    i.StudentCode.ToLower().Contains(search) ||
                    i.FormattedLast5.Contains(search) ||
                    i.Index.ToString().Contains(search));
            }

            foreach (var item in query)
            {
                DisplayItems.Add(item);
            }

            FilteredCount = DisplayItems.Count;
        }

        public void ClearSearch()
        {
            SearchText = string.Empty;
            FilterDuplicatesOnly = false;
            FilterUnregisteredOnly = false;
        }

        public void ExportCsv()
        {
            if (_allItems.Count == 0) return;

            var defaultName = Path.GetFileNameWithoutExtension(SelectedFilePath) + "_export.csv";
            var dialog = new SaveFileDialog
            {
                Filter = "CSVファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
                FileName = defaultName,
                Title = "スキャン結果をCSVにエクスポート"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    string csvContent = GenerateCsvContent();
                    // Excel で日本語が文字化けしないよう BOM 付き UTF-8 で出力
                    File.WriteAllText(dialog.FileName, csvContent, new UTF8Encoding(true));
                    StatusMessage = $"CSV出力完了: {Path.GetFileName(dialog.FileName)}";
                    MessageBox.Show("CSVファイルを正常に出力しました。", "エクスポート完了", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    StatusMessage = $"CSV出力失敗: {ex.Message}";
                    MessageBox.Show($"CSVファイルの保存に失敗しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        public string GenerateCsvContent()
        {
            var sb = new StringBuilder();
            sb.AppendLine("順序,学籍番号,氏名,コード,スキャン回数,状態");
            foreach (var item in _allItems)
            {
                string name = EscapeCsvField(item.StudentName);
                string code = EscapeCsvField(item.StudentCode);
                sb.AppendLine($"{item.Index},{item.FormattedLast5},{name},{code},{item.ScanCount},{item.StatusBadgeText}");
            }
            return sb.ToString();
        }

        public void CopyAllToClipboard()
        {
            if (_allItems.Count == 0) return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("No\t学籍番号\t氏名\tコード\t回数\t状態");
                // 現在フィルタ表示中のリスト、または全件
                IEnumerable<ScanItem> targetItems = DisplayItems.Count > 0 ? (IEnumerable<ScanItem>)DisplayItems : _allItems;
                int count = 0;
                foreach (var item in targetItems)
                {
                    sb.AppendLine($"{item.Index}\t{item.FormattedLast5}\t{item.StudentName}\t{item.StudentCode}\t{item.ScanCount}\t{item.StatusBadgeText}");
                    count++;
                }
                Clipboard.SetText(sb.ToString());
                StatusMessage = $"クリップボードに {count} 件コピーしました";
            }
            catch (Exception ex)
            {
                StatusMessage = $"コピー失敗: {ex.Message}";
            }
        }

        public void OpenContainingFolder()
        {
            if (string.IsNullOrEmpty(SelectedFilePath) || !File.Exists(SelectedFilePath)) return;

            try
            {
                string argument = $"/select, \"{SelectedFilePath}\"";
                Process.Start("explorer.exe", argument);
            }
            catch (Exception ex)
            {
                StatusMessage = $"フォルダ起動失敗: {ex.Message}";
            }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }

        private static string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field)) return "\"\"";
            if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            {
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }
            return $"\"{field}\"";
        }

        #endregion
    }
}
