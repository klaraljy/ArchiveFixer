using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「其余物」的命名与目录公式（docs/输出与整理模型.md §3.2，决策 D-8 / D-10）。
    ///
    /// 三条主线：
    /// ① 目录名 = <c>其余物</c>，且**旧名 <c>过程物</c> 仍然被认**（决策 D-8：老版本留下的目录要能清掉）；
    /// ② 其余物目录公式（决策 D-10）：每包一个目录的落点**不再多套一层**，
    ///    多个包共用同一个输出根的模式才按包基名分层；
    /// ③ 内容物那一层正好也叫「其余物」时的兜底（改用「其余物(1)」）。
    ///
    /// 公式的**唯一实现处**是 <see cref="ProcessArtifactLayout"/>：本文件既直接测它，
    /// 也通过 <see cref="ResultFinalizer.Plan"/> 把两种情形的真实落点钉死（两条路必须是同一个答案）。
    /// </summary>
    public class RestDirectoryLayoutTests : IDisposable
    {
        private readonly string _root;

        private const string Staging = @"C:\work\t1";

        public RestDirectoryLayoutTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerRestLayout", Guid.NewGuid().ToString("N"));
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

        private static StagedEntry StagedFile(string relativePath, long size = 0)
        {
            return new StagedEntry { RelativePath = relativePath, Size = size };
        }

        private static StagedEntry StagedArtifact(string relativePath, long size = 0)
        {
            return new StagedEntry { RelativePath = relativePath, Size = size, IsProcessArtifact = true };
        }

        // ---------- ① 命名与旧名识别 ----------

        [Fact]
        public void 其余物目录名_就是规格里的其余物()
        {
            Assert.Equal("其余物", ProcessArtifactLayout.ArtifactDirectoryName);
        }

        [Fact]
        public void 旧目录名_过程物只用于识别不用于新建()
        {
            Assert.Equal("过程物", ProcessArtifactLayout.LegacyArtifactDirectoryName);

            // 新建一律用新名：解析出来的永远是「其余物」。
            Assert.Equal("其余物", ProcessArtifactLayout.ResolveArtifactDirectoryName("666"));
            Assert.Equal("其余物", Path.GetFileName(ProcessArtifactLayout.ResolveArtifactDirectory(@"C:\111\222")));
        }

        [Fact]
        public void 名字判定_新旧两个名字都算其余物()
        {
            Assert.True(ProcessArtifactLayout.IsArtifactDirectoryName("其余物"));
            Assert.True(ProcessArtifactLayout.IsArtifactDirectoryName("过程物"));
            Assert.True(ProcessArtifactLayout.IsArtifactDirectoryName(@"C:\111\222\其余物"));
            Assert.True(ProcessArtifactLayout.IsArtifactDirectoryName(@"C:\111\222\过程物\"));
            Assert.True(ProcessArtifactLayout.IsArtifactDirectoryName("其余物".ToUpperInvariant()));

            Assert.False(ProcessArtifactLayout.IsArtifactDirectoryName("666"));
            Assert.False(ProcessArtifactLayout.IsArtifactDirectoryName("其余物品"));
            Assert.False(ProcessArtifactLayout.IsArtifactDirectoryName(null));
            Assert.False(ProcessArtifactLayout.IsArtifactDirectoryName("   "));
        }

        [Fact]
        public void 找已存在的其余物目录_新旧名都算_一个都没有时为空()
        {
            Directory.CreateDirectory(Path.Combine(_root, "both", "其余物"));
            Directory.CreateDirectory(Path.Combine(_root, "both", "过程物"));
            Directory.CreateDirectory(Path.Combine(_root, "old", "过程物"));
            Directory.CreateDirectory(Path.Combine(_root, "new", "其余物"));
            Directory.CreateDirectory(Path.Combine(_root, "none"));

            // 新名与旧名同时存在 → 两个都要能清（决策 D-8）。
            Assert.Equal(
                new[] { Path.Combine(_root, "both", "其余物"), Path.Combine(_root, "both", "过程物") },
                ProcessArtifactLayout.FindExistingArtifactDirectories(Path.Combine(_root, "both")));

            Assert.Equal(
                new[] { Path.Combine(_root, "old", "过程物") },
                ProcessArtifactLayout.FindExistingArtifactDirectories(Path.Combine(_root, "old")));

            Assert.Equal(
                new[] { Path.Combine(_root, "new", "其余物") },
                ProcessArtifactLayout.FindExistingArtifactDirectories(Path.Combine(_root, "new")));

            Assert.Empty(ProcessArtifactLayout.FindExistingArtifactDirectories(Path.Combine(_root, "none")));
            Assert.Empty(ProcessArtifactLayout.FindExistingArtifactDirectories(null));
        }

        [Fact]
        public void 重名兜底_内容物那层也叫其余物时改用其余物1()
        {
            Assert.Equal("其余物(1)", ProcessArtifactLayout.ResolveArtifactDirectoryName("其余物"));
            Assert.Equal("其余物(1)", ProcessArtifactLayout.ResolveArtifactDirectoryName("过程物"));

            // 内容物目录自己就叫「其余物」时，其余物落在它里面的 其余物(1)（不再叠一个同名的进去）。
            Assert.Equal(
                Path.Combine(@"C:\111\其余物", "其余物(1)"),
                ProcessArtifactLayout.ResolveArtifactDirectory(@"C:\111\其余物"));
        }

        // ---------- ② 其余物目录公式（D-10） ----------

        [Fact]
        public void 目录公式_每包一个目录_其余物在包目录下()
        {
            // 每包一个目录：destDir 本身就是"一个包一个目录" → destDir\其余物\，不再分一层。
            string directory = ProcessArtifactLayout.ResolveArtifactDirectory(
                @"C:\111\222",
                "222",
                sharedRoot: false);

            Assert.Equal(@"C:\111\222\其余物", directory);
        }

        [Fact]
        public void 目录公式_共用根_其余物按包名分层()
        {
            // 共用根（同一次导入里的包都落进同一层）→ 其余物\包基名\。
            string directory = ProcessArtifactLayout.ResolveArtifactDirectory(
                @"C:\111",
                "222.7z.001",
                sharedRoot: true);

            Assert.Equal(@"C:\111\其余物\222.7z.001", directory);   // 基名清洗由调用方负责（分卷组取 222）

            Assert.Equal(
                @"C:\111\其余物\222",
                ProcessArtifactLayout.ResolveArtifactDirectory(@"C:\111", "222", sharedRoot: true));
        }

        [Fact]
        public void 目录公式_指定位置每包一个目录_其余物在包目录下()
        {
            string directory = ProcessArtifactLayout.ResolveArtifactDirectory(
                @"C:\root\222",
                "222",
                sharedRoot: false);

            Assert.Equal(@"C:\root\222\其余物", directory);
        }

        [Fact]
        public void 目录公式_指定位置共用根_其余物按包名分层()
        {
            string directory = ProcessArtifactLayout.ResolveArtifactDirectory(
                @"C:\root",
                "222",
                sharedRoot: true);

            Assert.Equal(@"C:\root\其余物\222", directory);
        }

        [Fact]
        public void 目录公式_共用根但拿不到包基名时退回集中一处()
        {
            Assert.Equal(
                @"C:\root\其余物",
                ProcessArtifactLayout.ResolveArtifactDirectory(@"C:\root", null, sharedRoot: true));

            Assert.Equal(
                @"C:\root\其余物",
                ProcessArtifactLayout.ResolveArtifactDirectory(@"C:\root", "   ", sharedRoot: true));
        }

        // ---------- ②' 同一套公式在定稿计划里的真实落点 ----------

        /// <summary>每包一个目录（默认档）。</summary>
        [Fact]
        public void 定稿落点_每包一个目录_其余物落在包自己那一个目录下()
        {
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { StagedFile(@"out\666\a.mp4"), StagedArtifact(@"inner.7z", 10) },
                @"C:\111\222",
                TerminalLayoutMode.KeepLastFolder,
                "222",
                "out",
                Staging,
                sharedOutputRoot: false);

            Assert.Equal(@"C:\111\222\其余物", plan.ProcessArtifactDirectory);
            Assert.Equal(@"C:\111\222\其余物\inner.7z", plan.ProcessArtifactMoves[0].To);
        }

        /// <summary>共用根：同一次导入里的包都落进同一层（"添加文件夹 + 指定位置"）。</summary>
        [Fact]
        public void 定稿落点_共用根_其余物按包基名分层()
        {
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { StagedFile(@"out\666\a.mp4"), StagedArtifact(@"inner.7z", 10) },
                @"C:\111",
                TerminalLayoutMode.KeepLastFolder,
                "222",
                "out",
                Staging,
                sharedOutputRoot: true);

            Assert.Equal(@"C:\111\其余物\222", plan.ProcessArtifactDirectory);
            Assert.Equal(@"C:\111\其余物\222\inner.7z", plan.ProcessArtifactMoves[0].To);
        }

        /// <summary>指定位置 + 每个包各一个目录。</summary>
        [Fact]
        public void 定稿落点_指定位置每包一个目录_其余物落在包自己那一个目录下()
        {
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { StagedFile(@"out\666\a.mp4"), StagedArtifact(@"inner.7z", 10) },
                @"C:\root\222",
                TerminalLayoutMode.KeepLastFolder,
                "222",
                "out",
                Staging,
                sharedOutputRoot: false);

            Assert.Equal(@"C:\root\222\其余物", plan.ProcessArtifactDirectory);
            Assert.Equal(@"C:\root\222\其余物\inner.7z", plan.ProcessArtifactMoves[0].To);
        }

        /// <summary>指定位置 + 共用根（同一次导入里的包都落进同一层）。</summary>
        [Fact]
        public void 定稿落点_指定位置共用根_其余物按包基名分层()
        {
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { StagedFile(@"out\666\a.mp4"), StagedArtifact(@"inner.7z", 10) },
                @"C:\root",
                TerminalLayoutMode.KeepLastFolder,
                "222",
                "out",
                Staging,
                sharedOutputRoot: true);

            Assert.Equal(@"C:\root\其余物\222", plan.ProcessArtifactDirectory);
            Assert.Equal(@"C:\root\其余物\222\inner.7z", plan.ProcessArtifactMoves[0].To);
        }

        /// <summary>共用根但拿不到包基名：退回集中一处，并且**必须**留下提醒（不许静默）。</summary>
        [Fact]
        public void 定稿落点_共用根拿不到包基名时退回集中一处并提醒()
        {
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { StagedFile(@"out\666\a.mp4"), StagedArtifact(@"inner.7z", 10) },
                @"C:\root",
                stagingRoot: Staging,
                contentRoot: "out",
                sharedOutputRoot: true);

            Assert.Equal(@"C:\root\其余物", plan.ProcessArtifactDirectory);
            Assert.Contains(plan.Warnings, warning => warning.Contains("其余物"));
        }

        /// <summary>
        /// 旧的 <c>过程物\</c> 已经在源里时照样按"已经在其余物里"处理：
        /// 不再搬一次（决策 D-8，老版本留下的目录还在用户的盘上）。
        /// </summary>
        [Fact]
        public void 规划_源已经在旧名过程物里时跳过()
        {
            string oldDirectory = Path.Combine(_root, "过程物");
            Directory.CreateDirectory(oldDirectory);

            string source = Path.Combine(oldDirectory, "inner.7z");
            File.WriteAllText(source, "x");

            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(new ArtifactPlacementRequest
            {
                TargetDirectory = _root,
                Items = new[] { new ArtifactPlacementItem(source, "inner.7z", "内层归档") },
                TargetProbe = EmptyArtifactTargetProbe.Instance
            });

            Assert.Empty(plan.Moves);
            Assert.Equal(ArtifactSkipReason.SourceInsideArtifactDirectory, Assert.Single(plan.Skipped).Reason);
        }
    }
}
