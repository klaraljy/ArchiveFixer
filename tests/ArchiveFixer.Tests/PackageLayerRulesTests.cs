using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 三条**层规则**的唯一出口 <see cref="PackageLayerRules"/> 的落点用例（用户 2026-09-30 中午定）。
    ///
    /// <list type="number">
    /// <item><description><b>层的来源是"内层压缩包"</b>，不是"文件夹看着像不像多余的壳"：
    /// ⛔ 绝不许拿"这一层只有一个子文件夹"当理由摊平普通文件夹
    /// （用户原话："如果是一个文件夹 <c>1111</c> 里面包裹真正的内容物，这个时候你就会把 <c>1111</c> 省略，
    /// 这是非常大忌。1111 只是一个文件夹名字，我们不能去假设原打包人的逻辑"）。</description></item>
    /// <item><description><b>第一层（源包包名目录）与最后一层（最后一个内层包那一层）永不可省</b>，
    /// 只能省中间层（②页「续解时省略中间层」）。</description></item>
    /// <item><description><b>就地替换</b>：任何"按内容认出是压缩包"的文件在**原来的位置**解开，
    /// 留下一个以它命名的文件夹（去掉假后缀的基名）放内容物；真文件原地不动。</description></item>
    /// </list>
    ///
    /// <para>
    /// 本文件用**真目录**跑 <see cref="ExtractionWorkspace.Publish"/>（就地替换那一档），
    /// 再把发布出来的树喂给 <see cref="ResultFinalizer.Plan"/>，最后把移动计划**投影成整棵树**
    /// 逐字对照 —— 判据链是"发布侧 + 定稿侧"合起来的结果，不是某一侧的中间值。
    /// </para>
    ///
    /// <para>
    /// <b>红检</b>（把判据临时撤掉，亲眼看到用例变红；接手代理 2026-09-30 逐条实测，失败原文照抄）：
    /// ① <c>ResultFinalizer</c> 里"展开了内层包 ⇒ 要套的那一层取链上**最外层**那一个"那一支
    /// （<c>wrapperChainNode = hasInnermostPackage ? shape.Chain[0] : shape.Chain[^1]</c>）
    /// 改回只看 <c>shape.Chain[^1]</c> ⇒ <c>普通文件夹_1111里面包着真内容物时不许把1111摊平</c> 变红：
    /// <c>Assert.Equal() Failure: Strings differ　Expected: "内层包"　Actual: "真内容"</c>
    /// —— 即 `内层包\1111\真内容` 被一路摊掉，`1111` 这个名字直接消失（连带 5 条变红）。
    /// ② <see cref="PackageLayerRules.ShouldKeepLayerFolder"/> 里 <c>!hasChildLayer</c> 那一条去掉
    /// ⇒ <c>五层链_省中间_只留首尾</c> 变红（末层 5555555 被省掉，发布树塌成 <c>内容物\payload.bin</c>）
    /// 与 <c>唯一出口_末层永不省</c> 变红。
    /// ③ 把 <c>RecursiveExtractor.BuildResult</c> 的 <c>inPlace</c> 判据关掉（回到"把叶子层产物摊到发布目标根上"
    /// 的老口径）⇒ <c>RecursiveExtractorTests.就地替换_用户极端例子的整棵树逐字成立</c> 变红：
    /// 产物只剩顶层七个 <c>*.bin</c>，`1.mp4/2.mp4/3.mp4` 与所有层**整个不见了**。
    /// ④ 把 <c>ExtractionWorkspace.DeleteConsumedVolumeSiblings</c> 那一行撤掉 ⇒
    /// <c>分卷组_就地替换后产物里不许留下半套卷</c> 变红：产物里留下缺首卷的 <c>Y.7z.002</c>，
    /// 定稿那侧直接判 <c>这一层里有分卷组不完整：Y.7z.001… 缺首卷（Y.7z.001）。已按「什么都不动」处理</c>。
    /// </para>
    /// <para>
    /// ⚠ 别把 <c>DecideLayout</c> 里那条 <c>hasInnermostPackage → WrapInFolder</c> 提前返回
    /// 当成"普通文件夹不许摊平"的判据：实测撤掉它只让
    /// <c>TwoLayerLayoutTests.定稿_展开了内层包时单个文件也得待在那一层里面</c> 变红
    /// （链形状的结论由 ① 那支决定，提前返回对它没有影响）。
    /// </para>
    /// </summary>
    public class PackageLayerRulesTests
    {
        private const string Staging = @"C:\work\t1";

        // ═════════════════ ① 用户极端例子：整棵树逐字对照 ═════════════════
        //
        // 用户给的那棵树（必须逐字成立）：
        //   AAA/  ├── 1.mp4 2.mp4 3.mp4                       # 真文件，原地不动
        //         ├── DDDD.mp4 EEEE.mp4 FFFF.mp4（本质是包）  → DDDD\内容物、EEEE\内容物、FFFF\内容物
        //         ├── BBBB/  ├── CCCCC.mp4 → BBBB\CCCCC\内容物
        //         │          └── DDDDD.mp4 → BBBB\DDDDD\内容物
        //         └── CCCC/  ├── EEEEE.mp4 → CCCC\EEEEE\内容物
        //                    └── DDDDD/ └── EEEEEE.mp4 → CCCC\DDDDD\EEEEEE\内容物

        [Fact]
        public void 就地替换_用户极端例子的整棵树逐字成立()
        {
            string root = NewTempRoot();

            try
            {
                var workspace = new ExtractionWorkspace(Path.Combine(root, "ws"), "extreme");
                WorkspaceLayer outer = workspace.CreateNextLayer(Path.Combine(root, "AAA.rar"));

                // 第 0 层产物：真文件 + 七个"本质是压缩包"的伪装文件（其中五个在子文件夹里）。
                WriteFile(outer.OutputPath, "1.mp4");
                WriteFile(outer.OutputPath, "2.mp4");
                WriteFile(outer.OutputPath, "3.mp4");
                WriteFile(outer.OutputPath, "DDDD.mp4");
                WriteFile(outer.OutputPath, "EEEE.mp4");
                WriteFile(outer.OutputPath, "FFFF.mp4");
                WriteFile(outer.OutputPath, @"BBBB\CCCCC.mp4");
                WriteFile(outer.OutputPath, @"BBBB\DDDDD.mp4");
                WriteFile(outer.OutputPath, @"CCCC\EEEEE.mp4");
                WriteFile(outer.OutputPath, @"CCCC\DDDDD\EEEEEE.mp4");

                // 七个内层包各解出一层：内容物都放在自己那个 `内容物\` 里。
                Extract(workspace, outer, "DDDD.mp4", "DDDD");
                Extract(workspace, outer, "EEEE.mp4", "EEEE");
                Extract(workspace, outer, "FFFF.mp4", "FFFF");
                Extract(workspace, outer, @"BBBB\CCCCC.mp4", "CCCCC");
                Extract(workspace, outer, @"BBBB\DDDDD.mp4", "DDDDD");
                Extract(workspace, outer, @"CCCC\EEEEE.mp4", "EEEEE");
                Extract(workspace, outer, @"CCCC\DDDDD\EEEEEE.mp4", "EEEEEE");

                string target = Path.Combine(root, "stage");
                WorkspacePublishResult published = workspace.Publish(target, inPlaceInnerPackages: true);

                Assert.True(published.Success, published.Message);

                // ── 发布侧整棵树逐字对照（= 用户那棵树里 `AAA\` 底下的全部内容）──
                Assert.Equal(
                    new[]
                    {
                        "1.mp4",
                        "2.mp4",
                        "3.mp4",
                        @"BBBB",
                        @"BBBB\CCCCC",
                        @"BBBB\CCCCC\内容物",
                        @"BBBB\CCCCC\内容物\CCCCC.bin",
                        @"BBBB\DDDDD",
                        @"BBBB\DDDDD\内容物",
                        @"BBBB\DDDDD\内容物\DDDDD.bin",
                        @"CCCC",
                        @"CCCC\DDDDD",
                        @"CCCC\DDDDD\EEEEEE",
                        @"CCCC\DDDDD\EEEEEE\内容物",
                        @"CCCC\DDDDD\EEEEEE\内容物\EEEEEE.bin",
                        @"CCCC\EEEEE",
                        @"CCCC\EEEEE\内容物",
                        @"CCCC\EEEEE\内容物\EEEEE.bin",
                        @"DDDD",
                        @"DDDD\内容物",
                        @"DDDD\内容物\DDDD.bin",
                        @"EEEE",
                        @"EEEE\内容物",
                        @"EEEE\内容物\EEEE.bin",
                        @"FFFF",
                        @"FFFF\内容物",
                        @"FFFF\内容物\FFFF.bin"
                    },
                    Tree(target));

                // ⛔ 被解开的包文件一个都不许留在产物里（位置让给同名目录）。
                foreach (string consumed in new[]
                         {
                             "DDDD.mp4", "EEEE.mp4", "FFFF.mp4",
                             @"BBBB\CCCCC.mp4", @"BBBB\DDDDD.mp4",
                             @"CCCC\EEEEE.mp4", @"CCCC\DDDDD\EEEEEE.mp4"
                         })
                {
                    Assert.False(File.Exists(Path.Combine(target, consumed)), consumed + " 应该已经被就地替换成目录");
                }

                // ── 定稿侧：`destDir` = 源包包名目录 `AAA\`，里面**原样**保留上面那棵树 ──
                string destDir = Path.Combine(root, "out", "AAA");

                FinalizePlan plan = ResultFinalizer.Plan(
                    Snapshot(target),
                    destDir,
                    TerminalLayoutMode.KeepLastFolder,
                    "AAA",
                    stagingRoot: target);

                // 首层（包名目录）与末层（最后一个包那一层）都不另造、也不吃掉任何一层：
                // 内容物根上那八个条目原样搬进 `AAA\`。
                Assert.Equal(string.Empty, plan.ContentDirectoryName);
                Assert.Equal(destDir, plan.ContentParentDirectory);

                Assert.Equal(Tree(target), ProjectFinalTree(plan, destDir));
            }
            finally
            {
                TryDelete(root);
            }
        }

        // ═════════════════ ② 普通文件夹一律保留（1111 不许被摊平）═════════════════

        [Fact]
        public void 普通文件夹_1111里面包着真内容物时不许把1111摊平()
        {
            // 展开了内层包 ⇒ 就地替换留下的那一层在树里（这里 `内层包\`），判据是**事实**：
            // 树上每一层都有出处，"这一层只有一个子文件夹"不再是去掉它的理由。
            //
            // 老口径（判定表 ④ 单链塌缩）会把 `1111` 一路塌到最深那个文件夹名，
            // 于是用户的 `1111` 直接消失 —— 用户原话："这是非常大忌"。
            FinalizePlan plan = ResultFinalizer.Plan(
                new[]
                {
                    Dir(@"out\内层包"),
                    Dir(@"out\内层包\1111"),
                    Dir(@"out\内层包\1111\真内容"),
                    StagedFile(@"out\内层包\1111\真内容\a.mp4")
                },
                @"C:\out\AAA",
                TerminalLayoutMode.KeepLastFolder,
                "AAA",
                "out",
                Staging,
                innermostPackageBaseName: "内层包");

            Assert.Equal("内层包", plan.ContentDirectoryName);
            Assert.Equal(@"C:\out\AAA\内层包", plan.ContentParentDirectory);

            // 整棵子树一次搬走：`1111\真内容\a.mp4` 原样待在它里面。
            Assert.Equal(Staging + @"\out\内层包", plan.Moves[0].From);
            Assert.Equal(@"C:\out\AAA\内层包", plan.Moves[0].To);

            // ⛔ 绝不是 `C:\out\AAA\真内容`（把 `1111` 摊掉），也不是 `C:\out\AAA\1111`（把 `内层包` 摊掉）。
            Assert.DoesNotContain(plan.Moves, move => move.To == @"C:\out\AAA\真内容");
            Assert.DoesNotContain(plan.Moves, move => move.To == @"C:\out\AAA\1111");
        }

        [Fact]
        public void 普通文件夹_发布侧只有一个子文件夹也不摊平()
        {
            // 发布侧同一条规则：老口径 `ResolveContentRoot` 会把"只有一个子目录、没有同级文件"
            // 的那几层当"解压器自动加的壳"一路摊掉（`out\内层包\1111\真内容` → `真内容`）。
            // 就地替换那一档**一层都不摊**。
            string root = NewTempRoot();

            try
            {
                var workspace = new ExtractionWorkspace(Path.Combine(root, "ws"), "plain-folder");
                WorkspaceLayer outer = workspace.CreateNextLayer(Path.Combine(root, "AAA.rar"));

                WriteFile(outer.OutputPath, "1111\\真内容\\a.mp4");
                WriteFile(outer.OutputPath, "inner.7z");

                Extract(workspace, outer, "inner.7z", "inner");

                string target = Path.Combine(root, "stage");
                WorkspacePublishResult published = workspace.Publish(target, inPlaceInnerPackages: true);

                Assert.True(published.Success, published.Message);

                // `1111` 与它下面那层 `真内容` 都在；一个都没被当成"无意义外壳"摊掉。
                Assert.True(File.Exists(Path.Combine(target, "1111", "真内容", "a.mp4")), published.Message);
                Assert.False(File.Exists(Path.Combine(target, "a.mp4")), "`1111\\真内容` 两层都不许被摊平");
                Assert.False(Directory.Exists(Path.Combine(target, "真内容")), "`真内容` 不许被提上来当壳");
            }
            finally
            {
                TryDelete(root);
            }
        }

        // ═════════════════ ③ 五层链：省中间 / 不省 ═════════════════
        //
        // `111 → 2222 → 33333 → 444444 → 5555555`：
        //   不省   = `111\2222\33333\444444\5555555\内容物`
        //   省中间 = `111\5555555\内容物`

        [Fact]
        public void 五层链_不省_五层都在()
        {
            (IReadOnlyList<string> published, IReadOnlyList<string> final) = RunChain(omitMiddleLayers: false);

            Assert.True(
                new[]
                {
                    @"2222",
                    @"2222\33333",
                    @"2222\33333\444444",
                    @"2222\33333\444444\5555555",
                    @"2222\33333\444444\5555555\内容物",
                    @"2222\33333\444444\5555555\内容物\payload.bin"
                }.SequenceEqual(final),
                "不省档应当是五层都在。发布树：" + string.Join(" | ", published)
                + "　最终树：" + string.Join(" | ", final));
        }

        [Fact]
        public void 五层链_省中间_只留首尾()
        {
            // 中间三层（2222 / 33333 / 444444）都是"只装着下一个包、自己没有内容物"的干净过路层 ⇒ 省掉；
            // **末层 5555555 绝不省**（用户 2026-09-30 红线：只能省中间层）。
            (IReadOnlyList<string> published, IReadOnlyList<string> final) = RunChain(omitMiddleLayers: true);

            Assert.True(
                new[] { @"5555555", @"5555555\内容物", @"5555555\内容物\payload.bin" }.SequenceEqual(final),
                "省中间档应当只留末层那一层。发布树：" + string.Join(" | ", published)
                + "　最终树：" + string.Join(" | ", final));
        }

        // ═════════════════ ④ 首层 / 末层永不省 ═════════════════

        [Fact]
        public void 末层永不省_内层包名与源包包名相同也照建()
        {
            // `111\111.7z`：内层包的包基名与源包包名同名 —— 那一层**照建**（用户宁可多一层）。
            var chain = new List<RecursionLayerReport>
            {
                new() { Depth = 0, ArchivePath = @"C:\in\111.7z", Success = true },
                new() { Depth = 1, ArchivePath = @"C:\ws\layer-000\output\111.7z", Success = true }
            };

            Assert.Equal("111", PackageLayerRules.ResolveBaseName(chain));
            Assert.True(PackageLayerRules.ShouldKeepLayerFolder(false, parentProducedContent: false, siblingCount: 1, hasChildLayer: false));

            FinalizePlan plan = ResultFinalizer.Plan(
                new[]
                {
                    Dir(@"out\111"),
                    Dir(@"out\111\内容物"),
                    StagedFile(@"out\111\内容物\a.mp4")
                },
                @"C:\out\111",
                TerminalLayoutMode.KeepLastFolder,
                "111",
                "out",
                Staging,
                innermostPackageBaseName: "111");

            // 首层 `C:\out\111`（包名目录）+ 末层 `111` = 两层，同名的两层都照建。
            Assert.Equal("111", plan.ContentDirectoryName);
            Assert.Equal(@"C:\out\111\111", plan.ContentParentDirectory);
            Assert.NotEqual(plan.DestinationDirectory, plan.ContentParentDirectory);
        }

        [Fact]
        public void 首层永不省_内容物直接摊在源包根上时宁可多一层()
        {
            // 内层包的内容**直接摊在它自己的根上**（没有自带文件夹）：就地替换那一层用**包基名**建，
            // 与 destDir 末段同名也照建 —— 绝不让内容物直接躺在源包包名目录下。
            var chain = new List<RecursionLayerReport>
            {
                new() { Depth = 0, ArchivePath = @"C:\in\222.7z", Success = true },
                new() { Depth = 1, ArchivePath = @"C:\ws\layer-000\output\inner.mp4", Success = true }
            };

            FinalizePlan plan = ResultFinalizer.Plan(
                new[]
                {
                    Dir(@"out\inner"),
                    StagedFile(@"out\inner\a.mp4"),
                    StagedFile(@"out\inner\b.mp4")
                },
                @"C:\out\222",
                TerminalLayoutMode.KeepLastFolder,
                "222",
                "out",
                Staging,
                innermostPackageBaseName: PackageLayerRules.ResolveBaseName(chain));

            Assert.Equal("inner", plan.ContentDirectoryName);
            Assert.Equal(@"C:\out\222\inner", plan.ContentParentDirectory);
        }

        // ═════════════════ ⑤ 只有一层、无内层包 → 既有行为逐字不变 ═════════════════
        //
        // 期望值**照抄既有用例**（ResultFinalizerTests 的判定表 1/2/3/4），只是把"没有内层包"
        // 这一个事实显式传进去：它们必须一个字符都不变。

        [Fact]
        public void 无内层包_单链照旧塌缩到最深那个文件夹名()
        {
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { StagedFile(@"out\666\777\a.mp4"), StagedFile(@"out\666\777\b.mp4") },
                @"C:\out\222",
                TerminalLayoutMode.KeepLastFolder,
                "222",
                "out",
                Staging,
                innermostPackageBaseName: null);

            Assert.Equal(FinalizeLayoutKind.CollapseSingleChain, plan.Layout);
            Assert.Equal("777", plan.ContentDirectoryName);
            Assert.Equal(@"C:\out\222\777", plan.ContentParentDirectory);
            Assert.Equal((Staging + @"\out\666\777", @"C:\out\222\777"), (plan.Moves[0].From, plan.Moves[0].To));
        }

        [Fact]
        public void 无内层包_多重空目录照旧提上最后那个有意义的文件夹()
        {
            FinalizePlan plan = ResultFinalizer.Plan(
                new[]
                {
                    Dir(@"out\empty1"),
                    Dir(@"out\999"),
                    StagedFile(@"out\999\a.mp4"),
                    StagedFile(@"out\999\b.mp4")
                },
                @"C:\out\222",
                TerminalLayoutMode.KeepLastFolder,
                "222",
                "out",
                Staging,
                innermostPackageBaseName: null);

            Assert.Equal(FinalizeLayoutKind.PromoteInnermostFolder, plan.Layout);
            Assert.Equal(@"C:\out\222\999", plan.ContentParentDirectory);
        }

        /// <summary>
        /// ⛔ **只解了一层时发布侧也不摊外壳**（2026-10-02 真机修，与"就地替换"那一档对齐）：
        /// 归档自带的文件夹原样保留 —— 摊平它是用户的红线（"这是非常大忌"），
        /// 而且真机 <c>风景01.7z.001</c> 就是被这一步吃掉了 <c>风景\</c> 这一层。
        /// 摊不摊由唯一出口 <c>ResultFinalizer</c> 决定（同名套娃由它自己的"同名不套层"收掉）。
        /// </summary>
        [Fact]
        public void 无内层包_发布侧也不摊外壳_文件夹原样保留()
        {
            string root = NewTempRoot();

            try
            {
                var workspace = new ExtractionWorkspace(Path.Combine(root, "ws"), "one-layer");
                WorkspaceLayer layer = workspace.CreateNextLayer(Path.Combine(root, "pack.zip"));

                WriteFile(layer.OutputPath, "pack\\a.txt");

                string target = Path.Combine(root, "stage");
                WorkspacePublishResult published = workspace.Publish(target);

                Assert.True(published.Success, published.Message);
                Assert.True(
                    File.Exists(Path.Combine(target, "pack", "a.txt")),
                    "归档自带的文件夹必须原样保留：" + published.Message);

                Assert.False(
                    File.Exists(Path.Combine(target, "a.txt")),
                    "⛔ 只解一层时也不许把归档自带的文件夹摊平");
            }
            finally
            {
                TryDelete(root);
            }
        }

        // ═════════════════ ⑥ 多分支：每个内层包各占一层自己的包名 ═════════════════

        [Fact]
        public void 多分支_每个内层包各自一层自己的包名()
        {
            string root = NewTempRoot();

            try
            {
                var workspace = new ExtractionWorkspace(Path.Combine(root, "ws"), "branches");
                WorkspaceLayer outer = workspace.CreateNextLayer(Path.Combine(root, "AAA.7z"));

                WriteFile(outer.OutputPath, "payload.txt");
                WriteFile(outer.OutputPath, @"BBBB\CCCCC.mp4");
                WriteFile(outer.OutputPath, @"CCCC\EEEEE.mp4");

                Extract(workspace, outer, @"BBBB\CCCCC.mp4", "CCCCC");
                Extract(workspace, outer, @"CCCC\EEEEE.mp4", "EEEEE");

                string target = Path.Combine(root, "stage");
                WorkspacePublishResult published = workspace.Publish(target, inPlaceInnerPackages: true);

                Assert.True(published.Success, published.Message);

                // 每个分支各占一层自己的包名；**不许"多个分支共用一个名字"**。
                Assert.True(File.Exists(Path.Combine(target, "payload.txt")));
                Assert.True(File.Exists(Path.Combine(target, "BBBB", "CCCCC", "内容物", "CCCCC.bin")));
                Assert.True(File.Exists(Path.Combine(target, "CCCC", "EEEEE", "内容物", "EEEEE.bin")));

                // 两个分支的内容物绝不混进同一层。
                Assert.False(Directory.Exists(Path.Combine(target, "内容物")));
                Assert.False(Directory.Exists(Path.Combine(target, "BBBB", "内容物")));
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void 分卷组_就地替换后产物里不许留下半套卷()
        {
            /*
             * 被解开的分卷组是**整组**一起被消费掉的。只删掉"这一层要解的那一卷"，
             * 后续卷会缺了首卷留在产物里 —— 而定稿那一侧的分卷完整性闸门
             * （`ExtractionCoordinator.TryConfirmVolumeGroupComplete`：`.002` 必须能找到同目录的 `.001`）
             * 会判定"整组不完整"⇒ 整份计划作废、任务报失败（用户 2026-09-30 那 25 GB 的同一道闸门）。
             * 这里只钉一件事：被就地替换掉的那一组卷，产物里一卷都不许剩。
             */
            string root = NewTempRoot();

            try
            {
                var workspace = new ExtractionWorkspace(Path.Combine(root, "ws"), "volumes");
                WorkspaceLayer outer = workspace.CreateNextLayer(Path.Combine(root, "AAA.7z"));

                WriteFile(outer.OutputPath, "note.txt");
                WriteFile(outer.OutputPath, "Y.7z.001");
                WriteFile(outer.OutputPath, "Y.7z.002");

                WorkspaceLayer inner = workspace.CreateNextLayer(Path.Combine(outer.OutputPath, "Y.7z.001"));
                WriteFile(inner.OutputPath, @"内容物\payload.bin");

                string target = Path.Combine(root, "stage");
                WorkspacePublishResult published = workspace.Publish(target, inPlaceInnerPackages: true);

                Assert.True(published.Success, published.Message);

                // 真文件与解出来的内容物照常在。
                Assert.True(File.Exists(Path.Combine(target, "note.txt")), published.Message);
                Assert.True(File.Exists(Path.Combine(target, "内容物", "payload.bin")), published.Message);

                // ⛔ 整组卷一个都不许留在产物里。
                Assert.False(File.Exists(Path.Combine(target, "Y.7z.001")), "首卷已被解开、让位给了目录");
                Assert.False(File.Exists(Path.Combine(target, "Y.7z.002")), "同一组的后续卷也已经被消费，不该留在产物里");

                /*
                 * 而且这一份产物必须**过得了定稿**：里面没有"缺首卷的残组"，
                 * 定稿侧的分卷完整性闸门就不会把整份计划作废、把这一单判成失败。
                 * （老行为：`Y.7z.002` 留下来 ⇒ 闸门判"缺首卷（Y.7z.001）"⇒ 计划 Failed、
                 * 产物与源包一个字节都不动，用户看到的是"这一层里有分卷组不完整"。）
                 */
                ExtractionCoordinator.FinalLayoutPlan plan = ExtractionCoordinator.PlanFinalLayout(
                    target,
                    Path.Combine(root, "out", "AAA"),
                    sharedOutputRoot: false,
                    archiveBaseName: "AAA");

                Assert.False(plan.Failed, plan.FailureReason);
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void 多分支_两个分支都在同一层时各自一层()
        {
            // 同一层里认出多个内层包（`a.7z` 与 `b.7z` 并排）：各自一层，绝不并入同一层。
            string root = NewTempRoot();

            try
            {
                var workspace = new ExtractionWorkspace(Path.Combine(root, "ws"), "same-level");
                WorkspaceLayer outer = workspace.CreateNextLayer(Path.Combine(root, "AAA.7z"));

                WriteFile(outer.OutputPath, "a.7z");
                WriteFile(outer.OutputPath, "b.7z");

                Extract(workspace, outer, "a.7z", "a");
                Extract(workspace, outer, "b.7z", "b");

                string target = Path.Combine(root, "stage");
                WorkspacePublishResult published = workspace.Publish(target, inPlaceInnerPackages: true);

                Assert.True(published.Success, published.Message);

                Assert.Equal(
                    new[]
                    {
                        "a",
                        @"a\内容物",
                        @"a\内容物\a.bin",
                        "b",
                        @"b\内容物",
                        @"b\内容物\b.bin"
                    },
                    Tree(target));
            }
            finally
            {
                TryDelete(root);
            }
        }

        // ═════════════════ 唯一出口的几条事实（判据单元）═════════════════

        [Fact]
        public void 唯一出口_就地替换的名字与落点()
        {
            // 去掉假后缀的基名。
            Assert.Equal("DDDD", PackageLayerRules.ResolveInPlaceLayerName(@"C:\AAA\DDDD.mp4"));
            Assert.Equal("小小绘 推特大合集", PackageLayerRules.ResolveInPlaceLayerName(@"C:\AAA\小小绘 推特大合集.7z"));

            // ⛔ 分卷组一律不另建层（过程物名不成层）：返回空串，内容归上一层。
            Assert.Equal(string.Empty, PackageLayerRules.ResolveInPlaceLayerName(@"C:\AAA\59768866.7z.001"));

            // 落点 = **内层包原来所在的那个目录** + 基名（不是顶层、也不是父任务的落点）。
            Assert.Equal(@"C:\AAA\DDDD", PackageLayerRules.ResolveInPlaceDirectory(@"C:\AAA", @"C:\AAA\DDDD.mp4"));
            Assert.Equal(@"C:\AAA\BBBB\CCCCC", PackageLayerRules.ResolveInPlaceDirectory(@"C:\AAA\BBBB", @"C:\AAA\BBBB\CCCCC.mp4"));

            // 取不出名字时不硬造一层：落点就是上一层目录。
            Assert.Equal(
                @"C:\AAA",
                PackageLayerRules.ResolveInPlaceDirectory(@"C:\AAA\", @"C:\AAA\59768866.7z.001"));
        }

        [Fact]
        public void 唯一出口_中间层判据的三条优先级()
        {
            // ① 多分支：无条件建层（压过下面两条）。
            Assert.True(PackageLayerRules.ShouldAddInnerPackageLayer(false, parentProducedContent: true, siblingCount: 3));
            Assert.True(PackageLayerRules.ShouldAddInnerPackageLayer(true, parentProducedContent: true, siblingCount: 3));

            // ② 父层自己出了内容物：不建层（内层包的东西并进父层）。
            Assert.False(PackageLayerRules.ShouldAddInnerPackageLayer(false, parentProducedContent: true, siblingCount: 1));
            Assert.False(PackageLayerRules.ShouldAddInnerPackageLayer(true, parentProducedContent: true, siblingCount: 1));

            // ③ 干净的单链过路层：忠实档建、简洁档省。
            Assert.True(PackageLayerRules.ShouldAddInnerPackageLayer(false, parentProducedContent: false, siblingCount: 1));
            Assert.False(PackageLayerRules.ShouldAddInnerPackageLayer(true, parentProducedContent: false, siblingCount: 1));
        }

        [Fact]
        public void 唯一出口_末层永不省()
        {
            // 链到头（没有再往下的层）⇒ 无条件保留：简洁档也照样留。
            Assert.True(PackageLayerRules.ShouldKeepLayerFolder(true, parentProducedContent: false, siblingCount: 1, hasChildLayer: false));
            Assert.True(PackageLayerRules.ShouldKeepLayerFolder(true, parentProducedContent: true, siblingCount: 1, hasChildLayer: false));

            // 还有下一层 ⇒ 中间层，按开关走。
            Assert.False(PackageLayerRules.ShouldKeepLayerFolder(true, parentProducedContent: false, siblingCount: 1, hasChildLayer: true));
            Assert.True(PackageLayerRules.ShouldKeepLayerFolder(false, parentProducedContent: false, siblingCount: 1, hasChildLayer: true));
        }

        [Fact]
        public void 唯一出口_这一层有没有出内容物()
        {
            string root = NewTempRoot();

            try
            {
                string directory = Path.Combine(root, "layer");
                Directory.CreateDirectory(directory);

                // 目录里只有那一个内层包 ⇒ 过路层（自己没出内容物）。
                WriteFile(directory, "next.7z");
                Assert.False(PackageLayerRules.ProducedOwnContent(directory, innerArchiveCount: 1));

                // 多出一个文件 ⇒ 自己出了内容物。
                WriteFile(directory, "readme.txt");
                Assert.True(PackageLayerRules.ProducedOwnContent(directory, innerArchiveCount: 1));

                // 读不了目录时按"出了内容物"处理（保守一侧：宁可多留一层）。
                Assert.True(PackageLayerRules.ProducedOwnContent(Path.Combine(root, "不存在"), innerArchiveCount: 1));
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void 多分支落点_续解侧每个内层包各自一层自己的包名()
        {
            // 续解侧（一键处理那一档）与递归发布侧**读同一份判据**（PackageLayerRules）：
            // 落点 = 内层包**原来所在的那个目录** + 它自己的包基名。这里把用户极端例子里
            // 那七个内层包的落点逐个钉住（上次只有判据单元，没有落点用例）。
            var parent = new ArchiveTask(@"C:\AAA\AAA.rar")
            {
                OutputPath = @"C:\AAA",
                ContentDirectoryPath = @"C:\AAA"
            };

            var placements = new List<string>();

            foreach ((string innerPackage, string layerName, string expected) in new[]
                     {
                         (@"C:\AAA\DDDD.mp4", "DDDD", @"C:\AAA\DDDD"),
                         (@"C:\AAA\EEEE.mp4", "EEEE", @"C:\AAA\EEEE"),
                         (@"C:\AAA\FFFF.mp4", "FFFF", @"C:\AAA\FFFF"),
                         (@"C:\AAA\BBBB\CCCCC.mp4", "CCCCC", @"C:\AAA\BBBB\CCCCC"),
                         (@"C:\AAA\BBBB\DDDDD.mp4", "DDDDD", @"C:\AAA\BBBB\DDDDD"),
                         (@"C:\AAA\CCCC\EEEEE.mp4", "EEEEE", @"C:\AAA\CCCC\EEEEE"),
                         (@"C:\AAA\CCCC\DDDDD\EEEEEE.mp4", "EEEEEE", @"C:\AAA\CCCC\DDDDD\EEEEEE")
                     })
            {
                // 多分支（这一层认出多个内层包）⇒ addLayer = true，层名 = 内层包去掉假后缀的基名。
                bool addLayer = OneClickCoordinator.ShouldAddContinuationLevelLayer(
                    omitMiddleLayers: true,
                    parentProducedContent: true,
                    siblingCount: 7);

                Assert.True(addLayer, innerPackage + "：多分支必须各占一层");
                Assert.Equal(layerName, OneClickCoordinator.ResolveContinuationLayerName(innerPackage));

                string directory = OneClickCoordinator.ResolveContinuationOutputDirectory(
                    parent,
                    innerPackage,
                    OneClickCoordinator.ResolveContinuationLayerName(innerPackage));

                Assert.Equal(expected, directory);
                placements.Add(directory);
            }

            // ⛔ 不许"多个分支共用一个名字"。
            Assert.Equal(placements.Count, placements.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        // ═════════════════ 辅助 ═════════════════

        /// <summary>
        /// 跑一条五层链（真目录）：把"发布 + 定稿"接起来，返回**最终那棵树**（相对 <c>destDir</c>）。
        ///
        /// <para>链条：<c>111.7z → 2222.7z → 33333.7z → 444444.7z → 5555555.7z → 内容物\payload.bin</c>。
        /// 中间三层是"只装着下一个包、自己没有内容物"的干净过路层。</para>
        /// </summary>
        private static (IReadOnlyList<string> Published, IReadOnlyList<string> Final) RunChain(bool omitMiddleLayers)
        {
            string root = NewTempRoot();

            try
            {
                var workspace = new ExtractionWorkspace(Path.Combine(root, "ws"), "chain");
                WorkspaceLayer outer = workspace.CreateNextLayer(Path.Combine(root, "111.7z"));

                WriteFile(outer.OutputPath, "2222.7z");

                WorkspaceLayer second = workspace.CreateNextLayer(Path.Combine(outer.OutputPath, "2222.7z"));
                WriteFile(second.OutputPath, "33333.7z");

                WorkspaceLayer third = workspace.CreateNextLayer(Path.Combine(second.OutputPath, "33333.7z"));
                WriteFile(third.OutputPath, "444444.7z");

                WorkspaceLayer fourth = workspace.CreateNextLayer(Path.Combine(third.OutputPath, "444444.7z"));
                WriteFile(fourth.OutputPath, "5555555.7z");

                WorkspaceLayer fifth = workspace.CreateNextLayer(Path.Combine(fourth.OutputPath, "5555555.7z"));
                WriteFile(fifth.OutputPath, @"内容物\payload.bin");

                string target = Path.Combine(root, "stage");
                WorkspacePublishResult published = workspace.Publish(
                    target,
                    inPlaceInnerPackages: true,
                    omitMiddlePackageLayers: omitMiddleLayers);

                Assert.True(published.Success, published.Message);

                // ⛔ 被解开的包一个都不许留在产物里。
                Assert.DoesNotContain(Tree(target), entry => entry.EndsWith(".7z", StringComparison.OrdinalIgnoreCase));

                // 首层（`111\`）永远是落点本身，不参与"省不省"。
                string destDir = Path.Combine(root, "out", "111");

                FinalizePlan plan = ResultFinalizer.Plan(
                    Snapshot(target),
                    destDir,
                    TerminalLayoutMode.KeepLastFolder,
                    "111",
                    stagingRoot: target,
                    innermostPackageBaseName: "5555555");

                IReadOnlyList<string> finalTree = ProjectFinalTree(plan, destDir);

                return (Tree(target), finalTree);
            }
            finally
            {
                TryDelete(root);
            }
        }

        /// <summary>
        /// 照用户给的形状造一层：把 <paramref name="relativeArchivePath"/> 那个内层包解出来，
        /// 它的产物是 <c>内容物\&lt;层名&gt;.bin</c>（就地替换之后那一层里的东西）。
        /// </summary>
        private static WorkspaceLayer Extract(
            ExtractionWorkspace workspace,
            WorkspaceLayer parent,
            string relativeArchivePath,
            string layerName)
        {
            WorkspaceLayer layer = workspace.CreateNextLayer(Path.Combine(parent.OutputPath, relativeArchivePath));

            WriteFile(layer.OutputPath, @"内容物\" + layerName + ".bin");

            return layer;
        }

        /// <summary>把一棵真目录树写成 <see cref="StagedEntry"/> 清单（相对 <paramref name="root"/>）。</summary>
        private static IReadOnlyList<StagedEntry> Snapshot(string root)
        {
            var entries = new List<StagedEntry>();
            var pending = new Stack<(string Path, string Relative)>();

            pending.Push((root, string.Empty));

            while (pending.Count > 0)
            {
                (string path, string relative) = pending.Pop();

                foreach (string entry in Directory.GetFileSystemEntries(path))
                {
                    string name = Path.GetFileName(entry);
                    string entryRelative = relative.Length == 0 ? name : relative + @"\" + name;
                    bool isDirectory = Directory.Exists(entry);

                    entries.Add(new StagedEntry
                    {
                        RelativePath = entryRelative,
                        IsDirectory = isDirectory,
                        Size = isDirectory ? 0 : new FileInfo(entry).Length
                    });

                    if (isDirectory)
                    {
                        pending.Push((entry, entryRelative));
                    }
                }
            }

            return entries;
        }

        /// <summary>目录树里的全部条目（目录 + 文件），相对路径、排序后。</summary>
        private static IReadOnlyList<string> Tree(string root)
        {
            return Snapshot(root)
                .Select(entry => entry.RelativePath)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// 把移动计划**投影成最终的整棵树**（相对 <paramref name="destDir"/>）：
        /// 目录移动按 <c>From</c> 底下的真实条目展开，所以断言的是"用户最后看到什么"。
        /// </summary>
        private static IReadOnlyList<string> ProjectFinalTree(FinalizePlan plan, string destDir)
        {
            var projected = new List<string>();

            foreach (PlannedMove move in plan.Moves)
            {
                projected.Add(Relative(move.To, destDir));

                if (!Directory.Exists(move.From))
                {
                    continue;
                }

                foreach (string directory in Directory.GetDirectories(move.From, "*", SearchOption.AllDirectories))
                {
                    projected.Add(Relative(
                        Path.Combine(move.To, Path.GetRelativePath(move.From, directory)),
                        destDir));
                }

                foreach (string file in Directory.GetFiles(move.From, "*", SearchOption.AllDirectories))
                {
                    projected.Add(Relative(
                        Path.Combine(move.To, Path.GetRelativePath(move.From, file)),
                        destDir));
                }
            }

            return projected.OrderBy(path => path, StringComparer.Ordinal).ToList();
        }

        private static string Relative(string path, string root)
        {
            string full = Path.GetFullPath(path);
            string prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";

            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? full[prefix.Length..]
                : full;
        }

        private static void WriteFile(string root, string relativePath)
        {
            string path = Path.Combine(root, relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, relativePath);
        }

        /// <summary>造一条"暂存条目"（文件）—— ⛔ 刻意不叫 <c>File</c>：会与 <see cref="System.IO.File"/> 撞名。</summary>
        private static StagedEntry StagedFile(string relativePath, long size = 100)
        {
            return new StagedEntry { RelativePath = relativePath, Size = size };
        }

        private static StagedEntry Dir(string relativePath)
        {
            return new StagedEntry { RelativePath = relativePath, IsDirectory = true };
        }

        private static string NewTempRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "ArchiveFixer-layer-rules-" + Guid.NewGuid().ToString("N"));
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
