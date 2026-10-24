using System;
using System.IO;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 续解层那层目录的规则（用户 2026-09-27 拍板：**首尾必留**；
    /// 2026-10-16 删掉了中间那一档开关 ⇒ 中间层**一律照建**）。
    ///
    /// <para>现在落成的形状：<c>111\222\333\444\555\666\内容物</c> —— 每个内层包各占一层，
    /// 第一层（源包名）与最后一层（装着内容物的那个包）永远保留。</para>
    ///
    /// <para>三条边界（每条都有对应用例）：① 父任务自己产出了内容物（分支）就那一层照建、
    /// 内层包解出来的东西并进父层；② 同一个父任务这一轮认出多个内层包（分支）各自占一层；
    /// ③ **过程物名不成层** —— 分卷组的基名不许变成文件夹（真机现场 <c>59768866</c>）。</para>
    /// </summary>
    public class ContinuationLayerTests
    {
        // ── ① 该不该建这一层（唯一判据 ShouldAddContinuationLevelLayer） ──────────

        [Fact]
        public void 干净单链的每一层都建层()
        {
            // **干净的单链过路层**（父层只出了下一个包）照建 ——
            // 这正是 111\222\333\444\555\666\内容物 那条链。
            // 2026-10-16：原来还有一档「续解时省略中间层」（简洁档）会把它省掉，用户拍板删掉了那一档，
            // 于是中间层**一律照建**，这条用例只剩一个答案。
            Assert.True(OneClickCoordinator.ShouldAddContinuationLevelLayer(
                parentProducedContent: false, siblingCount: 1));
        }

        [Fact]
        public void 父层自己产出了内容物_不另建层_并进父层()
        {
            /*
             * 用户 2026-09-27 真机（改口径的那一条）：父层已经有内容物时，内层包解出来的东西
             * **并进父层那一个目录**，不再用内层包名另造一层 —— 他原话："最后的结果应该和上一层文件
             * 在同一个目录里面的，但是你直接把他放进去了，这个就非常的危险了"
             * （现场：`（T250）经济2\国考资料.txt` 旁边本该是 `老王宣传\…`，我们却造了一层 `Sociology\`）。
             *
             * ⚠ 这条与"中间层省不省"从来是两个维度：它一直都在，删掉简洁档之后更是唯一那条。
             */
            Assert.False(OneClickCoordinator.ShouldAddContinuationLevelLayer(
                parentProducedContent: true, siblingCount: 1));
        }

        [Fact]
        public void 同一层认出多个内层包_照样建层()
        {
            // 一个父任务这一轮认出 2 个以上内层包 = 分支：各自一层，否则几个包的内容物会倒进同一层。
            // 这条压过"父层已出内容物"那条（先命中先返回）—— 几个包挤进同一层更糟。
            Assert.True(OneClickCoordinator.ShouldAddContinuationLevelLayer(
                parentProducedContent: false, siblingCount: 2));

            Assert.True(OneClickCoordinator.ShouldAddContinuationLevelLayer(
                parentProducedContent: true, siblingCount: 5));
        }

        [Fact]
        public void 父子两种分支叠加_结论仍是建_而且只建一层()
        {
            // 两种分支叠加（自己出了内容物 + 认出多个内层包）：结论仍是"建"，而且**只建一层**
            //（这一层的目录一个，几个内层包各自再按自己的包名占一层）。
            Assert.True(OneClickCoordinator.ShouldAddContinuationLevelLayer(
                parentProducedContent: true, siblingCount: 5));
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
        public void 续解落点_层名取不出来时不追加()
        {
            var parent = new ArchiveTask(@"C:\111\222.rar")
            {
                OutputPath = @"C:\111\222",
                ContentDirectoryPath = @"C:\111\222"
            };

            // 层名取不出来（分卷组那种，或被上面那些规则挡下）：落点就是父任务那一层
            // （内容物最后归到这一层下面）。
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

        /// <summary>
        /// **链根最终目录 = 记录下来的内容目录**（2026-10-18 困难批 `11111111` 组合 2 运行期定案）：
        /// 这一单发布过内容物（`ContentFileCount &gt; 0`），而"按链根包名补出来的那一层"盘上不存在
        /// ⇒ 不许拿一个不存在的预测路径去数内容物（那会让链尾补搬数到 0、选了彻底删除也不删源包）。
        /// </summary>
        [Fact]
        public void 链根最终目录_包名层不在盘上_退回记录下来的内容目录()
        {
            string root = Path.Combine(Path.GetTempPath(), "ArchiveFixerChainRoot", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "L2"));
            File.WriteAllText(Path.Combine(root, "L2", "content.bin"), "x");

            try
            {
                var task = new ArchiveTask(Path.Combine(root, "pack.7z"))
                {
                    OutputPath = root,
                    ContentDirectoryPath = root,
                    ContentFileCount = 1
                };

                Assert.Equal(root, OneClickCoordinator.ResolveContinuationOutputDirectory(task));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        /// <summary>反向：包名那一层真的在盘上 ⇒ 照旧补一层（老行为一个字不改）。</summary>
        [Fact]
        public void 链根最终目录_包名层真的在盘上_照旧补一层()
        {
            string root = Path.Combine(Path.GetTempPath(), "ArchiveFixerChainRoot", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "pack"));
            File.WriteAllText(Path.Combine(root, "pack", "content.bin"), "x");

            try
            {
                var task = new ArchiveTask(Path.Combine(root, "pack.7z"))
                {
                    OutputPath = root,
                    ContentDirectoryPath = root,
                    ContentFileCount = 1
                };

                Assert.Equal(
                    Path.Combine(root, "pack"),
                    OneClickCoordinator.ResolveContinuationOutputDirectory(task));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        /// <summary>反向：这一单没发布过内容物（`ContentFileCount = 0`）⇒ 照旧补预测层，不退回。</summary>
        [Fact]
        public void 链根最终目录_这一单没发布过内容物_照旧补预测层()
        {
            string root = Path.Combine(Path.GetTempPath(), "ArchiveFixerChainRoot", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                var task = new ArchiveTask(Path.Combine(root, "pack.7z"))
                {
                    OutputPath = root,
                    ContentDirectoryPath = root,
                    ContentFileCount = 0
                };

                Assert.Equal(
                    Path.Combine(root, "pack"),
                    OneClickCoordinator.ResolveContinuationOutputDirectory(task));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        // ── ④之二 层名被内层包自己那个文件占着（名字被改坏时的物理约束） ──────────


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
