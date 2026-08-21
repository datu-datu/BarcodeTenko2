using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Tenko.Native.Common;
using Tenko.Native.Infrastructure;
using Tenko.Native.Models;
using Tenko.Native.Services;
using Tenko.Native.ViewModels;
using Xunit;

namespace Tenko.Tests;

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
    public void MainViewModel_Validation_HandlesEdgeCases()
    {
        var storage = new StorageService();
        var settingsService = new SettingsService(storage);
        var historyService = new HistoryService(storage);
        var scanFileService = new ScanFileService(storage);
        var notificationService = new NotificationService();
        var studentService = new StudentService(storage);

        string lastNotification = string.Empty;
        NotificationType lastType = NotificationType.Success;
        notificationService.OnNotification += (s, e) =>
        {
            lastNotification = e.Message;
            lastType = e.Type;
        };

        var vm = new MainViewModel(
            settingsService,
            historyService,
            scanFileService,
            notificationService,
            studentService
        );

        // 場所が未選択の場合
        vm.CurrentLocation = string.Empty;
        vm.ManualInput = "21021";
        vm.SubmitCommand.Execute(null);
        Assert.Equal(NotificationType.Warning, lastType);
        Assert.Contains("スキャン場所を選択してください", lastNotification);

        // 場所を設定
        vm.CurrentLocation = "テスト部屋";

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

        // 重複スキャン
        vm.ManualInput = "21021";
        vm.SubmitCommand.Execute(null);
        Assert.Equal(NotificationType.Warning, lastType);
        Assert.Contains("既にスキャン済み", lastNotification);

        // 10桁スキャン（末尾5桁が抽出される）
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

        // CSV出力テスト
        vm.ExportCsvCommand.Execute(null);
        Assert.Contains("出力しました", lastNotification);

        // BIN出力テスト
        vm.ExportBinCommand.Execute(null);
        Assert.Contains("出力しました", lastNotification);

        // 削除テスト（ConfirmDialog をモックして自動承認）
        vm.ConfirmDialog = (msg, title, icon) => true;

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


