using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// M3「一批包一键搞定」的三个维护模块测试：
    /// <see cref="OutputVerifier"/>（解压结果校验）、<see cref="SourceCleanupService"/>（可选清理源包）、
    /// <see cref="ResultCollector"/>（结果归集）。
    ///
    /// 全部只动 <see cref="Path.GetTempPath"/> 下的临时目录，<see cref="Dispose"/> 里删干净；
    /// 不碰项目目录、不碰用户个人目录（AGENTS.md §8）。
    /// 测试数据一律是合成值（文件名如 pack.zip、内容如 "NEW"），不含任何真实密码或站点信息。
    /// </summary>
    public class ExtractionMaintenanceTests : IDisposable
    {
        private readonly string _root;

        public ExtractionMaintenanceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerExtractionTests", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响测试结论。
            }
        }

        // ---------- 测试辅助 ----------

        private string PathOf(string relativePath)
        {
            return Path.Combine(_root, relativePath);
        }

        private string WriteFile(string relativePath, string content)
        {
            string path = PathOf(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>造一个"列目录成功"的预期结果。</summary>
        private static ArchiveListResult Expected(int fileCount, long totalSize)
        {
            return new ArchiveListResult
            {
                Success = true,
                FileCount = fileCount,
                DirectoryCount = 0,
                TotalUncompressedSize = totalSize,
                Entries = new List<ArchiveEntry>(),
                EngineId = "SevenZip",
                EngineVersion = "26.01"
            };
        }

        private static OutputVerificationResult Verified()
        {
            return new OutputVerificationResult
            {
                Verified = true,
                Message = "测试用：校验通过"
            };
        }

        /// <summary>断言目录已经不存在、或者已经空了（两种都算"没有残留产物"）。</summary>
        private static void AssertNoFilesLeft(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            Assert.Empty(Directory.GetFileSystemEntries(directory));
        }

        // ---------- Measure ----------

        [Fact]
        public void 度量_目录不存在时返回零()
        {
            (int fileCount, long totalSize) = OutputVerifier.Measure(PathOf("not-exists"));

            Assert.Equal(0, fileCount);
            Assert.Equal(0L, totalSize);
        }

        [Fact]
        public void 度量_目录路径为空时返回零()
        {
            (int fileCount, long totalSize) = OutputVerifier.Measure(null);

            Assert.Equal(0, fileCount);
            Assert.Equal(0L, totalSize);
        }

        [Fact]
        public void 度量_空目录返回零()
        {
            Directory.CreateDirectory(PathOf("empty"));

            (int fileCount, long totalSize) = OutputVerifier.Measure(PathOf("empty"));

            Assert.Equal(0, fileCount);
            Assert.Equal(0L, totalSize);
        }

        [Fact]
        public void 度量_递归统计嵌套子目录()
        {
            WriteFile(@"out\a.txt", "12345");             // 5 字节
            WriteFile(@"out\sub\b.txt", "1234567890");    // 10 字节
            WriteFile(@"out\sub\deep\c.txt", "abc");      // 3 字节
            Directory.CreateDirectory(PathOf(@"out\empty-dir"));

            (int fileCount, long totalSize) = OutputVerifier.Measure(PathOf("out"));

            Assert.Equal(3, fileCount);
            Assert.Equal(18L, totalSize);
        }

        // 说明：符号链接 / 目录联接点要跳过的分支没有单独写测试 ——
        // 造联接点要么需要管理员权限（symlink），要么得调 mklink 外部命令，在测试里不稳定；
        // 该分支的实现见 OutputVerifier.Measure 里对 FileAttributes.ReparsePoint 的判断。

        // ---------- Verify ----------

        [Fact]
        public void 校验_空目录不通过并说明没有产物()
        {
            Directory.CreateDirectory(PathOf("out"));

            OutputVerificationResult result = OutputVerifier.Verify(PathOf("out"), Expected(3, 100));

            Assert.False(result.Verified);
            Assert.Equal(0, result.ActualFileCount);
            Assert.Equal(3, result.ExpectedFileCount);
            Assert.Contains("没有产物", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 校验_输出目录不存在时不通过()
        {
            OutputVerificationResult result = OutputVerifier.Verify(PathOf("not-exists"), Expected(1, 10));

            Assert.False(result.Verified);
            Assert.Contains("没有产物", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 校验_没有预期结果时只做非空校验()
        {
            WriteFile(@"out\a.bin", "12345");

            OutputVerificationResult result = OutputVerifier.Verify(PathOf("out"), null);

            Assert.True(result.Verified);
            Assert.Equal(1, result.ActualFileCount);
            Assert.Equal(5L, result.ActualTotalSize);
            Assert.Contains("非空", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 校验_列目录失败时退回非空校验()
        {
            WriteFile(@"out\a.bin", "12345");

            var failed = new ArchiveListResult
            {
                Success = false,
                ErrorType = "EngineFailure",
                Message = "测试用：列目录失败"
            };

            OutputVerificationResult result = OutputVerifier.Verify(PathOf("out"), failed);

            Assert.True(result.Verified);
            Assert.Contains("非空", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 校验_实际多于预期时通过()
        {
            // 输出目录里本来就有别的东西（用户选了已有目录）时也必须算通过 —— 所以用 >= 而不是 ==。
            WriteFile(@"out\a.bin", "12345");
            WriteFile(@"out\b.bin", "12345");
            WriteFile(@"out\c.bin", "12345");

            OutputVerificationResult result = OutputVerifier.Verify(PathOf("out"), Expected(2, 10));

            Assert.True(result.Verified);
            Assert.Equal(3, result.ActualFileCount);
            Assert.Equal(15L, result.ActualTotalSize);
        }

        [Fact]
        public void 校验_实际少于预期时不通过并写明预期与实际()
        {
            WriteFile(@"out\a.bin", "12345");    // 只有 1 个文件 5 字节
            WriteFile(@"out\b.bin", "12345");

            OutputVerificationResult result = OutputVerifier.Verify(PathOf("out"), Expected(5, 5000));

            Assert.False(result.Verified);
            Assert.Equal(5, result.ExpectedFileCount);
            Assert.Equal(2, result.ActualFileCount);
            Assert.Equal(5000L, result.ExpectedTotalSize);
            Assert.Equal(10L, result.ActualTotalSize);

            // Message 必须同时给出预期与实际，用户才知道差在哪。
            Assert.Contains("预期 5 个文件 / 5000 字节", result.Message, StringComparison.Ordinal);
            Assert.Contains("实际 2 个 / 10 字节", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 校验_字节数不足时也不通过()
        {
            WriteFile(@"out\a.bin", "12345");    // 文件数够、字节数不够

            OutputVerificationResult result = OutputVerifier.Verify(PathOf("out"), Expected(1, 999));

            Assert.False(result.Verified);
            Assert.Contains("校验未通过", result.Message, StringComparison.Ordinal);
        }

        // ---------- SourceCleanupService ----------

        [Fact]
        public void 清理_未开启时一个文件都不碰()
        {
            string source = WriteFile(@"src\pack.7z", "0123456789");
            var task = new ArchiveTask(source);

            SourceCleanupResult result = new SourceCleanupService().Cleanup(task, Verified(), enabled: false);

            Assert.False(result.Attempted);
            Assert.Empty(result.DeletedFiles);
            Assert.Equal(0L, result.FreedBytes);
            Assert.True(File.Exists(source), "开关没开时源文件必须原样保留");
            Assert.Contains("未开启", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 清理_校验未通过时保留源文件()
        {
            string source = WriteFile(@"src\pack.7z", "0123456789");
            var task = new ArchiveTask(source);

            var verification = new OutputVerificationResult
            {
                Verified = false,
                Message = "输出目录是空目录，没有产物"
            };

            SourceCleanupResult result = new SourceCleanupService().Cleanup(task, verification, enabled: true);

            Assert.False(result.Attempted);
            Assert.Empty(result.DeletedFiles);
            Assert.True(File.Exists(source), "校验没过时必须保留源文件");
            Assert.Contains("校验未通过", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 清理_没有校验结果时保留源文件()
        {
            string source = WriteFile(@"src\pack.7z", "0123456789");
            var task = new ArchiveTask(source);

            SourceCleanupResult result = new SourceCleanupService().Cleanup(task, null, enabled: true);

            Assert.False(result.Attempted);
            Assert.True(File.Exists(source), "没有校验结果时必须保留源文件");
        }

        [Fact]
        public void 清理_单文件任务只删自己不动邻居()
        {
            string source = WriteFile(@"src\pack.7z", "0123456789");   // 10 字节
            string neighbour = WriteFile(@"src\other.7z", "ABCDEF");
            string sidecar = WriteFile(@"src\pack.txt", "密码：TestPass123!");

            var task = new ArchiveTask(source);

            SourceCleanupResult result = new SourceCleanupService().Cleanup(task, Verified(), enabled: true);

            Assert.True(result.Attempted);
            Assert.Equal(source, Assert.Single(result.DeletedFiles));
            Assert.Empty(result.FailedFiles);
            Assert.Equal(10L, result.FreedBytes);

            Assert.False(File.Exists(source), "任务自己的源文件应被删除");
            Assert.True(File.Exists(neighbour), "同目录的邻居文件不属于本任务，绝不能删");
            Assert.True(File.Exists(sidecar), "同目录的说明文件不属于本任务，绝不能删");
            Assert.Contains("已删除 1 个源文件", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 清理_分卷组整组删除并跳过不存在的卷()
        {
            string volume1 = WriteFile(@"vol\pack.7z.001", "11111");        // 5 字节
            string volume2 = WriteFile(@"vol\pack.7z.002", "2222222");      // 7 字节
            string neighbour = WriteFile(@"vol\pack.7z.999", "not-a-volume");

            // 第三卷故意不落盘：清单里有、磁盘上没有时必须跳过，而不是报成删除失败。
            string missingVolume3 = PathOf(@"vol\pack.7z.003");

            var task = new ArchiveTask(volume1) { IsVolumeGroup = true };
            task.VolumePaths.Add(volume1);
            task.VolumePaths.Add(volume2);
            task.VolumePaths.Add(missingVolume3);

            SourceCleanupResult result = new SourceCleanupService().Cleanup(task, Verified(), enabled: true);

            Assert.True(result.Attempted);
            Assert.Equal(2, result.DeletedFiles.Count);
            Assert.Empty(result.FailedFiles);
            Assert.Equal(12L, result.FreedBytes);

            Assert.False(File.Exists(volume1));
            Assert.False(File.Exists(volume2));
            Assert.True(File.Exists(neighbour), "不在 VolumePaths 清单里的文件绝不能删");
            Assert.Contains("已删除 2 个源分卷", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 清理_单个分卷被占用时继续删其余卷并报部分失败()
        {
            string lockedVolume = WriteFile(@"vol\pack.7z.001", "11111");
            string freeVolume = WriteFile(@"vol\pack.7z.002", "2222222");

            var task = new ArchiveTask(lockedVolume) { IsVolumeGroup = true };
            task.VolumePaths.Add(lockedVolume);
            task.VolumePaths.Add(freeVolume);

            using (new FileStream(lockedVolume, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                SourceCleanupResult result = new SourceCleanupService().Cleanup(task, Verified(), enabled: true);

                Assert.True(result.Attempted);
                Assert.Equal(freeVolume, Assert.Single(result.DeletedFiles));
                Assert.Equal(lockedVolume, Assert.Single(result.FailedFiles));
                Assert.Equal(7L, result.FreedBytes);
                Assert.Contains("部分失败", result.Message, StringComparison.Ordinal);
            }

            // 被占用的那一卷没有被删掉，且占用解除后仍然完整 —— 失败不会误删。
            Assert.True(File.Exists(lockedVolume));
            Assert.Equal("11111", File.ReadAllText(lockedVolume));
            Assert.False(File.Exists(freeVolume));
        }

        [Fact]
        public void 清理_没有源文件路径时不动手()
        {
            var task = new ArchiveTask(string.Empty);

            SourceCleanupResult result = new SourceCleanupService().Cleanup(task, Verified(), enabled: true);

            Assert.False(result.Attempted);
            Assert.Empty(result.DeletedFiles);
            Assert.Empty(result.FailedFiles);
        }

        // 说明：目录 / 符号链接被拒绝删除的分支没有单独写测试 ——
        // VolumePaths 与 CurrentPath 都来自扫描结果，指向目录或链接的情况在真实流程里不出现，
        // 该判断是纯防御性代码（见 SourceCleanupService 里对 FileAttributes 的检查）。

        // ---------- ResultCollector ----------

        [Fact]
        public void 归集_正常移动后源目录清空且产物齐全()
        {
            WriteFile(@"out\readme.md", "R");
            WriteFile(@"out\docs\a.txt", "A");
            WriteFile(@"out\docs\sub\b.txt", "B");

            var task = new ArchiveTask(PathOf("pack.zip")) { OutputPath = PathOf("out") };

            CollectResult result = new ResultCollector().Collect(task, PathOf("collect"));

            Assert.True(result.Success);
            Assert.Equal(3, result.MovedFileCount);
            Assert.Empty(result.Conflicts);
            Assert.Equal(PathOf(@"collect\pack"), result.DestinationPath);

            Assert.Equal("R", File.ReadAllText(Path.Combine(result.DestinationPath, "readme.md")));
            Assert.Equal("A", File.ReadAllText(Path.Combine(result.DestinationPath, "docs", "a.txt")));
            Assert.Equal("B", File.ReadAllText(Path.Combine(result.DestinationPath, "docs", "sub", "b.txt")));

            // 产物目录下只剩空目录（通常连空目录都顺手删了），不能留下任何文件。
            AssertNoFilesLeft(PathOf("out"));
        }

        [Fact]
        public void 归集_开启摊平时不套两层目录()
        {
            // 解压出来是 out/包名/… 这种"无意义外层目录"（设计.md §九）。
            WriteFile(@"out\inner\a.txt", "A");
            WriteFile(@"out\inner\sub\b.txt", "B");

            var task = new ArchiveTask(PathOf("pack.zip")) { OutputPath = PathOf("out") };

            CollectResult result = new ResultCollector().Collect(task, PathOf("collect"), flattenSingleWrapper: true);

            Assert.True(result.Success);
            Assert.Equal(2, result.MovedFileCount);
            Assert.Equal(PathOf(@"collect\pack"), result.DestinationPath);

            Assert.Equal("A", File.ReadAllText(Path.Combine(result.DestinationPath, "a.txt")));
            Assert.Equal("B", File.ReadAllText(Path.Combine(result.DestinationPath, "sub", "b.txt")));

            // 不套两层：目标目录下不该再出现 inner 这一层。
            Assert.False(Directory.Exists(Path.Combine(result.DestinationPath, "inner")));
            AssertNoFilesLeft(PathOf("out"));
        }

        [Fact]
        public void 归集_关闭摊平时保留外层目录()
        {
            WriteFile(@"out\inner\a.txt", "A");

            var task = new ArchiveTask(PathOf("pack.zip")) { OutputPath = PathOf("out") };

            CollectResult result = new ResultCollector().Collect(task, PathOf("collect"), flattenSingleWrapper: false);

            Assert.True(result.Success);
            Assert.Equal(1, result.MovedFileCount);
            Assert.True(File.Exists(Path.Combine(result.DestinationPath, "inner", "a.txt")));
        }

        [Fact]
        public void 归集_目标同名文件已存在时不覆盖并报冲突()
        {
            WriteFile(@"out\same.txt", "NEW");
            WriteFile(@"collect\pack\same.txt", "OLD");

            var task = new ArchiveTask(PathOf("pack.zip")) { OutputPath = PathOf("out") };

            CollectResult result = new ResultCollector().Collect(task, PathOf("collect"));

            Assert.True(result.Success);
            Assert.Equal(1, result.MovedFileCount);

            // 目标子目录已存在 → 本次产物整体落到 pack(1)，绝不与已有结果混在一起。
            Assert.Equal(PathOf(@"collect\pack(1)"), result.DestinationPath);

            // 同名冲突必须报出来，用户才知道目标位置本来就有这个文件。
            Assert.Single(result.Conflicts);
            Assert.Contains("same.txt", result.Conflicts[0], StringComparison.OrdinalIgnoreCase);

            Assert.Equal("OLD", File.ReadAllText(PathOf(@"collect\pack\same.txt")));
            Assert.Equal("NEW", File.ReadAllText(Path.Combine(result.DestinationPath, "same.txt")));
        }

        [Fact]
        public void 归集_目标位置被同名文件占着时也不覆盖()
        {
            WriteFile(@"out\a.txt", "A");
            WriteFile(@"collect\pack", "这是一个占位文件，不是目录");

            var task = new ArchiveTask(PathOf("pack.zip")) { OutputPath = PathOf("out") };

            CollectResult result = new ResultCollector().Collect(task, PathOf("collect"));

            Assert.True(result.Success);
            Assert.Equal(1, result.MovedFileCount);
            Assert.Equal(PathOf(@"collect\pack(1)"), result.DestinationPath);
            Assert.Equal("这是一个占位文件，不是目录", File.ReadAllText(PathOf(@"collect\pack")));
        }

        [Fact]
        public void 归集_目标目录不存在时会自动创建()
        {
            WriteFile(@"out\a.txt", "A");

            var task = new ArchiveTask(PathOf("pack.zip")) { OutputPath = PathOf("out") };
            string targetRoot = PathOf(@"deep\nested\collect");

            Assert.False(Directory.Exists(targetRoot));

            CollectResult result = new ResultCollector().Collect(task, targetRoot);

            Assert.True(result.Success);
            Assert.True(Directory.Exists(targetRoot));
            Assert.True(File.Exists(Path.Combine(result.DestinationPath, "a.txt")));
        }

        [Fact]
        public void 归集_未指定目标目录时失败且不动产物()
        {
            WriteFile(@"out\a.txt", "A");

            var task = new ArchiveTask(PathOf("pack.zip")) { OutputPath = PathOf("out") };

            CollectResult result = new ResultCollector().Collect(task, null);

            Assert.False(result.Success);
            Assert.Equal(0, result.MovedFileCount);
            Assert.Equal(string.Empty, result.DestinationPath);
            Assert.Contains("未指定归集目标目录", result.Message, StringComparison.Ordinal);
            Assert.Equal("A", File.ReadAllText(PathOf(@"out\a.txt")));
        }

        [Fact]
        public void 归集_输出目录为空时失败且不抛()
        {
            var task = new ArchiveTask(PathOf("pack.zip"));

            CollectResult result = new ResultCollector().Collect(task, PathOf("collect"));

            Assert.False(result.Success);
            Assert.Equal(0, result.MovedFileCount);
            Assert.Contains("输出目录", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 归集_产物目录不存在时失败且不抛()
        {
            var task = new ArchiveTask(PathOf("pack.zip")) { OutputPath = PathOf("not-exists") };

            CollectResult result = new ResultCollector().Collect(task, PathOf("collect"));

            Assert.False(result.Success);
            Assert.Contains("产物目录不存在", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 归集_任务为空时失败且不抛()
        {
            CollectResult result = new ResultCollector().Collect(null, PathOf("collect"));

            Assert.False(result.Success);
            Assert.Equal(0, result.MovedFileCount);
        }

        [Fact]
        public void 归集_目标位于产物目录内部时拒绝执行()
        {
            WriteFile(@"out\a.txt", "A");

            var task = new ArchiveTask(PathOf("pack.zip")) { OutputPath = PathOf("out") };

            CollectResult result = new ResultCollector().Collect(task, PathOf(@"out\collect"));

            Assert.False(result.Success);
            Assert.Contains("产物目录内部", result.Message, StringComparison.Ordinal);

            // 拒绝执行时连空目录都不该留下，产物也必须原样未动。
            Assert.False(Directory.Exists(PathOf(@"out\collect")));
            Assert.Equal("A", File.ReadAllText(PathOf(@"out\a.txt")));
        }
    }
}
