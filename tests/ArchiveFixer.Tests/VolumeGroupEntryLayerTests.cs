using System;
using System.IO;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 第二大步「收片」两个执行体（2026-10-23 重建）的行为守门：
    /// 程序产出的片搬进入口层、**用户导入的原件只建硬链接不搬**、目标名被占不覆盖；
    /// 成功后删入口层里的片、用户原件不删。
    /// </summary>
    public class VolumeGroupEntryLayerTests : IDisposable
    {
        private readonly string _root;

        public VolumeGroupEntryLayerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af-entry-layer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        [Fact]
        public void 程序产出的片_同卷搬进入口层_源不在了()
        {
            string pieces = Directory.CreateDirectory(Path.Combine(_root, "pieces")).FullName;
            string entry = Directory.CreateDirectory(Path.Combine(_root, "entry")).FullName;

            string piece = Path.Combine(pieces, "set.part2.rar");
            File.WriteAllBytes(piece, new byte[] { 1, 2, 3 });

            VolumeGroupEntryLayer.MoveResult result =
                VolumeGroupEntryLayer.MovePiecesIntoEntryLayer(new[] { piece }, entry, _ => false);

            Assert.Equal(1, result.Moved);
            Assert.False(File.Exists(piece), "程序产出的片应当被搬走");
            Assert.True(File.Exists(Path.Combine(entry, "set.part2.rar")));
        }

        [Fact]
        public void 用户导入的原件_只建硬链接_原件还在且是同一份字节()
        {
            string pieces = Directory.CreateDirectory(Path.Combine(_root, "user")).FullName;
            string entry = Directory.CreateDirectory(Path.Combine(_root, "entry2")).FullName;

            string userPiece = Path.Combine(pieces, "set.part3.rar");
            File.WriteAllBytes(userPiece, new byte[] { 9, 9, 9 });

            VolumeGroupEntryLayer.MoveResult result =
                VolumeGroupEntryLayer.MovePiecesIntoEntryLayer(new[] { userPiece }, entry, _ => true);

            Assert.Equal(1, result.Linked);
            Assert.True(File.Exists(userPiece), "用户导入的原件一个字节都不许搬走");

            string linked = Path.Combine(entry, "set.part3.rar");
            Assert.True(File.Exists(linked), "入口层里应当有那一份硬链接");

            Assert.True(FileIdentity.TryGetIdentity(userPiece, out ulong v1, out ulong i1));
            Assert.True(FileIdentity.TryGetIdentity(linked, out ulong v2, out ulong i2));
            Assert.Equal(v1, v2);
            Assert.Equal(i1, i2);
        }

        [Fact]
        public void 目标名被占_绝不覆盖()
        {
            string pieces = Directory.CreateDirectory(Path.Combine(_root, "p3")).FullName;
            string entry = Directory.CreateDirectory(Path.Combine(_root, "entry3")).FullName;

            string piece = Path.Combine(pieces, "set.part4.rar");
            File.WriteAllBytes(piece, new byte[] { 5, 5 });

            string occupied = Path.Combine(entry, "set.part4.rar");
            File.WriteAllBytes(occupied, new byte[] { 7, 7, 7 });

            VolumeGroupEntryLayer.MoveResult result =
                VolumeGroupEntryLayer.MovePiecesIntoEntryLayer(new[] { piece }, entry, _ => false);

            Assert.Equal(1, result.Skipped);
            Assert.True(File.Exists(piece), "被占时源不动");
            Assert.Equal(new byte[] { 7, 7, 7 }, File.ReadAllBytes(occupied));
        }

        [Fact]
        public void 成功之后_入口层里的片按过程物删掉_用户原件不删()
        {
            string entry = Directory.CreateDirectory(Path.Combine(_root, "entry4")).FullName;

            string ourPiece = Path.Combine(entry, "set.part2.rar");
            File.WriteAllBytes(ourPiece, new byte[] { 1 });

            string userPiece = Path.Combine(entry, "set.part3.rar");
            File.WriteAllBytes(userPiece, new byte[] { 2 });

            int deleted = VolumeGroupEntryLayer.PurgeSolvedGroupPiecesInEntryLayer(
                new[] { ourPiece, userPiece },
                entry,
                path => string.Equals(Path.GetFileName(path), "set.part3.rar", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(ourPiece), "程序产出的片在成功后应当删掉");
            Assert.True(File.Exists(userPiece), "用户导入的原件不删");
        }
        [Fact]
        public void 成功且校验通过_入口层里的组片按过程物删掉()
        {
            string entry = Directory.CreateDirectory(Path.Combine(_root, "entry5")).FullName;
            string piece = Path.Combine(entry, "set.part5.rar");
            File.WriteAllBytes(piece, new byte[] { 3 });

            int deleted = VolumeStepExecutor.ApplySecondStepGroupPieceHandling(
                new[] { piece }, entry, solvedAndVerified: true, _ => false);

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(piece));
        }

        [Fact]
        public void 失败或未校验_入口层里的组片一片都不删()
        {
            string entry = Directory.CreateDirectory(Path.Combine(_root, "entry6")).FullName;
            string piece = Path.Combine(entry, "set.part6.rar");
            File.WriteAllBytes(piece, new byte[] { 4 });

            int deleted = VolumeStepExecutor.ApplySecondStepGroupPieceHandling(
                new[] { piece }, entry, solvedAndVerified: false, _ => false);

            Assert.Equal(0, deleted);
            Assert.True(File.Exists(piece), "没解开时片要留在入口包的位置");
        }

        [Fact]
        public void 合成组去重键_源层加基名_同层同基名相等_不同层或不同基名不等()
        {
            string a = Path.Combine(_root, "layer1");
            string b = Path.Combine(_root, "layer2");

            Assert.Equal(
                VolumeStepExecutor.BuildSyntheticGroupEntryKey(a, "111"),
                VolumeStepExecutor.BuildSyntheticGroupEntryKey(a, "111"));

            Assert.Equal(
                VolumeStepExecutor.BuildSyntheticGroupEntryKey(@"C:\x\layer1", "111"),
                VolumeStepExecutor.BuildSyntheticGroupEntryKey(@"C:\x\layer1", "111"),
                ignoreCase: true);

            Assert.NotEqual(
                VolumeStepExecutor.BuildSyntheticGroupEntryKey(a, "111"),
                VolumeStepExecutor.BuildSyntheticGroupEntryKey(b, "111"));

            Assert.NotEqual(
                VolumeStepExecutor.BuildSyntheticGroupEntryKey(a, "111"),
                VolumeStepExecutor.BuildSyntheticGroupEntryKey(a, "222"));
        }

        [Fact]
        public async System.Threading.Tasks.Task 建组出口_少于两片或格式判不出_不建组也不动盘()
        {
            string dir = Directory.CreateDirectory(Path.Combine(_root, "pieces7")).FullName;
            string one = Path.Combine(dir, "a.bin");
            File.WriteAllBytes(one, new byte[] { 1, 2 });

            // 引擎为空 ⇒ 直接不建组（试开没有引擎就是判不出）。
            Assert.Null(await VolumeStepExecutor.TryBuildGroupUnitFromContentAsync(
                new[] { one }, dir, engine: null));

            // 内容判不出格式（普通字节）⇒ 不建组，且原文件一个字节不动。
            string two = Path.Combine(dir, "b.bin");
            File.WriteAllBytes(two, new byte[] { 3, 4 });

            Assert.Null(await VolumeStepExecutor.TryBuildGroupUnitFromContentAsync(
                new[] { one, two }, dir, engine: null));

            Assert.True(File.Exists(one));
            Assert.True(File.Exists(two));
        }

    }
}
