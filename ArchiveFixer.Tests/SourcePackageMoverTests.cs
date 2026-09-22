using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 源包搬进「其余物」的搬运本体（决策 D-9 / D-12，docs/输出与整理模型.md §3.2）。
    ///
    /// 四条红线，逐条钉死：
    /// ① **整组一起移**：分卷组 = 全部卷，一卷都不能落下；
    /// ② **绝不覆盖**：目标同名加 <c>(1)(2)</c>，已存在的文件一个字节都不动；
    /// ③ **跨盘 = 复制成功后再删原件**：复制失败时源包**原封不动**（绝不"先删后移"）；
    /// ④ 搬不动（被占用 / 只读）→ 如实报失败，源包仍在原处。
    ///
    /// 跨盘那条路真机上很难复现（要临时造第二个卷），所以文件系统是可注入的
    /// （<see cref="ISourceMoveFileSystem"/>）：注入的假实现只接管"同盘/跨盘"与失败注入，
    /// 真正的复制 / 删除仍然落在临时目录里，测的是真实字节。
    /// </summary>
    public class SourcePackageMoverTests : IDisposable
    {
        private readonly string _root;

        public SourcePackageMoverTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSourceMove", Guid.NewGuid().ToString("N"));
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

        private string PathOf(string relativePath) => Path.Combine(_root, relativePath);

        private string WriteFile(string relativePath, string content)
        {
            string path = PathOf(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        private static ArchiveTask SingleTask(string sourcePath)
        {
            return new ArchiveTask(sourcePath);
        }

        private static ArchiveTask VolumeTask(params string[] volumePaths)
        {
            var task = new ArchiveTask(volumePaths[0])
            {
                IsVolumeGroup = true,
                VolumeGroupKey = "group"
            };

            task.VolumePaths.AddRange(volumePaths);
            return task;
        }

        // ---------- ① 计划：整组 + 不覆盖 + 跳过 ----------

        [Fact]
        public void 计划_分卷组整组一起搬_并统计总大小()
        {
            string first = WriteFile(@"src\222.7z.001", "aaaa");
            string second = WriteFile(@"src\222.7z.002", "bbbbbb");
            string artifactDirectory = PathOf(@"out\222\其余物");

            SourcePackageMovePlan plan = new SourcePackageMover()
                .Plan(VolumeTask(first, second), artifactDirectory);

            Assert.Equal(artifactDirectory, plan.ArtifactDirectory);
            Assert.Equal(2, plan.Moves.Count);
            Assert.Equal(10, plan.TotalSize);
            Assert.Equal(
                new[] { Path.Combine(artifactDirectory, "222.7z.001"), Path.Combine(artifactDirectory, "222.7z.002") },
                plan.Moves.Select(m => m.TargetPath).ToArray());
            Assert.Empty(plan.Skipped);

            // 纯规划：一个字节都没动。
            Assert.True(File.Exists(first));
            Assert.False(Directory.Exists(artifactDirectory));
        }

        [Fact]
        public void 计划_目标同名时加序号_绝不覆盖()
        {
            string source = WriteFile(@"src\222.7z", "NEW");
            string artifactDirectory = PathOf(@"out\222\其余物");

            Directory.CreateDirectory(artifactDirectory);
            File.WriteAllText(Path.Combine(artifactDirectory, "222.7z"), "OLD");

            SourcePackageMovePlan plan = new SourcePackageMover().Plan(SingleTask(source), artifactDirectory);

            SourcePackageMove move = Assert.Single(plan.Moves);
            Assert.True(move.Renamed);
            Assert.Equal(Path.Combine(artifactDirectory, "222(1).7z"), move.TargetPath);
        }

        [Fact]
        public void 计划_源不存在时跳过而不是报错()
        {
            SourcePackageMovePlan plan = new SourcePackageMover()
                .Plan(SingleTask(PathOf(@"src\gone.7z")), PathOf(@"out\222\其余物"));

            Assert.Empty(plan.Moves);
            Assert.Equal(ArtifactSkipReason.SourceMissing, Assert.Single(plan.Skipped).Reason);
        }

        [Fact]
        public void 计划_源已经在其余物里时跳过_旧名过程物也算()
        {
            var mover = new SourcePackageMover();

            string inNew = WriteFile(@"out\222\其余物\222.7z", "x");
            string inOld = WriteFile(@"out\222\过程物\222.7z", "x");

            Assert.Empty(mover.Plan(SingleTask(inNew), PathOf(@"out\222\其余物")).Moves);
            Assert.Empty(mover.Plan(SingleTask(inOld), PathOf(@"out\222\其余物")).Moves);
        }

        [Fact]
        public void 计划_分卷清单不完整时什么都不搬()
        {
            // IsVolumeGroup = true 但 VolumePaths 为空：分组信息不完整，少搬一卷比不搬更糟。
            var task = new ArchiveTask(WriteFile(@"src\222.7z.001", "x")) { IsVolumeGroup = true };

            SourcePackageMovePlan plan = new SourcePackageMover().Plan(task, PathOf(@"out\222\其余物"));

            Assert.Empty(plan.Moves);
            Assert.Contains("源包一律不动", plan.Message);
        }

        // ---------- ② 执行：同盘改名 ----------

        [Fact]
        public void 执行_同盘_整组搬进其余物_源文件离开原处()
        {
            string first = WriteFile(@"src\222.7z.001", "aaaa");
            string second = WriteFile(@"src\222.7z.002", "bbbbbb");
            string artifactDirectory = PathOf(@"out\222\其余物");

            var mover = new SourcePackageMover();
            SourcePackageMoveResult result = mover.Execute(mover.Plan(VolumeTask(first, second), artifactDirectory));

            Assert.True(result.Attempted);
            Assert.Equal(2, result.MovedCount);
            Assert.Equal(0, result.FailedCount);
            Assert.Equal(0, result.CrossVolumeCount);
            Assert.Equal(10, result.MovedBytes);
            Assert.False(File.Exists(first));
            Assert.False(File.Exists(second));
            Assert.Equal("aaaa", File.ReadAllText(Path.Combine(artifactDirectory, "222.7z.001")));
            Assert.Equal("bbbbbb", File.ReadAllText(Path.Combine(artifactDirectory, "222.7z.002")));

            // 每次搬运都写日志：从哪到哪 + 大小。
            Assert.Equal(2, result.LogLines.Count);
            Assert.Contains(first, result.LogLines[0], StringComparison.Ordinal);
            Assert.Contains("aaaa".Length.ToString(), result.LogLines[0], StringComparison.Ordinal);
        }

        [Fact]
        public void 执行_同盘改名失败时如实报失败_源包原地不动()
        {
            string source = WriteFile(@"src\222.7z", "DATA");
            string artifactDirectory = PathOf(@"out\222\其余物");

            var fileSystem = new FakeSourceMoveFileSystem { MoveFailure = new IOException("文件被占用") };
            var mover = new SourcePackageMover(fileSystem);

            SourcePackageMoveResult result = mover.Execute(mover.Plan(SingleTask(source), artifactDirectory));

            Assert.Equal(0, result.MovedCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Contains("文件被占用", Assert.Single(result.Failures), StringComparison.Ordinal);
            Assert.True(File.Exists(source), "搬失败时源包必须原地不动");
            Assert.False(File.Exists(Path.Combine(artifactDirectory, "222.7z")));
        }

        [Fact]
        public void 执行_计划之后目标被占用时改名不覆盖()
        {
            string source = WriteFile(@"src\222.7z", "NEW");
            string artifactDirectory = PathOf(@"out\222\其余物");

            var mover = new SourcePackageMover();

            // 计划时目标还不存在（用"什么都占不到"的探测器），执行前才被占 —— 命中最后一道改名。
            SourcePackageMovePlan plan = mover.Plan(SingleTask(source), artifactDirectory, EmptyArtifactTargetProbe.Instance);

            Directory.CreateDirectory(artifactDirectory);
            File.WriteAllText(Path.Combine(artifactDirectory, "222.7z"), "OLD");

            SourcePackageMoveResult result = mover.Execute(plan);

            Assert.Equal(1, result.MovedCount);
            Assert.Equal("OLD", File.ReadAllText(Path.Combine(artifactDirectory, "222.7z")));
            Assert.Equal("NEW", File.ReadAllText(Path.Combine(artifactDirectory, "222(1).7z")));
        }

        // ---------- ③ 执行：跨盘（复制成功后再删原件） ----------

        [Fact]
        public void 执行_跨盘_复制成功后再删原件()
        {
            string source = WriteFile(@"src\222.7z", "CROSS-VOLUME");
            string artifactDirectory = PathOf(@"out\222\其余物");

            var fileSystem = new FakeSourceMoveFileSystem { SameVolume = false };
            var mover = new SourcePackageMover(fileSystem);

            SourcePackageMoveResult result = mover.Execute(mover.Plan(SingleTask(source), artifactDirectory));

            Assert.Equal(1, result.MovedCount);
            Assert.Equal(1, result.CrossVolumeCount);
            Assert.False(fileSystem.MoveWasUsed, "跨盘路径不许走同盘改名");
            Assert.True(fileSystem.CopyWasUsed);
            Assert.False(File.Exists(source), "复制成功之后原件才该消失");
            Assert.Equal("CROSS-VOLUME", File.ReadAllText(Path.Combine(artifactDirectory, "222.7z")));
        }

        [Fact]
        public void 执行_跨盘复制失败_源包一个字节都不动()
        {
            string source = WriteFile(@"src\222.7z", "KEEP-ME");
            string artifactDirectory = PathOf(@"out\222\其余物");

            var fileSystem = new FakeSourceMoveFileSystem
            {
                SameVolume = false,
                CopyFailure = new IOException("目标盘空间不足")
            };

            var mover = new SourcePackageMover(fileSystem);
            SourcePackageMoveResult result = mover.Execute(mover.Plan(SingleTask(source), artifactDirectory));

            Assert.Equal(0, result.MovedCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Contains("目标盘空间不足", Assert.Single(result.Failures), StringComparison.Ordinal);
            Assert.True(File.Exists(source), "复制失败时源包绝不能被动过（绝不先删后移）");
            Assert.Equal("KEEP-ME", File.ReadAllText(source));
            Assert.False(File.Exists(Path.Combine(artifactDirectory, "222.7z")));
        }

        [Fact]
        public void 执行_跨盘复制成功但原件删不掉_回滚副本且源包仍在()
        {
            string source = WriteFile(@"src\222.7z", "STILL-HERE");
            string artifactDirectory = PathOf(@"out\222\其余物");

            var fileSystem = new FakeSourceMoveFileSystem { SameVolume = false };
            fileSystem.DeleteFailures.Add(source);

            var mover = new SourcePackageMover(fileSystem);
            SourcePackageMoveResult result = mover.Execute(mover.Plan(SingleTask(source), artifactDirectory));

            Assert.Equal(0, result.MovedCount);
            Assert.Equal(1, result.FailedCount);
            Assert.True(File.Exists(source), "原件删不掉时源包必须完整保留");
            Assert.False(
                File.Exists(Path.Combine(artifactDirectory, "222.7z")),
                "复制出来的副本必须回滚 —— 否则用户目录里会凭空多一份");

            Assert.Contains(result.LogLines, line => line.Contains("已回滚", StringComparison.Ordinal));
        }

        // ---------- 假文件系统 ----------

        /// <summary>
        /// 只接管"同盘 / 跨盘"这个判断与失败注入，真正的复制 / 删除仍然落在临时目录里 ——
        /// 于是跨盘那条路测的是**真实字节**，不是假的记账。
        /// </summary>
        private sealed class FakeSourceMoveFileSystem : ISourceMoveFileSystem
        {
            private readonly ISourceMoveFileSystem _inner = FileSystemSourceMoveFileSystem.Instance;

            /// <summary>强制"是不是同一个卷"的结论；默认 true（同盘）。</summary>
            public bool SameVolume { get; set; } = true;

            public Exception? MoveFailure { get; set; }

            public Exception? CopyFailure { get; set; }

            /// <summary>这些路径上的删除会抛（模拟被占用 / 只读）。</summary>
            public HashSet<string> DeleteFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

            public bool MoveWasUsed { get; private set; }

            public bool CopyWasUsed { get; private set; }

            public bool FileExists(string path) => _inner.FileExists(path);

            public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

            public bool CreateDirectory(string path) => _inner.CreateDirectory(path);

            public long GetFileSize(string path) => _inner.GetFileSize(path);

            public bool IsSameVolume(string sourcePath, string targetPath) => SameVolume;

            public void MoveFile(string source, string target)
            {
                MoveWasUsed = true;

                if (MoveFailure != null)
                {
                    throw MoveFailure;
                }

                _inner.MoveFile(source, target);
            }

            public void CopyFile(string source, string target)
            {
                CopyWasUsed = true;

                if (CopyFailure != null)
                {
                    throw CopyFailure;
                }

                _inner.CopyFile(source, target);
            }

            public void DeleteFile(string path)
            {
                if (DeleteFailures.Contains(path))
                {
                    throw new IOException("被占用，无法删除");
                }

                _inner.DeleteFile(path);
            }
        }
    }
}
