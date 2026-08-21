using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Microsoft.Win32;
using Tenko.Native.Services;
using Tenko.Native.Common;

namespace ScanViewer.ViewModels
{
    public class ScanItem : ViewModelBase
    {
        public int Index { get; set; }
        public ushort Last5 { get; set; }
        public string StudentName { get; set; } = string.Empty;
        public string StudentCode { get; set; } = string.Empty;
    }

    public class MainViewModel : ViewModelBase
    {
        private readonly StudentService _studentService;
        private string _selectedFilePath = string.Empty;
        private string _searchText = string.Empty;
        private List<ScanItem> _allItems = new();
        
        public ObservableCollection<ScanItem> Items { get; } = new();

        public MainViewModel()
        {
            // StorageService をカレントディレクトリ基準で初期化
            var storage = new StorageService();
            _studentService = new StudentService(storage);

            SelectFileCommand = new RelayCommand(_ => SelectFile());
            ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty);
        }

        public string SelectedFilePath
        {
            get => _selectedFilePath;
            set => SetProperty(ref _selectedFilePath, value);
        }

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

        public ICommand SelectFileCommand { get; }
        public ICommand ClearSearchCommand { get; }

        private void SelectFile()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Binary files (*.bin)|*.bin|All files (*.*)|*.*",
                Title = "スキャンデータファイルを選択"
            };

            if (dialog.ShowDialog() == true)
            {
                LoadFile(dialog.FileName);
            }
        }

        private void LoadFile(string path)
        {
            try
            {
                SelectedFilePath = path;
                byte[] data = File.ReadAllBytes(path);
                var items = new List<ScanItem>();

                for (int i = 0; i < data.Length; i += 2)
                {
                    if (i + 1 >= data.Length) break;
                    
                    ushort last5 = BitConverter.ToUInt16(data, i);
                    var (name, code) = _studentService.GetStudentInfo(last5);
                    
                    items.Add(new ScanItem
                    {
                        Index = (i / 2) + 1,
                        Last5 = last5,
                        StudentName = name,
                        StudentCode = code
                    });
                }

                _allItems = items;
                ApplyFilter();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"ファイルの読み込みに失敗しました: {ex.Message}");
            }
        }

        private void ApplyFilter()
        {
            Items.Clear();
            IEnumerable<ScanItem> query = _allItems;

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                string lowerSearch = SearchText.ToLower();
                query = query.Where(i => 
                    i.StudentName.ToLower().Contains(lowerSearch) ||
                    i.StudentCode.ToLower().Contains(lowerSearch) ||
                    i.Last5.ToString("D5").Contains(lowerSearch));
            }

            foreach (var item in query)
            {
                Items.Add(item);
            }
        }
    }
}
