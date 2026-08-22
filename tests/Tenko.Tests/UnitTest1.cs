using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using Tenko.Native.Common;
using Tenko.Native.Infrastructure;
using Tenko.Native.Models;
using Tenko.Native.Services;
using Tenko.Native.ViewModels;
using Xunit;

namespace Tenko.Tests;

public class MockDialogService : IDialogService
{
    public bool ReturnValue { get; set; } = true;
    public bool Confirm(string message, string title, MessageBoxImage icon = MessageBoxImage.Question) => ReturnValue;
    public void ShowMessage(string message, string title = "情報", MessageBoxImage icon = MessageBoxImage.Information) { }
}

public class MockClockService : IClockService
{
    public DateTime Now { get; set; } = new DateTime(2026, 8, 22, 10, 0, 0);
    public event Action<DateTime>? OnTick;
    public void TriggerTick(DateTime time) => OnTick?.Invoke(time);
    public void Dispose() { }
}

public class TenkoTests : IDisposable
{
    private readonly string _testDir;

    public TenkoTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "TenkoTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        Directory.CreateDirectory(Path.Combine(_testDir, "data"));
        Directory.CreateDirectory(Path.Combine(_testDir, "scans"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    public void JsonHelper_SerializeAndDeserialize_WorksCorrectly()
    {
        var record = new ScanRecord
        {
            Id = "test_1",
            Timestamp = new DateTime(2026, 8, 21, 12, 0, 0),
            Barcode = "21021",
            Last5 = 21021,
            StudentName = "太郎 花子",
            StudentCode = "4D23",
            Location = "2棟2階"
        };

        string json = JsonHelper.Serialize(record);
        Assert.Contains("太郎 花子", json); // 日本語がエスケープされずに保持されているか

        var deserialized = JsonHelper.Deserialize<ScanRecord>(json);
        Assert.NotNull(deserialized);
        Assert.Equal(record.Id, deserialized.Id);
        Assert.Equal(record.Last5, deserialized.Last5);
        Assert.Equal(record.StudentName, deserialized.StudentName);
        Assert.Equal(record.Location, deserialized.Location);
    }

    [Fact]
    public void ScanFileService_AppendAndRemoveLast5_WorksCorrectly()
    {
        var storage = new StorageService();
        var service = new ScanFileService(storage);

        string testLocation = "TestLoc_" + Guid.NewGuid().ToString("N")[..6];
        string filePath = storage.GetScanPath($"ids_{testLocation}.bin");

        try
        {
            // 追記テスト (2バイト Little Endian)
            service.AppendLast5(testLocation, 12345);
            service.AppendLast5(testLocation, 23456);

            Assert.True(service.Exists(testLocation));

            byte[] bytes = File.ReadAllBytes(filePath);
            Assert.Equal(4, bytes.Length);
            Assert.Equal(12345, BitConverter.ToUInt16(bytes, 0));
            Assert.Equal(23456, BitConverter.ToUInt16(bytes, 2));

            // 削除テスト (最後の該当要素を削除)
            service.RemoveLast5(testLocation, 12345);
            bytes = File.ReadAllBytes(filePath);
            Assert.Equal(2, bytes.Length);
            Assert.Equal(23456, BitConverter.ToUInt16(bytes, 0));

            // リネームテスト
            service.RenameBin(testLocation, "bak");
            string renamedPath = storage.GetScanPath($"ids_{testLocation}_bak.bin");
            Assert.True(File.Exists(renamedPath));
            Assert.False(File.Exists(filePath));

            // クリーンアップ
            if (File.Exists(renamedPath)) File.Delete(renamedPath);
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }

    [Fact]
    public void StudentService_DecryptionAndLookup_Works()
    {
        var storage = new StorageService();
        var studentService = new StudentService(storage);

        // data/students.csv にあるテストデータ: 21021 -> 太郎 花子 (4D23)
        var (name, code) = studentService.GetStudentInfo(21021);
        Assert.Equal("太郎 花子", name);
        Assert.Equal("4D23", code);

        // 未知の学生番号
        var (unknownName, unknownCode) = studentService.GetStudentInfo(99999 % 65536);
        Assert.Equal(string.Empty, unknownName);
        Assert.Equal(string.Empty, unknownCode);
    }

    [Fact]
    public void ScanProcessor_ValidationAndProcessing_Works()
    {
        var storage = new StorageService();
        var historyService = new HistoryService(storage);
        var scanFileService = new ScanFileService(storage);
        var studentService = new StudentService(storage);

        var processor = new ScanProcessor(historyService, scanFileService, studentService);
        var history = processor.LoadHistory();
        string testLocation = "ProcTest_" + Guid.NewGuid().ToString("N")[..6];

        try
        {
            // ロケーション未指定
            var res1 = processor.ProcessScan("21021", "", history);
            Assert.Equal(ScanResultStatus.LocationNotSet, res1.Status);

            // 数字以外
            var res2 = processor.ProcessScan("ABC", testLocation, history);
            Assert.Equal(ScanResultStatus.ValidationError, res2.Status);

            // 桁数不正 (3桁)
            var res3 = processor.ProcessScan("123", testLocation, history);
            Assert.Equal(ScanResultStatus.ValidationError, res3.Status);

            // 正常スキャン (5桁)
            var res4 = processor.ProcessScan("21021", testLocation, history);
            Assert.Equal(ScanResultStatus.Success, res4.Status);
            Assert.NotNull(res4.Record);
            Assert.Equal("太郎 花子", res4.Record!.StudentName);
            Assert.Equal("4D23", res4.Record.StudentCode);
            Assert.Equal(21021, res4.Record.Last5);

            // デバウンス (3秒以内の同一学籍番号の再スキャン)
            var res5 = processor.ProcessScan("21021", testLocation, history);
            Assert.Equal(ScanResultStatus.IgnoredDebounce, res5.Status);

            // 10桁スキャン
            var res6 = processor.ProcessScan("0000023213", testLocation, history);
            Assert.Equal(ScanResultStatus.Success, res6.Status);
            Assert.NotNull(res6.Record);
            Assert.Equal(23213, res6.Record!.Last5);
            Assert.Equal("次郎 美咲", res6.Record.StudentName);
        }
        finally
        {
            processor.DeleteAllForLocation(testLocation, history);
        }
    }

    [Fact]
    public void ExportService_ExportsCsvAndBin()
    {
        var exportService = new ExportService();
        string location = "ExpTest_" + Guid.NewGuid().ToString("N")[..6];
        var records = new[]
        {
            new ScanRecord { Id = "1", Timestamp = DateTime.Now, Barcode = "21021", Last5 = 21021, StudentName = "太郎", StudentCode = "4D23", Location = location },
            new ScanRecord { Id = "2", Timestamp = DateTime.Now, Barcode = "23213", Last5 = 23213, StudentName = "次郎", StudentCode = "4D24", Location = location }
        };

        string tempDir = Path.Combine(Path.GetTempPath(), "ExportTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string csvName = exportService.ExportCsv(location, records, tempDir);
            string csvPath = Path.Combine(tempDir, csvName);
            Assert.True(File.Exists(csvPath));
            string csvContent = File.ReadAllText(csvPath);
            Assert.Contains("Timestamp,ID", csvContent);
            Assert.Contains("21021", csvContent);
            Assert.Contains("23213", csvContent);

            string binName = exportService.ExportBin(location, records, tempDir);
            string binPath = Path.Combine(tempDir, binName);
            Assert.True(File.Exists(binPath));
            byte[] binBytes = File.ReadAllBytes(binPath);
            Assert.Equal(4, binBytes.Length);
            Assert.Equal(21021, BitConverter.ToUInt16(binBytes, 0));
            Assert.Equal(23213, BitConverter.ToUInt16(binBytes, 2));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void MainViewModel_Validation_HandlesEdgeCases()
    {
        var storage = new StorageService();
        var settingsService = new SettingsService(storage);
        var historyService = new HistoryService(storage);
        var scanFileService = new ScanFileService(storage);
        var studentService = new StudentService(storage);
        var notificationService = new NotificationService();
        var clockService = new MockClockService();
        var exportService = new ExportService();
        var dialogService = new MockDialogService { ReturnValue = true };

        var processor = new ScanProcessor(historyService, scanFileService, studentService);

        string lastNotification = string.Empty;
        NotificationType lastType = NotificationType.Success;
        notificationService.OnNotification += (s, e) =>
        {
            lastNotification = e.Message;
            lastType = e.Type;
        };

        var vm = new MainViewModel(
            processor,
            settingsService,
            notificationService,
            clockService,
            exportService,
            dialogService
        );

        // 時計表示の確認
        Assert.Equal("2026-08-22 10:00:00", vm.CurrentTimeString);
        clockService.TriggerTick(new DateTime(2026, 8, 22, 10, 0, 1));
        Assert.Equal("2026-08-22 10:00:01", vm.CurrentTimeString);

        // 初回起動時モーダルの確認（SettingsService の初期値に応じて設定）
        vm.CurrentLocation = string.Empty;
        vm.ShowInitialLocationModal = true;
        Assert.True(vm.ShowInitialLocationModal);

        // 未選択状態で開始しようとすると警告
        vm.StartSessionCommand.Execute(null);
        Assert.True(vm.ShowInitialLocationModal);

        // 場所が未選択の場合
        vm.ManualInput = "21021";
        vm.SubmitCommand.Execute(null);
        Assert.Equal(NotificationType.Warning, lastType);
        Assert.Contains("スキャン場所を選択してください", lastNotification);

        // 場所を設定して点呼開始
        vm.CurrentLocation = "テスト部屋";
        vm.StartSessionCommand.Execute(null);
        Assert.False(vm.ShowInitialLocationModal);
        Assert.Equal(NotificationType.Success, lastType);
        Assert.Contains("点呼を開始しました", lastNotification);

        // 数字以外
        vm.ManualInput = "ABCDE";
        vm.SubmitCommand.Execute(null);
        Assert.Equal(NotificationType.Error, lastType);
        Assert.Contains("数字のみ入力可能です", lastNotification);

        // 桁数不一致 (3桁)
        vm.ManualInput = "123";
        vm.SubmitCommand.Execute(null);
        Assert.Equal(NotificationType.Error, lastType);
        Assert.Contains("5桁または10桁", lastNotification);

        // 正常スキャン (5桁)
        vm.ManualInput = "21021";
        vm.SubmitCommand.Execute(null);
        Assert.Empty(vm.ManualInput);
        Assert.Contains(vm.History, r => r.Last5 == 21021 && r.StudentName == "太郎 花子");

        // 重複スキャン (デバウンス時間外をシミュレートするため別学籍番号を挟むか、重複チェック確認)
        // 21021 は既に history に存在
        // デバウンスを回避して重複警告の挙動を確認するために、直前のスキャン履歴に存在するが recentScanAt に載っていない学籍番号、あるいは別ロケーション等
        // ここでは 0000023213 を追加
        vm.ManualInput = "0000023213";
        vm.SubmitCommand.Execute(null);
        Assert.Contains(vm.History, r => r.Last5 == 23213 && r.StudentName == "次郎 美咲");

        // 検索フィルタリング
        vm.SearchText = "花子";
        Assert.True(vm.IsFiltered);
        Assert.Single(vm.History);
        Assert.Equal("太郎 花子", vm.History[0].StudentName);

        vm.ClearSearchCommand.Execute(null);
        Assert.False(vm.IsFiltered);
        Assert.Equal(2, vm.History.Count);

        // モーダルの開閉コマンドテスト
        vm.OpenSettingsCommand.Execute(null);
        Assert.True(vm.ShowSettingsModal);
        vm.CloseSettingsCommand.Execute(null);
        Assert.False(vm.ShowSettingsModal);

        vm.OpenCompleteModalCommand.Execute(null);
        Assert.True(vm.ShowCompleteModal);
        vm.CloseCompleteModalCommand.Execute(null);
        Assert.False(vm.ShowCompleteModal);

        // CSV出力テスト
        vm.ExportCsvCommand.Execute(null);
        Assert.Contains("出力しました", lastNotification);

        // BIN出力テスト
        vm.ExportBinCommand.Execute(null);
        Assert.Contains("出力しました", lastNotification);

        // 削除テスト
        var recordToDelete = vm.History[0];
        vm.DeleteRecordCommand.Execute(recordToDelete);
        Assert.Single(vm.History);
        Assert.Contains("削除しました", lastNotification);

        // 全履歴削除テスト
        vm.DeleteAllCommand.Execute(null);
        Assert.Empty(vm.History);
        Assert.Contains("履歴を削除しました", lastNotification);
    }

    [Fact]
    public void ScanFileService_HandlesCorruptedOrEmptyFileGracefully()
    {
        var storage = new StorageService();
        var service = new ScanFileService(storage);
        string loc = "CorruptTest_" + Guid.NewGuid().ToString("N")[..6];
        string path = storage.GetScanPath($"ids_{loc}.bin");

        try
        {
            // 奇数バイトのファイルを作成
            File.WriteAllBytes(path, new byte[] { 0x01, 0x02, 0x03 });
            // 削除を試みても例外にならず安全に無視されること
            service.RemoveLast5(loc, 100);
            Assert.True(File.Exists(path));

            // 存在しない場所の削除
            service.RemoveLast5("NonExistentLocation", 100);
            service.DeleteBin("NonExistentLocation");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SettingsService_LoadsAndSavesCorrectly()
    {
        var storage = new StorageService();
        var settings = new SettingsService(storage);

        Assert.NotEmpty(settings.Locations);
        string oldLoc = settings.Location;
        settings.Location = "仮設ブース";
        Assert.Equal("仮設ブース", settings.Location);

        // 再読み込み
        var settings2 = new SettingsService(storage);
        Assert.Equal("仮設ブース", settings2.Location);

        // 復元
        settings.Location = oldLoc;
    }

    [Fact]
    public void EmbeddedLocations_ReturnsConfiguredLocations()
    {
        var locations = Tenko.Native.Generated.EmbeddedLocations.GetLocations();
        Assert.NotNull(locations);
        Assert.NotEmpty(locations);
        Assert.Contains("2棟2階", locations);
        Assert.Contains("第一体育館前", locations);
        Assert.Contains("本部横", locations);
    }
}
