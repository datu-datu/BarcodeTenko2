using System;
using System.IO;
using System.Linq;
using ScanViewer.ViewModels;
using Tenko.Native.Services;
using Xunit;

namespace Tenko.Tests
{
    public class ScanViewerTests : IDisposable
    {
        private readonly string _testDir;
        private readonly StudentService _studentService;

        public ScanViewerTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "ScanViewerTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);

            var storage = new StorageService();
            _studentService = new StudentService(storage);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDir))
            {
                try { Directory.Delete(_testDir, true); } catch { }
            }
        }

        [Fact]
        public void LoadFromBytes_CorrectlyParsesBinaryAndCalculatesMetrics()
        {
            var vm = new MainViewModel(_studentService);

            // 21021: 既知の生徒 (太郎 花子) x 2回
            // 9999: 未知の生徒 x 1回
            ushort idKnown = 21021;
            ushort idUnknown = 9999;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(idKnown);
            bw.Write(idUnknown);
            bw.Write(idKnown); // 重複スキャン

            byte[] data = ms.ToArray();
            vm.LoadFromBytes(data);

            Assert.Equal(3, vm.TotalCount);
            Assert.Equal(2, vm.UniqueCount);
            Assert.Equal(2, vm.DuplicateCount); // idKnown が2回出現
            Assert.Equal(1, vm.UnregisteredCount); // idUnknown が1回出現
            Assert.True(vm.HasDuplicates);
            Assert.True(vm.HasUnregistered);
            Assert.Equal(3, vm.DisplayItems.Count);

            // Item 1: Known, Duplicate (2 times)
            var item1 = vm.DisplayItems[0];
            Assert.Equal(1, item1.Index);
            Assert.Equal(21021, item1.Last5);
            Assert.Equal("21021", item1.FormattedLast5);
            Assert.Equal("太郎 花子", item1.StudentName);
            Assert.Equal("4D23", item1.StudentCode);
            Assert.Equal(2, item1.ScanCount);
            Assert.True(item1.IsDuplicate);
            Assert.False(item1.IsUnregistered);
            Assert.Equal("重複 (2回)", item1.StatusBadgeText);

            // Item 2: Unknown, Not Duplicate
            var item2 = vm.DisplayItems[1];
            Assert.Equal(2, item2.Index);
            Assert.Equal(9999, item2.Last5);
            Assert.Equal("09999", item2.FormattedLast5);
            Assert.True(item2.IsUnregistered);
            Assert.False(item2.IsDuplicate);
            Assert.Equal("未登録", item2.StatusBadgeText);
        }

        [Fact]
        public void Filter_DuplicatesOnly_Works()
        {
            var vm = new MainViewModel(_studentService);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((ushort)21021);
            bw.Write((ushort)9999);
            bw.Write((ushort)21021);

            vm.LoadFromBytes(ms.ToArray());

            Assert.Equal(3, vm.DisplayItems.Count);

            vm.FilterDuplicatesOnly = true;
            Assert.Equal(2, vm.DisplayItems.Count);
            Assert.All(vm.DisplayItems, item => Assert.True(item.IsDuplicate));

            vm.FilterDuplicatesOnly = false;
            Assert.Equal(3, vm.DisplayItems.Count);
        }

        [Fact]
        public void Filter_UnregisteredOnly_Works()
        {
            var vm = new MainViewModel(_studentService);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((ushort)21021);
            bw.Write((ushort)9999);
            bw.Write((ushort)21021);

            vm.LoadFromBytes(ms.ToArray());

            vm.FilterUnregisteredOnly = true;
            Assert.Single(vm.DisplayItems);
            Assert.True(vm.DisplayItems[0].IsUnregistered);
            Assert.Equal(9999, vm.DisplayItems[0].Last5);
        }

        [Fact]
        public void SearchText_FiltersCorrectly()
        {
            var vm = new MainViewModel(_studentService);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((ushort)21021); // 太郎 花子
            bw.Write((ushort)9999);

            vm.LoadFromBytes(ms.ToArray());

            // 氏名で検索
            vm.SearchText = "太郎";
            Assert.Single(vm.DisplayItems);
            Assert.Equal("太郎 花子", vm.DisplayItems[0].StudentName);

            // 学籍番号で検索
            vm.SearchText = "09999";
            Assert.Single(vm.DisplayItems);
            Assert.Equal(9999, vm.DisplayItems[0].Last5);

            // クリア
            vm.ClearSearch();
            Assert.Equal(2, vm.DisplayItems.Count);
        }

        [Fact]
        public void GenerateCsvContent_ProducesValidCsv()
        {
            var vm = new MainViewModel(_studentService);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((ushort)21021);
            bw.Write((ushort)21021);

            vm.LoadFromBytes(ms.ToArray());
            string csv = vm.GenerateCsvContent();

            Assert.Contains("順序,学籍番号,氏名,コード,スキャン回数,状態", csv);
            Assert.Contains("1,21021,\"太郎 花子\",\"4D23\",2,重複 (2回)", csv);
            Assert.Contains("2,21021,\"太郎 花子\",\"4D23\",2,重複 (2回)", csv);
        }

        [Fact]
        public void HandleFileDrop_LoadsBinFile()
        {
            var vm = new MainViewModel(_studentService);
            string testBinPath = Path.Combine(_testDir, "ids_test.bin");

            using (var bw = new BinaryWriter(File.OpenWrite(testBinPath)))
            {
                bw.Write((ushort)21021);
            }

            vm.HandleFileDrop(new[] { testBinPath });

            Assert.True(vm.IsFileLoaded);
            Assert.Equal("ids_test.bin", vm.FileName);
            Assert.Equal(1, vm.TotalCount);
            Assert.Single(vm.DisplayItems);
        }
    }
}
