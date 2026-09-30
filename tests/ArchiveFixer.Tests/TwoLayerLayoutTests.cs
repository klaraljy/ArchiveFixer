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
    /// ⛔ **落点最少两层文件夹**（用户 2026-09-30 真机红线）：
    /// 最外层 = 以源包（任务）包名命名的目录；最里层 = 以**最后一个压缩包**那一层命名的目录 ——
    /// **两层都不许省**，判定表里任何"塌缩 / 不套层 / 提上来"的分支都不许吃掉最里层。
    ///
    /// <para>
    /// 现场（`26081118.7z`）：包里只有一个内层包 → 解出 `T 小小绘 推特大合集 330P+454V-9.31G\P|V`，
    /// 定稿却把 `T …` 那一层吃掉，只把 `P`、`V` 摊在 `…\26081118\` 下。丢那一层有**两处**：
    /// </para>
    /// <list type="number">
    /// <item><description><b>递归发布侧</b>：<see cref="ExtractionWorkspace.Publish"/> 把叶子层产物里
    /// "只有一个子目录、没有同级文件"的那一层当"解压器自动加的壳"摊掉了 —— `T …` 在第 1 层发布时
    /// 就已经不在暂存区里了（见「发布」那一组）。</description></item>
    /// <item><description><b>定稿侧</b>：<c>ResultFinalizer.ResolveWrapperName</c> 的
    /// "不套包名层 / 套层名与落点末段同名就不套"两条分支直接返回 null —— 内容物于是直接落进包名目录
    /// （见「定稿」那一组）。</description></item>
    /// </list>
    ///
    /// <para>
    /// 判据只有一处：<see cref="PackageLayerRules"/> 算"最后一个被展开的内层包"，
    /// 定稿侧用它决定最里层叫什么、发布侧用它决定要不要保留叶子层自带的那个文件夹。
    /// ⛔ **没有内层包（只解了一层）时行为逐字不变** —— 那一份口径有很多既有用例钉着。
    /// </para>
    ///
    /// <para>
    /// <b>红检</b>：把 <c>ResultFinalizer.Plan</c> 末尾那道"最里层不许被吃掉"的闸门（连同
    /// <c>ResolveWrapperName</c> 里 `hasInnermostPackage` 那一支）注掉，
    /// 「定稿」与「发布」两组里的守门用例立刻变红（`ContentParentDirectory` 变回包名目录本身）。
    /// 把 <c>RecursiveExtractor.BuildResult</c> 里的 <c>stripLeafWrapper</c> 恢复成默认 true，
    /// 「发布」那一组变红（`T …` 那一层不见了）。
    /// </para>
    /// </summary>
    public class TwoLayerLayoutTests
    {
        private const string Staging = @"C:\work\t1";

        /// <summary>源包包名目录 = 最外层（由落点解析给，定稿一个字都不动它）。</summary>
        private const string SourcePackageDir = @"C:\out\26081118";

        /// <summary>内层包自己产出的那层文件夹（真机现场那个名字）。</summary>
        private const string InnerFolder = "T 小小绘 推特大合集 330P+454V-9.31G";

        private static StagedEntry File(string relativePath, long size = 100)
        {
            return new StagedEntry { RelativePath = relativePath, Size = size };
        }

        private static StagedEntry Dir(string relativePath)
        {
            return new StagedEntry { RelativePath = relativePath, IsDirectory = true };
        }

        private static StagedEntry Artifact(string relativePath, long size = 100)
        {
            return new StagedEntry { RelativePath = relativePath, Size = size, IsProcessArtifact = true };
        }

        private static RecursionLayerReport Layer(string archivePath, bool success = true, int depth = 0)
        {
            return new RecursionLayerReport
            {
                Depth = depth,
                ArchivePath = archivePath,
                Success = success
            };
        }

        // ══════════════════ 判据的唯一出口：最后一个被展开的内层包 ══════════════════

        [Fact]
        public void 内层包判据_只解了一层时没有最里层()
        {
            // 第 0 层 = 任务自己那个包：没有内层包 ⇒ 最里层无从谈起，两处消费方都按老口径走。
            var onlyRoot = new[] { Layer(@"C:\in\26081118.7z") };

            Assert.False(PackageLayerRules.ExpandedInnerPackage(onlyRoot));
            Assert.Equal(string.Empty, PackageLayerRules.ResolveBaseName(onlyRoot));
            Assert.Equal(string.Empty, PackageLayerRules.ResolveArchivePath(onlyRoot));

            // 传 null / 空列表同样判"没有内层包"（单层路径、直读路径都会传 null）。
            Assert.False(PackageLayerRules.ExpandedInnerPackage(null));
            Assert.Equal(string.Empty, PackageLayerRules.ResolveBaseName(Array.Empty<RecursionLayerReport>()));
        }

        [Fact]
        public void 内层包判据_最后一个成功展开的内层包才作数()
        {
            var layers = new[]
            {
                Layer(@"C:\work\1\out\26081118.7z", depth: 0),
                Layer(@"C:\work\1\out\小小绘 推特大合集.7z", depth: 1),

                // 更深那一层失败了：它没产出任何东西，不许当成"最后一个被展开的内层包"。
                Layer(@"C:\work\1\out\坏包.7z", success: false, depth: 2)
            };

            Assert.True(PackageLayerRules.ExpandedInnerPackage(layers));
            Assert.Equal(@"C:\work\1\out\小小绘 推特大合集.7z", PackageLayerRules.ResolveArchivePath(layers));

            // 名字走**唯一**那一个包名取法（OutputPlacement.ResolveArchiveBaseName）：去掉所有后缀。
            Assert.Equal("小小绘 推特大合集", PackageLayerRules.ResolveBaseName(layers));

            // 分卷组取整组基名（复用既有实现，不另写一份取名字规则）。
            Assert.Equal(
                "222",
                PackageLayerRules.ResolveBaseName(new[]
                {
                    Layer(@"C:\work\1\out\outer.7z", depth: 0),
                    Layer(@"C:\work\1\out\222.7z.001", depth: 1)
                }));
        }

        // ══════════════════ ① 内层包产出一层文件夹（T … 形状） ══════════════════

        [Fact]
        public void 定稿_内层包产出一层文件夹_结果不是把PV摊在包名目录下()
        {
            // 现场还原：暂存区里是 T …\P|V（发布那一步已经保住 T …，见下一个用例）。
            FinalizePlan plan = ResultFinalizer.Plan(
                new[]
                {
                    Dir(@"out\" + InnerFolder),
                    Dir(@"out\" + InnerFolder + @"\P"),
                    File(@"out\" + InnerFolder + @"\P\a.jpg"),
                    Dir(@"out\" + InnerFolder + @"\V"),
                    File(@"out\" + InnerFolder + @"\V\b.mp4")
                },
                SourcePackageDir,
                TerminalLayoutMode.KeepLastFolder,
                "26081118",
                "out",
                Staging,
                innermostPackageBaseName: "小小绘 推特大合集");

            Assert.Equal(FinalizeLayoutKind.WrapInFolder, plan.Layout);

            // 最里层 = 内层包**自己产出的那层文件夹**（它就是"最后一个压缩包那一层"）。
            Assert.Equal(InnerFolder, plan.ContentDirectoryName);
            Assert.Equal(SourcePackageDir + @"\" + InnerFolder, plan.ContentParentDirectory);

            // ⛔ 内容物绝不是 SourcePackageDir\P / \V —— 那正是用户说"会导致混乱"的形状。
            Assert.DoesNotContain(plan.Moves, move => move.To == SourcePackageDir + @"\P");
            Assert.DoesNotContain(plan.Moves, move => move.To == SourcePackageDir + @"\V");

            // 整棵 T … 子树一次搬走，P、V 原样待在里面。
            Assert.Contains(
                new PlannedMove(Staging + @"\out\" + InnerFolder, SourcePackageDir + @"\" + InnerFolder),
                plan.Moves);
        }

        [Fact]
        public void 定稿_展开了内层包时单个文件也得待在那一层里面()
        {
            // 判定表 ①（单个文件直放 destDir）也是"省一层"的一支：展开了内层包就不许再直放。
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { File(@"out\movie.mp4") },
                SourcePackageDir,
                TerminalLayoutMode.KeepLastFolder,
                "26081118",
                "out",
                Staging,
                innermostPackageBaseName: "内层包");

            Assert.Equal(FinalizeLayoutKind.WrapInFolder, plan.Layout);
            Assert.Equal(@"内层包", plan.ContentDirectoryName);
            Assert.Equal(SourcePackageDir + @"\内层包\movie.mp4", plan.Moves[0].To);
        }

        // ══════════════════ ② 内层包名与源包包名相同 ══════════════════

        [Fact]
        public void 定稿_内层包名与源包包名相同_仍然两层()
        {
            // 内层包 `26081118.7z` 的内容直接摊在它自己的根上（没有自带文件夹）：
            // 就地替换留下的那一层名字**只能**取内层包的包基名 —— 与 destDir 末段同名也**照建**
            // （用户宁可多一层，也不要内容物摊平）。
            //
            // ⚠ 暂存树按**就地替换之后**的真实形状给（用户 2026-09-30 中午）：
            // 工作区发布已经在原位置留下了 `<包基名>\`，所以这里是 `out\26081118\{P,V}`
            // 而不是"内容直接摊在 out 上"。
            FinalizePlan plan = ResultFinalizer.Plan(
                new[]
                {
                    Dir(@"out\26081118"),
                    Dir(@"out\26081118\P"),
                    File(@"out\26081118\P\a.jpg"),
                    Dir(@"out\26081118\V"),
                    File(@"out\26081118\V\b.mp4")
                },
                SourcePackageDir,
                TerminalLayoutMode.KeepLastFolder,
                "26081118",
                "out",
                Staging,
                innermostPackageBaseName: "26081118");

            Assert.Equal(FinalizeLayoutKind.WrapInFolder, plan.Layout);
            Assert.Equal("26081118", plan.ContentDirectoryName);
            Assert.Equal(SourcePackageDir + @"\26081118", plan.ContentParentDirectory);

            // 整棵 `26081118` 子树一次搬走（P、V 原样待在它里面）。
            Assert.Single(plan.Moves);
            Assert.Equal(Staging + @"\out\26081118", plan.Moves[0].From);
            Assert.Equal(SourcePackageDir + @"\26081118", plan.Moves[0].To);

            // 两层：源包包名目录 + 最里层（两层同名也照建）。
            Assert.NotEqual(SourcePackageDir, plan.ContentParentDirectory);
        }

        // ══════════════════ ③ 没有内层包（只解了一层）→ 与现在逐字相同 ══════════════════
        //
        // 这一组刻意**照抄既有用例的期望值**（ResultFinalizerTests / PlacementAndFinalizer 那几条），
        // 只是把"没有内层包"这一个事实显式传进去：它们必须一个字符都不变。

        [Fact]
        public void 无内层包_归档根上多个文件夹_照旧用包基名套一层()
        {
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { Dir(@"out\a"), Dir(@"out\b"), File(@"out\a\x.mp4"), File(@"out\b\y.mp4") },
                @"C:\111",
                TerminalLayoutMode.KeepLastFolder,
                "222",
                "out",
                Staging,
                innermostPackageBaseName: null);

            Assert.Equal(FinalizeLayoutKind.WrapInFolder, plan.Layout);
            Assert.Equal("222", plan.ContentDirectoryName);
            Assert.Equal(@"C:\111\222", plan.ContentParentDirectory);
            Assert.Equal(@"C:\111\222\a", plan.Moves[0].To);
            Assert.Equal(@"C:\111\222\b", plan.Moves[1].To);
        }

        [Fact]
        public void 无内层包_自带文件夹与落点末段同名_照旧不再叠一层()
        {
            // 既有用例 `PlacementAndFinalizer_同名同目录不再塌缩_落点就是包名那一层` 的口径：
            // 归档自带的那一层与包名同名时不再叠一次（`…\名字\名字\名字` 是用户反复抱怨的重复层）。
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { File(@"out\名字\a.mp4"), File(@"out\名字\b.mp4") },
                @"C:\111\222\名字\名字",
                TerminalLayoutMode.KeepLastFolder,
                "名字",
                "out",
                Staging,
                innermostPackageBaseName: null);

            Assert.Equal(@"C:\111\222\名字\名字", plan.ContentParentDirectory);
            Assert.Equal(@"C:\111\222\名字\名字\a.mp4", plan.Moves[0].To);
        }

        [Fact]
        public void 无内层包_单个文件_照旧直放且没有套层()
        {
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { File(@"out\666.MP4") },
                @"C:\111\222",
                TerminalLayoutMode.KeepLastFolder,
                "222",
                "out",
                Staging,
                innermostPackageBaseName: null);

            Assert.Equal(FinalizeLayoutKind.SingleFileToDestination, plan.Layout);
            Assert.Equal(string.Empty, plan.ContentDirectoryName);
            Assert.Equal(@"C:\111\222\666.MP4", plan.Moves[0].To);
        }

        [Fact]
        public void 无内层包_没有归档基名_照旧不硬造重复层并写提醒()
        {
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { File(@"out\a.mp4"), File(@"out\b.mp4") },
                @"C:\out\222",
                stagingRoot: Staging,
                contentRoot: "out",
                innermostPackageBaseName: null);

            Assert.Equal(FinalizeLayoutKind.WrapInFolder, plan.Layout);
            Assert.Equal(@"C:\out\222", plan.ContentParentDirectory);
            Assert.Equal(@"C:\out\222\a.mp4", plan.Moves[0].To);
            Assert.NotEmpty(plan.Warnings);
        }

        // ══════════════════ ④ suppressPackageFolderLayer 三种成因下最里层依然存在 ══════════════════
        //
        // 那三条分支（不套包名层 / 续解层已在路径里 / 过程物名不成层）**只免掉"包名那一层"**，
        // ⛔ 绝不许顺手把"最后一个压缩包那一层"也免掉。判据在调用方（ExtractionCoordinator），
        // 定稿这一侧只认这一个布尔 —— 这里逐个成因验它。

        [Theory]
        [InlineData("手动档「解压到当前文件夹」")]
        [InlineData("续解的那一层已经在落点路径里")]
        [InlineData("这一层本身就是分卷组的一卷（过程物名不成层）")]
        public void 不套包名层时_展开了内层包最里层依然存在(string cause)
        {
            // 三种成因在定稿这一侧的输入**完全一样**（一个布尔），区别只在日志里怎么说；
            // 这里用三个名字把"每一种成因都不许吃掉最里层"分别钉住（Theory 名 = 成因）。
            Assert.False(string.IsNullOrWhiteSpace(cause));

            // ⚠ 暂存树按**就地替换之后**的真实形状给：工作区发布已经在原位置留下了 `inner\`
            // （内层包的内容直接摊在它自己的根上时，那一层就是包基名），所以是 `out\inner\{P,V}`。
            FinalizePlan plan = ResultFinalizer.Plan(
                new[]
                {
                    Dir(@"out\inner"),
                    Dir(@"out\inner\P"),
                    File(@"out\inner\P\a.jpg"),
                    Dir(@"out\inner\V"),
                    File(@"out\inner\V\b.mp4")
                },
                @"C:\111\222\666",
                TerminalLayoutMode.KeepLastFolder,
                "666",
                "out",
                Staging,
                suppressPackageFolderLayer: true,
                innermostPackageBaseName: "inner");

            Assert.Equal("inner", plan.ContentDirectoryName);
            Assert.Equal(@"C:\111\222\666\inner", plan.ContentParentDirectory);

            // 整棵 `inner` 子树一次搬走（P、V 原样待在它里面），所以落点是那个文件夹本身。
            Assert.Single(plan.Moves);
            Assert.Equal(Staging + @"\out\inner", plan.Moves[0].From);
            Assert.Equal(@"C:\111\222\666\inner", plan.Moves[0].To);
        }

        [Fact]
        public void 不套包名层时_内层包自带文件夹_照旧保留那一层()
        {
            // 成因之一（手动档「解压到当前文件夹」）的既有口径：按包内原样解开，归档自带的那一层保留
            // （整棵子树一次搬走，所以落点是那个文件夹本身）。
            // 展开了内层包之后**最里层照样是它** —— 不是"少一层"，而是"提上来的这一个就是最里层"。
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { File(@"out\666\a.mp4") },
                @"C:\111",
                TerminalLayoutMode.KeepLastFolder,
                "666",
                "out",
                Staging,
                suppressPackageFolderLayer: true,
                innermostPackageBaseName: "666");

            Assert.Equal("666", plan.ContentDirectoryName);
            Assert.Equal(@"C:\111\666", plan.ContentParentDirectory);
            Assert.Equal(@"C:\111\666", plan.Moves[0].To);
            Assert.Equal(Staging + @"\out\666", plan.Moves[0].From);
        }

        [Fact]
        public void 内层包自己产出的那层文件夹成为最里层_更深的链待在它里面()
        {
            // ⛔ 取"最里层"名字时用的是链上**最外层**那一个（= 内层包自己产出的那层文件夹），
            // 不是最深的那一个：包内是 `T …\P\a.jpg` 这种单文件夹链时，
            // 取最深的会得到 `包名目录\P\a.jpg` —— `P` 这种**归档内部子文件夹**直接躺在包名目录下，
            // 正是用户点名不许出现的形状。
            FinalizePlan plan = ResultFinalizer.Plan(
                new[]
                {
                    Dir(@"out\T 内层包产出"),
                    Dir(@"out\T 内层包产出\P"),
                    File(@"out\T 内层包产出\P\a.jpg")
                },
                SourcePackageDir,
                TerminalLayoutMode.KeepLastFolder,
                "26081118",
                "out",
                Staging,
                innermostPackageBaseName: "内层包");

            Assert.Equal("T 内层包产出", plan.ContentDirectoryName);
            Assert.Equal(SourcePackageDir + @"\T 内层包产出", plan.ContentParentDirectory);
            Assert.Equal(Staging + @"\out\T 内层包产出", plan.Moves[0].From);

            // ⛔ 绝不是 SourcePackageDir\P（归档内部子文件夹直接躺在包名目录下）。
            Assert.NotEqual(SourcePackageDir + @"\P", plan.ContentParentDirectory);
        }

        [Fact]
        public void 不套包名层且没有内层包_照旧一层都不套()
        {
            // ⛔ 边界：没有内层包时这一档**一个字都不改**（手动档「解压到当前文件夹」的本意就是
            // "内容物直接落进源包那一层"，这时候没有"最后一个压缩包那一层"可谈）。
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { File(@"out\a.mp4"), File(@"out\b.mp4") },
                @"C:\111",
                TerminalLayoutMode.KeepLastFolder,
                "111",
                "out",
                Staging,
                suppressPackageFolderLayer: true,
                innermostPackageBaseName: null);

            Assert.Equal(@"C:\111", plan.ContentParentDirectory);
            Assert.Equal(@"C:\111\a.mp4", plan.Moves[0].To);
        }

        [Fact]
        public void 特定解压例外档_不许把最里层也塌掉_并且如实记没生效()
        {
            // 特定解压（规格 §3.5）本来会把"要套的那一层"去掉（`222\1111\内容物`）。包里还有内层包时
            // 那一层正是最里层 ⇒ 红线优先，并且**绝不静默**（写 WARN 说清为什么、结论也不再报"规则已生效"）。
            var plan = ResultFinalizer.Plan(
                new[] { File(@"out\666\a.mp4") },
                @"C:\111\222",
                TerminalLayoutMode.KeepLastFolder,
                "222",
                "out",
                Staging,
                specialExtraction: SpecialExtractionPlan.FromSettings(new AppSettings
                {
                    UseSpecialExtraction = true,
                    SpecialExtractionRules = new List<string> { SpecialExtractionRules.SingleContentLayer }
                }),
                innermostPackageBaseName: "inner");

            Assert.False(plan.SpecialExtractionApplied);
            Assert.NotEqual(@"C:\111\222", plan.ContentParentDirectory);
            Assert.Equal(@"C:\111\222\666", plan.ContentParentDirectory);
            Assert.Equal(@"C:\111\222\666", plan.Moves[0].To);
            Assert.Contains(plan.Warnings, warning => warning.Contains("没有生效", StringComparison.Ordinal));
        }

        [Fact]
        public void 只出了其余物时_不硬造最里层()
        {
            // ⛔ 边界：暂存区里**只有其余物**（没有内容物）时不定稿、也不该凭空报出一层"内容物层"——
            // 那一层的名字会被其余物目录取名那一支读到（撞名判断），凭空多一个名字只会让落点漂移。
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { Artifact(@"out\inner.7z") },
                @"C:\111\222",
                TerminalLayoutMode.KeepLastFolder,
                "222",
                "out",
                Staging,
                innermostPackageBaseName: "inner");

            Assert.Equal(FinalizeLayoutKind.ProcessArtifactsOnly, plan.Layout);
            Assert.Equal(string.Empty, plan.ContentDirectoryName);
            Assert.Empty(plan.ContentMoves);
            Assert.Equal(@"C:\111\222\其余物", plan.ProcessArtifactDirectory);
        }

        // ══════════════════ 发布侧：内层包自己产出的那层文件夹不许被摊掉 ══════════════════
        //
        // 现场丢 `T …` 的真凶在这里：定稿侧那两条分支只是"补刀"——`T …` 在发布那一步
        // 就已经当"解压器自动加的壳"被摊掉了，暂存区里只剩 `P`、`V`。

        [Fact]
        public void 发布_展开了内层包时不摊掉内层包自己产出的那层文件夹()
        {
            string root = NewTempRoot();

            try
            {
                var workspace = new ExtractionWorkspace(Path.Combine(root, "ws"), "two-layer");
                WorkspaceLayer layer = workspace.CreateNextLayer(Path.Combine(root, "inner.7z"));

                string inner = Path.Combine(layer.OutputPath, InnerFolder);
                Directory.CreateDirectory(Path.Combine(inner, "P"));
                Directory.CreateDirectory(Path.Combine(inner, "V"));
                System.IO.File.WriteAllText(Path.Combine(inner, "P", "a.jpg"), "P");
                System.IO.File.WriteAllText(Path.Combine(inner, "V", "b.mp4"), "V");

                string target = Path.Combine(root, "stage");

                WorkspacePublishResult published = workspace.Publish(target, inPlaceInnerPackages: true);

                Assert.True(published.Success, published.Message);

                // `T …` 那一层必须还在暂存区里（它就是"最里层"），P、V 待在它里面。
                Assert.True(Directory.Exists(Path.Combine(target, InnerFolder)), published.Message);
                Assert.True(System.IO.File.Exists(Path.Combine(target, InnerFolder, "P", "a.jpg")));
                Assert.True(System.IO.File.Exists(Path.Combine(target, InnerFolder, "V", "b.mp4")));

                // ⛔ 老形状（P、V 直接躺在暂存根上）不许再出现。
                Assert.False(Directory.Exists(Path.Combine(target, "P")), "P 不许被摊到暂存根上");
                Assert.False(Directory.Exists(Path.Combine(target, "V")), "V 不许被摊到暂存根上");
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void 发布_只解了一层时照旧摊掉无意义外壳()
        {
            // ⛔ 边界（既有用例 `工作区_Publish去掉一层无意义外壳` 的同一口径）：
            // 没有内层包 ⇒ 照旧摊，一个字都不改。
            string root = NewTempRoot();

            try
            {
                var workspace = new ExtractionWorkspace(Path.Combine(root, "ws"), "one-layer");
                WorkspaceLayer layer = workspace.CreateNextLayer(Path.Combine(root, "pack.zip"));

                string shell = Path.Combine(layer.OutputPath, "pack");
                Directory.CreateDirectory(shell);
                System.IO.File.WriteAllText(Path.Combine(shell, "a.txt"), "内容");

                WorkspacePublishResult published = workspace.Publish(Path.Combine(root, "stage"));

                Assert.True(published.Success, published.Message);
                Assert.True(System.IO.File.Exists(Path.Combine(root, "stage", "a.txt")), published.Message);
                Assert.False(Directory.Exists(Path.Combine(root, "stage", "pack")), "无意义外壳应当被去掉");
            }
            finally
            {
                TryDelete(root);
            }
        }

        // ══════════════════ 端到端形状：落点解析 + 定稿 ══════════════════

        [Fact]
        public void 落点解析加定稿_源包包名目录里必有一层()
        {
            // 111\222\26081118.7z → 落点 = 111\222\26081118（最外层），定稿再套最里层。
            OutputPlacementResult placement = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\26081118.7z",
                OutputPlacementMode.PerArchiveSubfolder);

            FinalizePlan plan = ResultFinalizer.Plan(
                new[]
                {
                    Dir(@"out\" + InnerFolder),
                    Dir(@"out\" + InnerFolder + @"\P"),
                    File(@"out\" + InnerFolder + @"\P\a.jpg")
                },
                placement.DestinationDirectory,
                TerminalLayoutMode.KeepLastFolder,
                placement.ArchiveBaseName,
                "out",
                Staging,
                innermostPackageBaseName: "小小绘 推特大合集");

            Assert.Equal(@"C:\111\222\26081118", placement.DestinationDirectory);
            Assert.Equal(@"C:\111\222\26081118\" + InnerFolder, plan.ContentParentDirectory);

            // 最外层（包名目录）与最里层是**两层**，谁都没省。
            Assert.NotEqual(plan.DestinationDirectory, plan.ContentParentDirectory);
            Assert.StartsWith(plan.DestinationDirectory + @"\", plan.ContentParentDirectory, StringComparison.Ordinal);
        }

        private static string NewTempRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "ArchiveFixer-two-layer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void TryDelete(string directory)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }
    }
}
