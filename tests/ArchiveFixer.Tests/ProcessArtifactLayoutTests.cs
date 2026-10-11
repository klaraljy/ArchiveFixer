using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 其余物命名与归置规则的测试（docs/输出与整理模型.md §3.2 / 需求变更 R6）。
    ///
    /// 两条主线：
    /// ① **命名只有一个来源** —— 其余物目录名只能来自 <see cref="ProcessArtifactLayout.ArtifactDirectoryName"/>，
    ///    落点只能是 <c>D\其余物\…</c>；
    /// ② <see cref="ProcessArtifactLayout.Plan"/> 是**纯规划**：只算"从哪搬到哪"，
    ///    不建目录、不移动、不删除、不覆盖 —— 每个用例都顺手断言这一点。
    ///
    /// 只在 <see cref="Path.GetTempPath"/> 下的临时目录里动文件，<see cref="Dispose"/> 里清干净。
    /// </summary>
    public class ProcessArtifactLayoutTests : IDisposable
    {
        private readonly string _root;

        public ProcessArtifactLayoutTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerArtifactTests", Guid.NewGuid().ToString("N"));
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

        private string MakeDirectory(string relativePath)
        {
            string path = PathOf(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        private string ArtifactDirectory => PathOf(ProcessArtifactLayout.ArtifactDirectoryName);

        private static ArtifactPlacementRequest Request(
            string targetDirectory,
            string baseDirectory,
            params ArtifactPlacementItem[] items)
        {
            return new ArtifactPlacementRequest
            {
                TargetDirectory = targetDirectory,
                BaseDirectory = baseDirectory,
                Items = items
            };
        }

        // ---------- 命名常量与解析 ----------

        [Fact]
        public void 其余物目录名_就是规格里的其余物()
        {
            Assert.Equal("其余物", ProcessArtifactLayout.ArtifactDirectoryName);
        }

        [Fact]
        public void 解析_其余物目录位于目标目录之下()
        {
            Assert.Equal(ArtifactDirectory, ProcessArtifactLayout.ResolveArtifactDirectory(_root));
            Assert.Equal(
                "其余物",
                Path.GetFileName(ProcessArtifactLayout.ResolveArtifactDirectory(_root)));
        }

        [Fact]
        public void 解析_目标目录为空时返回空串不抛()
        {
            Assert.Equal(string.Empty, ProcessArtifactLayout.ResolveArtifactDirectory(null));
            Assert.Equal(string.Empty, ProcessArtifactLayout.ResolveArtifactDirectory("   "));
        }

        // ---------- 纯规划：结构与重名 ----------

        [Fact]
        public void 规划_保持相对结构()
        {
            string source = WriteFile(@"out\inner\sub\pack.7z.001", "V");

            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(Request(
                _root,
                PathOf("out"),
                new ArtifactPlacementItem(source, @"inner\sub\pack.7z.001", "内层分卷")));

            ArtifactMove move = Assert.Single(plan.Moves);
            Assert.Empty(plan.Skipped);
            Assert.Equal(source, move.SourcePath);
            Assert.Equal(Path.Combine(ArtifactDirectory, "inner", "sub", "pack.7z.001"), move.TargetPath);
            Assert.Equal(@"inner\sub\pack.7z.001", move.RelativePath);
            Assert.Equal("内层分卷", move.Reason);
            Assert.False(move.Renamed);

            // 只规划：源还在原地，其余物目录也没被建出来。
            Assert.True(File.Exists(source));
            Assert.False(Directory.Exists(ArtifactDirectory));
        }

        [Fact]
        public void 规划_同层重名自动加序号不覆盖()
        {
            string first = WriteFile(@"layer-a\pack.7z", "A");
            string second = WriteFile(@"layer-b\pack.7z", "B");

            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(Request(
                _root,
                string.Empty,
                new ArtifactPlacementItem(first, "pack.7z", "分卷"),
                new ArtifactPlacementItem(second, "pack.7z", "分卷")));

            Assert.Equal(2, plan.Moves.Count);
            Assert.Equal(Path.Combine(ArtifactDirectory, "pack.7z"), plan.Moves[0].TargetPath);
            Assert.False(plan.Moves[0].Renamed);
            Assert.Equal(Path.Combine(ArtifactDirectory, "pack(1).7z"), plan.Moves[1].TargetPath);
            Assert.True(plan.Moves[1].Renamed);

            // 两个源都还在原地，没有任何覆盖发生。
            Assert.Equal("A", File.ReadAllText(first));
            Assert.Equal("B", File.ReadAllText(second));
        }

        [Fact]
        public void 规划_目标位置已有同名时也不覆盖()
        {
            string existing = WriteFile(@"其余物\a.txt", "EXISTING");
            string source = WriteFile(@"src\a.txt", "NEW");

            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(Request(
                _root,
                string.Empty,
                new ArtifactPlacementItem(source, "a.txt", "内层归档")));

            ArtifactMove move = Assert.Single(plan.Moves);
            Assert.Equal(Path.Combine(ArtifactDirectory, "a(1).txt"), move.TargetPath);
            Assert.True(move.Renamed);

            // 已有文件一个字节都没动，源也没被搬走、没被新建出来的 (1) 盖掉。
            Assert.Equal("EXISTING", File.ReadAllText(existing));
            Assert.Equal("NEW", File.ReadAllText(source));
            Assert.False(File.Exists(Path.Combine(ArtifactDirectory, "a(1).txt")));
        }

        [Fact]
        public void 规划_源是目录时重名整名加序号不拆扩展名()
        {
            string existing = MakeDirectory(@"其余物\v1.2");
            string source = MakeDirectory(@"src\v1.2");

            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(Request(
                _root,
                string.Empty,
                new ArtifactPlacementItem(source, "v1.2", "纯壳目录")));

            ArtifactMove move = Assert.Single(plan.Moves);
            Assert.Equal(Path.Combine(ArtifactDirectory, "v1.2(1)"), move.TargetPath);
            Assert.True(move.Renamed);

            Assert.True(Directory.Exists(existing));
            Assert.True(Directory.Exists(source));
            Assert.False(Directory.Exists(Path.Combine(ArtifactDirectory, "v1.2(1)")));
        }

        [Fact]
        public void 规划_批内序号与磁盘上已有名字都不会撞()
        {
            WriteFile(@"其余物\pack(1).7z", "EXISTING");
            string first = WriteFile(@"a\pack.7z", "A");
            string second = WriteFile(@"b\pack.7z", "B");
            string third = WriteFile(@"c\pack.7z", "C");

            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(Request(
                _root,
                string.Empty,
                new ArtifactPlacementItem(first, "pack.7z"),
                new ArtifactPlacementItem(second, "pack.7z"),
                new ArtifactPlacementItem(third, "pack.7z")));

            Assert.Equal(3, plan.Moves.Count);
            Assert.Equal(Path.Combine(ArtifactDirectory, "pack.7z"), plan.Moves[0].TargetPath);
            Assert.Equal(Path.Combine(ArtifactDirectory, "pack(2).7z"), plan.Moves[1].TargetPath);
            Assert.Equal(Path.Combine(ArtifactDirectory, "pack(3).7z"), plan.Moves[2].TargetPath);
            Assert.Equal("EXISTING", File.ReadAllText(PathOf(@"其余物\pack(1).7z")));
        }

        // ---------- 纯规划：相对路径推算 ----------

        [Fact]
        public void 规划_相对路径为空时用文件名()
        {
            string source = WriteFile(@"src\movie.mp4", "M");

            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(Request(
                _root,
                string.Empty,
                new ArtifactPlacementItem(source)));

            Assert.Equal(Path.Combine(ArtifactDirectory, "movie.mp4"), Assert.Single(plan.Moves).TargetPath);
        }

        [Fact]
        public void 规划_按基准目录推算相对路径()
        {
            string source = WriteFile(@"out\inner\pack.7z.001", "V");

            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(Request(
                _root,
                PathOf("out"),
                new ArtifactPlacementItem(source)));

            Assert.Equal(
                Path.Combine(ArtifactDirectory, "inner", "pack.7z.001"),
                Assert.Single(plan.Moves).TargetPath);
        }

        [Fact]
        public void 规划_基准之外的源被跳过()
        {
            string source = WriteFile(@"other\a.txt", "A");

            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(Request(
                _root,
                PathOf("out"),
                new ArtifactPlacementItem(source)));

            Assert.Empty(plan.Moves);
            ArtifactSkip skip = Assert.Single(plan.Skipped);
            Assert.Equal(ArtifactSkipReason.InvalidRelativePath, skip.Reason);
            Assert.Contains("..", skip.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(source));
        }

        [Fact]
        public void 规划_非法字符被清洗后再落位()
        {
            string source = WriteFile(@"src\weird.txt", "W");

            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(Request(
                _root,
                string.Empty,
                new ArtifactPlacementItem(source, "a<b>c.txt")));

            ArtifactMove move = Assert.Single(plan.Moves);
            Assert.Equal("a_b_c.txt", Path.GetFileName(move.TargetPath));
        }

        // ---------- 纯规划：拒绝面 ----------

        [Fact]
        public void 规划_越界相对路径被跳过()
        {
            string source = WriteFile(@"src\a.txt", "A");

            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(Request(
                _root,
                string.Empty,
                new ArtifactPlacementItem(source, @"..\..\evil.txt")));

            Assert.Empty(plan.Moves);
            ArtifactSkip skip = Assert.Single(plan.Skipped);
            Assert.Equal(ArtifactSkipReason.InvalidRelativePath, skip.Reason);
        }

        [Fact]
        public void 规划_绝对路径与UNC路径被跳过()
        {
            string first = WriteFile(@"src\a.txt", "A");
            string second = WriteFile(@"src\b.txt", "B");

            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(Request(
                _root,
                string.Empty,
                new ArtifactPlacementItem(first, @"C:\Windows\System32\evil.dll"),
                new ArtifactPlacementItem(second, @"\\server\share\evil.dll")));

            Assert.Empty(plan.Moves);
            Assert.Equal(2, plan.Skipped.Count);
            Assert.All(plan.Skipped, skip => Assert.Equal(ArtifactSkipReason.InvalidRelativePath, skip.Reason));
        }

        [Fact]
        public void 规划_保留设备名被跳过()
        {
            string source = WriteFile(@"src\a.txt", "A");

            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(Request(
                _root,
                string.Empty,
                new ArtifactPlacementItem(source, "CON.txt")));

            Assert.Empty(plan.Moves);
            Assert.Equal(ArtifactSkipReason.InvalidRelativePath, Assert.Single(plan.Skipped).Reason);
        }

        [Fact]
        public void 规划_源已经在其余物目录里时跳过()
        {
            string source = WriteFile(@"其余物\inner\pack.7z", "P");

            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(Request(
                _root,
                string.Empty,
                new ArtifactPlacementItem(source)));

            Assert.Empty(plan.Moves);
            ArtifactSkip skip = Assert.Single(plan.Skipped);
            Assert.Equal(ArtifactSkipReason.SourceInsideArtifactDirectory, skip.Reason);
            Assert.True(File.Exists(source));
        }

        [Fact]
        public void 规划_目标落在源内部时跳过()
        {
            // 源就是目标目录本身：把 D 搬进 D\其余物 会自己套自己。
            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(Request(
                _root,
                string.Empty,
                new ArtifactPlacementItem(_root, "a.txt")));

            Assert.Empty(plan.Moves);
            Assert.Equal(ArtifactSkipReason.TargetInsideSource, Assert.Single(plan.Skipped).Reason);
        }

        [Fact]
        public void 规划_源为空时跳过()
        {
            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(Request(
                _root,
                string.Empty,
                new ArtifactPlacementItem(string.Empty, "a.txt")));

            Assert.Empty(plan.Moves);
            Assert.Equal(ArtifactSkipReason.EmptySource, Assert.Single(plan.Skipped).Reason);
        }

        [Fact]
        public void 规划_重复源只规划一次()
        {
            string source = WriteFile(@"src\a.txt", "A");

            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(Request(
                _root,
                string.Empty,
                new ArtifactPlacementItem(source, "a.txt"),
                new ArtifactPlacementItem(source, "sub\a.txt")));

            Assert.Single(plan.Moves);
            Assert.Equal(
                ArtifactSkipReason.DuplicateSource,
                Assert.Single(plan.Skipped).Reason);
        }

        [Fact]
        public void 规划_没有目标目录时全部跳过()
        {
            string source = WriteFile(@"src\a.txt", "A");

            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(Request(
                string.Empty,
                string.Empty,
                new ArtifactPlacementItem(source, "a.txt")));

            Assert.Empty(plan.Moves);
            Assert.Equal(string.Empty, plan.ArtifactDirectory);
            Assert.Equal(ArtifactSkipReason.TargetDirectoryMissing, Assert.Single(plan.Skipped).Reason);
            Assert.True(File.Exists(source));
        }

        [Fact]
        public void 规划_请求为空时不抛()
        {
            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(null);

            Assert.Empty(plan.Moves);
            Assert.Empty(plan.Skipped);
            Assert.Contains("未指定目标目录", plan.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 规划_只规划不执行()
        {
            string first = WriteFile(@"out\inner\pack.7z", "P");
            string second = WriteFile(@"out\movie.mp4", "M");

            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(Request(
                _root,
                PathOf("out"),
                new ArtifactPlacementItem(first, @"inner\pack.7z", "内层归档"),
                new ArtifactPlacementItem(second, @"movie.mp4", "杂物")));

            Assert.Equal(2, plan.Moves.Count);

            // 源一个都没动，其余物目录也没有被创建 —— 执行是调用方的事。
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.False(Directory.Exists(ArtifactDirectory));
        }

        [Fact]
        public void 规划_说明里报出改名与跳过数量()
        {
            WriteFile(@"其余物\pack.7z", "EXISTING");
            string source = WriteFile(@"a\pack.7z", "A");
            string escaping = WriteFile(@"b\x.txt", "X");

            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(Request(
                _root,
                string.Empty,
                new ArtifactPlacementItem(source, "pack.7z"),
                new ArtifactPlacementItem(escaping, @"..\evil.txt")));

            Assert.Single(plan.Moves);
            Assert.Single(plan.Skipped);
            Assert.Contains("1 项", plan.Message, StringComparison.Ordinal);
            Assert.Contains("被跳过", plan.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 规划_空条目清单时给出空计划()
        {
            ArtifactMovePlan plan = ProcessArtifactLayout.Plan(new ArtifactPlacementRequest
            {
                TargetDirectory = _root,
                Items = Array.Empty<ArtifactPlacementItem>()
            });

            Assert.Empty(plan.Moves);
            Assert.Empty(plan.Skipped);
            Assert.Equal(ArtifactDirectory, plan.ArtifactDirectory);
        }

        /// <summary>
        /// ⛔ **"这一单自己那一份在盘上"必须按"同一组"那把尺子找（族 + 基名），⛔ 不是只比包基名**
        /// （2026-10-11 补；这条路径的值直接喂给 <c>SourcePackageMover.Plan</c>，认错人就会把**别人的包**
        /// 搬进其余物、删除档下还会被永久删掉）。
        ///
        /// <para><b>夹具</b>：同一目录里放 `111.rar` 与 `111.zip`（同基名、**不同族**），
        /// 这一单最初导入的是 `111.rar`，而它已经被改名/搬走（盘上不在）⇒ 回落到"按尺子找"。</para>
        ///
        /// <para><b>红检</b>：把判据换回"只比包基名"⇒ 本条当场变红（会返回 `111.zip`）。</para>
        /// </summary>
        [Fact]
        public void 自己那一份要按同族同基名找_绝不认到另一个后缀的包()
        {
            string directory = PathOf("同基名两个族");
            Directory.CreateDirectory(directory);

            // 另一个包：同基名、不同族（RarOld vs ZipSpanned）。
            string other = Path.Combine(directory, "111.zip");
            File.WriteAllBytes(other, new byte[1024]);

            // 这一单最初导入的那一份（现在盘上已经不在）。
            string own = Path.Combine(directory, "111.rar");

            var task = new ArchiveFixer.Models.ArchiveTask(own, 1);

            string resolved = SourcePackageMover.ResolveOwnFileOnDisk(task);

            Assert.NotEqual(other, resolved);
            Assert.Equal(string.Empty, resolved);
        }

        /// <summary>
        /// 对照：**同族改名**（`111.z0删除2` → `111.z02`）照样要认得出 —— 尺子宽窄是刻意的，
        /// ⛔ 别把"同族"也一起挡掉。
        /// </summary>
        [Fact]
        public void 同族改名的自己那一份_照样认得出()
        {
            string directory = PathOf("同族改名");
            Directory.CreateDirectory(directory);

            string renamed = Path.Combine(directory, "111.z02");
            File.WriteAllBytes(renamed, new byte[1024]);

            var task = new ArchiveFixer.Models.ArchiveTask(Path.Combine(directory, "111.z0删除2"), 1);

            Assert.Equal(renamed, SourcePackageMover.ResolveOwnFileOnDisk(task));
        }
    }
}
