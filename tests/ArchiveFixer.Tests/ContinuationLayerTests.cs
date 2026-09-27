using System.IO;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 续解层那层目录的规则（用户 2026-09-27 拍板：**首尾必留，中间看开关**）。
    ///
    /// <para>两档要落成的形状（用户原话里的例子）：</para>
    /// <list type="bullet">
    /// <item><description>忠实档（默认，开关关）：<c>111\222\333\444\555\666\内容物</c> —— 每个内层包各占一层；</description></item>
    /// <item><description>简洁档（开关开）：<c>111\222\666\内容物</c> —— 中间的过路层省掉，
    /// 第一层（源包名）与最后一层（装着内容物的那个包）永远保留。</description></item>
    /// </list>
    ///
    /// <para>四条边界（每条都有对应用例）：① 只在**干净的单链**上省；② 父任务自己产出了内容物
    /// （分支）就那一层照建；③ 同一个父任务这一轮认出多个内层包（分支）也照建；
    /// ④ **过程物名不成层** —— 分卷组的基名不许变成文件夹（真机现场 <c>59768866</c>）。</para>
    /// </summary>
    public class ContinuationLayerTests
    {
        // ── ① 该不该建这一层（唯一判据 ShouldAddContinuationLevelLayer） ──────────

        [Fact]
        public void 忠实档_每一层都建层()
        {
            // 开关关 = 忠实档：单链、有没有内容物，都照建（用户要的就是"每层都看得见"）。
            Assert.True(OneClickCoordinator.ShouldAddContinuationLevelLayer(
                omitMiddleLayers: false, parentProducedContent: false, siblingCount: 1));

            Assert.True(OneClickCoordinator.ShouldAddContinuationLevelLayer(
                omitMiddleLayers: false, parentProducedContent: true, siblingCount: 3));
        }

        [Fact]
        public void 简洁档_干净单链的中间层省略()
        {
            // 开关开 + 父任务没产出内容物 + 本轮只有一个内层包 = 干净的单链中间层 → 省掉那一层。
            Assert.False(OneClickCoordinator.ShouldAddContinuationLevelLayer(
                omitMiddleLayers: true, parentProducedContent: false, siblingCount: 1));
        }

        [Fact]
        public void 简洁档_父任务产出过内容物就照建_退化成忠实档()
        {
            // 父任务既有内容物又有内层包 = 分支：那一层照建（⛔ 不许为了"简洁"把分支也省掉）。
            Assert.True(OneClickCoordinator.ShouldAddContinuationLevelLayer(
                omitMiddleLayers: true, parentProducedContent: true, siblingCount: 1));
        }

        [Fact]
        public void 简洁档_同一层认出多个内层包也照建()
        {
            // 一个父任务这一轮认出 2 个以上内层包 = 分支：各自一层，否则几个包的内容物会倒进同一层。
            Assert.True(OneClickCoordinator.ShouldAddContinuationLevelLayer(
                omitMiddleLayers: true, parentProducedContent: false, siblingCount: 2));

            Assert.True(OneClickCoordinator.ShouldAddContinuationLevelLayer(
                omitMiddleLayers: true, parentProducedContent: false, siblingCount: 7));
        }

        // ── ② 层名怎么取（过程物名不成层） ──────────────────────────────────────

        [Theory]
        [InlineData(@"C:\111\222\其余物\333.rar", "333")]
        [InlineData(@"C:\111\222\其余物\333.7z.001", "")]
        [InlineData(@"C:\111\222\其余物\444.rar.jpg", "444")]
        [InlineData(@"C:\111\222\其余物\666.MP4", "666")]
        [InlineData(@"C:\111\222\其余物\59768866.001", "")]
        [InlineData(@"C:\111\222\其余物\x.part2.rar", "")]
        public void 层名取包基名_分卷组取不出来(string innerPackagePath, string expected)
        {
            Assert.Equal(expected, OneClickCoordinator.ResolveContinuationLayerName(innerPackagePath));
        }

        [Fact]
        public void 层名_空路径取不出来()
        {
            Assert.Equal(string.Empty, OneClickCoordinator.ResolveContinuationLayerName(null));
            Assert.Equal(string.Empty, OneClickCoordinator.ResolveContinuationLayerName("   "));
        }

        // ── ③ 追加那一层（防重复、防空） ────────────────────────────────────────

        [Fact]
        public void 追加一层_普通情形()
        {
            Assert.Equal(
                @"C:\111\222\333",
                OneClickCoordinator.AppendOwnLayer(@"C:\111\222", "333"));
        }

        [Fact]
        public void 追加一层_名字为空就一个字都不加()
        {
            Assert.Equal(@"C:\111\222", OneClickCoordinator.AppendOwnLayer(@"C:\111\222", null));
            Assert.Equal(@"C:\111\222", OneClickCoordinator.AppendOwnLayer(@"C:\111\222", "  "));
        }

        [Fact]
        public void 追加一层_最后一段已经同名就不再叠()
        {
            // 333\333.rar 这种"包躺在自名文件夹里"的形状：⛔ 不许变成 333\333。
            Assert.Equal(
                @"C:\111\333",
                OneClickCoordinator.AppendOwnLayer(@"C:\111\333", "333"));

            // 大小写不同也算同名（Windows 路径比较口径）—— 且**原样返回**，不去改写用户路径里的大小写。
            Assert.Equal(
                @"C:\111\ABC",
                OneClickCoordinator.AppendOwnLayer(@"C:\111\ABC", "abc"));

            // 结尾的分隔符不影响判断。
            Assert.Equal(
                @"C:\111\333\",
                OneClickCoordinator.AppendOwnLayer(@"C:\111\333\", "333"));
        }

        // ── ④ 两层接线：落点解析 + "要不要再套一层"的判据 ────────────────────────

        [Fact]
        public void 续解落点_忠实档带上这一层()
        {
            var parent = new ArchiveTask(@"C:\111\222.rar")
            {
                OutputPath = @"C:\111\222",
                ContentDirectoryPath = @"C:\111\222"
            };

            string directory = OneClickCoordinator.ResolveContinuationOutputDirectory(
                parent,
                @"C:\111\222\其余物\333.rar",
                "333");

            Assert.Equal(@"C:\111\222\333", directory);

            // 下一层的落点就在这一层里面 —— 链就是这样一层层搭出来的。
            Assert.Equal(
                @"C:\111\222\333\444",
                OneClickCoordinator.ResolveContinuationOutputDirectory(
                    parent,
                    @"C:\111\222\333\其余物\444.rar",
                    "444"));
        }

        [Fact]
        public void 续解落点_简洁档的单链中间层不追加()
        {
            var parent = new ArchiveTask(@"C:\111\222.rar")
            {
                OutputPath = @"C:\111\222",
                ContentDirectoryPath = @"C:\111\222"
            };

            // 省略那一层：落点就是父任务那一层（内容物最后归到这一层下面）。
            string directory = OneClickCoordinator.ResolveContinuationOutputDirectory(
                parent,
                @"C:\111\222\其余物\333.rar",
                ownLayerName: null);

            Assert.Equal(@"C:\111\222", directory);
        }

        [Theory]
        [InlineData(@"C:\111\222", "666", false)]
        [InlineData(@"C:\111\222\333\666", "666", true)]
        [InlineData(@"C:\111\222\333\666\", "666", true)]
        [InlineData(@"C:\111\222\333", "666", false)]
        [InlineData(@"", "666", false)]
        [InlineData(@"C:\111\222", "", false)]
        public void 落点里有没有自己那一层(string parentDirectory, string archiveBaseName, bool expected)
        {
            Assert.Equal(
                expected,
                ExtractionCoordinator.IsContinuationLayerAlreadyInPath(parentDirectory, archiveBaseName));
        }

        // ── ④之二 层名被内层包自己那个文件占着（名字被改坏时的物理约束） ──────────

        /// <summary>
        /// **名字被改坏、剥不出后缀**的包（<c>user.jp删除g</c> / <c>222.ra删除r</c>）：包基名 == 文件名，
        /// 于是"这一层"的位置正好被那个文件占着 —— Windows 同一条路径不许既有文件又有目录。
        ///
        /// <para>真机测试逮到过：不挡这一下，续解任务会落成"最终目录不存在且创建失败"
        /// （`…\polyglot\user.jp删除g` 既是文件又要当目录）。处置 = 这一层不另建，
        /// 内容落进父任务那一层里（调用方会写一条日志说清）。</para>
        /// </summary>
        [Fact]
        public void 层名被内层包自己那个文件占着_不另建那一层()
        {
            string directory = Path.Combine(Path.GetTempPath(), "ArchiveFixerLayerProbe", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                // 名字被改坏的包：包基名 == 文件名（`user.jp删除g`）。
                string mangled = Path.Combine(directory, "user.jp删除g");
                File.WriteAllText(mangled, "not a real archive");

                Assert.Equal("user.jp删除g", OneClickCoordinator.ResolveContinuationLayerName(mangled));
                Assert.True(OneClickCoordinator.IsOwnLayerOccupiedByPackageFile(directory, "user.jp删除g", mangled));

                var parent = new ArchiveTask(Path.Combine(directory, "parent.7z"))
                {
                    // 让它本身也是一个**续解任务**（父落点 = 这个目录）：这样链根那层"补包名目录"
                    // 不会参与运算，这条用例量的就是"层名被文件占着"这一件事。
                    ParentOutputDirectory = directory,
                    OutputPath = directory,
                    ContentDirectoryPath = directory
                };

                // 该建层时不建（否则会和那个文件撞名）。
                Assert.Equal(
                    directory,
                    OneClickCoordinator.ResolveContinuationOutputDirectory(parent, mangled, "user.jp删除g"));

                // 正常包（`333.7z`）永远不命中这条：层名 `333` 与文件名不同。
                string normal = Path.Combine(directory, "333.7z");
                File.WriteAllText(normal, "not a real archive");

                Assert.False(OneClickCoordinator.IsOwnLayerOccupiedByPackageFile(directory, "333", normal));
                Assert.Equal(
                    Path.Combine(directory, "333"),
                    OneClickCoordinator.ResolveContinuationOutputDirectory(parent, normal, "333"));
            }
            finally
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch
                {
                    // 临时目录删不掉不影响断言结果。
                }
            }
        }

        // ── ⑤ 定稿那一半：手动档「解压到当前文件夹」一层都不套 ──────────────────

        [Fact]
        public void 手动档定稿_不套包名层_但归档自带的那一层照旧保留()
        {
            // 111\222.rar 里有 666\a.mp4（归档自带一层文件夹）→ 手动档要的是 111\666\a.mp4：
            // **不建** 222 那一层，但归档自己的 666 一个字都不动（"按包内原样解开"）。
            FinalizePlan plan = ResultFinalizer.Plan(
                new[]
                {
                    new StagedEntry { RelativePath = @"out\666\a.mp4", Size = 10 },
                    new StagedEntry { RelativePath = @"out\666", IsDirectory = true }
                },
                @"C:\111",
                TerminalLayoutMode.KeepLastFolder,
                archiveBaseName: "222",
                contentRoot: "out",
                stagingRoot: @"C:\stage",
                suppressPackageFolderLayer: true);

            Assert.Equal(@"C:\111\666", plan.ContentParentDirectory);

            // 单链是**整棵子树一次搬走**（少一堆操作）：搬的是 666 这一层，文件跟着在里面。
            Assert.Equal(@"C:\111\666", plan.Moves[0].To);
            Assert.DoesNotContain(plan.Moves, move => move.To.EndsWith(@"\222", StringComparison.Ordinal));
        }

        [Fact]
        public void 手动档定稿_摊在归档根上的多个文件也直接落进那一层()
        {
            FinalizePlan plan = ResultFinalizer.Plan(
                new[]
                {
                    new StagedEntry { RelativePath = @"out\a.mp4", Size = 10 },
                    new StagedEntry { RelativePath = @"out\b.mp4", Size = 20 }
                },
                @"C:\111",
                TerminalLayoutMode.KeepLastFolder,
                archiveBaseName: "222",
                contentRoot: "out",
                stagingRoot: @"C:\stage",
                suppressPackageFolderLayer: true);

            Assert.Equal(@"C:\111", plan.ContentParentDirectory);
            Assert.Equal(@"C:\111\a.mp4", plan.Moves[0].To);
            Assert.Equal(@"C:\111\b.mp4", plan.Moves[1].To);
        }

        [Fact]
        public void 默认档定稿_照旧套一层包名文件夹()
        {
            // 同一份输入、落点是"包名那一层已经在了"（真实情形：根任务的落点就是 111\222）
            // → 不打开手动档时，第二层由归档自带的那一层（666）担任：111\222\666\a.mp4。
            FinalizePlan plan = ResultFinalizer.Plan(
                new[]
                {
                    new StagedEntry { RelativePath = @"out\666\a.mp4", Size = 10 },
                    new StagedEntry { RelativePath = @"out\666", IsDirectory = true }
                },
                @"C:\111\222",
                TerminalLayoutMode.KeepLastFolder,
                archiveBaseName: "222",
                contentRoot: "out",
                stagingRoot: @"C:\stage");

            Assert.Equal(@"C:\111\222\666", plan.ContentParentDirectory);
            Assert.Equal(@"C:\111\222\666", plan.Moves[0].To);
        }
    }
}
