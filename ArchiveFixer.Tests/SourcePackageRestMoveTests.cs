using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「一键处理」把源包移进其余物 / 手动「只解压」永远不动源包（决策 D-9 / D-11 / D-12）。
    ///
    /// <para>
    /// 这一组是**红线测试**，三条各自钉死：
    /// ① **手动「只解压」这条路，即使设置成 <c>MoveToRest</c>，也绝对不动源包**、也不生成 <c>其余物</c>
    ///    （AGENTS.md §6 第 1 条：用户放开的只是"一键处理"这一条路径）；
    /// ② 一键处理默认档：整组源包进 <c>其余物</c>，内容物一点不受影响；
    /// ③ 搬不动（只读 / 被占用）时任务标「部分完成」并写明"内容物已好，源包未能移入其余物"，
    ///    内容物结论不受影响（不变量 6：跑了一半的事不许只报成功）。
    /// </para>
    /// <para>
    /// 引擎是假的（照着 ExtractionPipelineFixTests 的同一套装配）：这里要验的是**收尾那几步**
    /// ——定稿、校验、归集、源包处理——解压本身不是被测对象。
    /// </para>
    /// </summary>
    public class SourcePackageRestMoveTests : IDisposable
    {
        private readonly string _root;

        public SourcePackageRestMoveTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerRestMove", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 手动「只解压」也照搬（需求变更 2026-09-22）

        /// <summary>
        /// <b>需求变更（用户 2026-09-22 拍板，本条测试是被改写的那一个）</b>：
        /// 上一版的规则是"手动「只解压」永远不动源包"；用户看过程序跑的结果后明确改为
        /// 「如果成功了你就直接将源包放在其余物里面」＋"地基路径也照这条走"。
        ///
        /// <para>
        /// 所以现在的判据是：手动「只解压」**成功 + 输出校验通过 + 未取消** →
        /// 整组源包移入其余物，内容物一点不受影响。
        /// </para>
        /// </summary>
        [Fact]
        public async Task 手动只解压_成功后源包也移入其余物()
        {
            Harness harness = CreateHarness(settings => settings.SourceHandling = "MoveToRest");

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(task.IsOutputVerified);

            string restDirectory = Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName);
            string movedSource = Path.Combine(restDirectory, "pack.7z");

            // 源包：原位没有了，其余物里有；内容一个字节都没变。
            Assert.False(File.Exists(source), "手动「只解压」成功后源包也该进其余物（用户 2026-09-22 的新规则）");
            Assert.True(File.Exists(movedSource), $"源包没有落到 {movedSource}");
            Assert.Equal("not a real archive - the engine is faked in these tests", File.ReadAllText(movedSource));
            Assert.Equal(SourcePackageMoveState.Done, task.SourcePackageMove);
            Assert.Equal(movedSource, task.CurrentPath);

            // 内容物照常（其余物里那份源包不算内容物）。
            Assert.Equal(5, CountFiles(task.OutputPath, excludeArtifactDirectory: true));

            // 日志必须说清"从哪搬到哪"。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("源包移入其余物", StringComparison.Ordinal) &&
                        line.Contains(source, StringComparison.Ordinal) &&
                        line.Contains(movedSource, StringComparison.Ordinal));
        }

        /// <summary>
        /// 手动「只解压」是**单层**路径：第一层只出中间件（内容物在更深的层里）时它没有"续解链"可等，
        /// 而"解压成功 + 校验通过"这个事实当场就成立 —— 所以源包**当场**搬，不延期。
        ///
        /// 这一条同时钉住"延期只属于一键处理"这件事：手动路径若也延期，就再也没有人来补搬，
        /// 源包会永远留在原地（那正是本次要修的缺陷在另一条路径上的翻版）。
        /// </summary>
        [Fact]
        public async Task 手动只解压_第一层只出中间件_源包照样当场搬进其余物()
        {
            Harness harness = CreateHarness(settings => settings.SourceHandling = "MoveToRest");

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            // 引擎只写出归档/分卷（都是"其余物"），一个内容物都没有。
            harness.Engine.FileNames = new[] { "inner.7z.001", "inner.7z.002", "inner.7z.003" };

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.Equal(SourcePackageMoveState.Done, task.SourcePackageMove);

            string restDirectory = Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName);

            Assert.False(File.Exists(source));
            Assert.True(File.Exists(Path.Combine(restDirectory, "pack.7z")));
            Assert.True(File.Exists(Path.Combine(restDirectory, "inner.7z.001")));
        }

        /// <summary>
        /// <b>新红线（需求变更里明确保留的那一条）</b>：解压失败 → 源包原地不动，其余物不生成。
        /// </summary>
        [Fact]
        public async Task 手动只解压_失败时_源包留在原地且不生成其余物()
        {
            Harness harness = CreateHarness(settings => settings.SourceHandling = "MoveToRest");

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            harness.Engine.ExtractFailure = new ArchiveOperationResult
            {
                Success = false,
                Status = StatusText.Corrupted,
                Message = "文件损坏",
                DetectedErrorType = "Corrupted"
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.Corrupted, task.Status);
            Assert.True(File.Exists(source), "失败时源包必须原地不动");
            Assert.Equal(SourcePackageMoveState.NotAttempted, task.SourcePackageMove);
            Assert.False(
                Directory.Exists(Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName)),
                "失败时不该生成其余物目录");
        }

        /// <summary>同一条红线的取消版：用户按了「取消当前」→ 源包原地不动、其余物不生成。</summary>
        [Fact]
        public async Task 手动只解压_取消时_源包留在原地且不生成其余物()
        {
            Harness harness = CreateHarness(settings => settings.SourceHandling = "MoveToRest");

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            // 解压已经做完、进入收尾（收尾第一件事就是列目录）时按下「取消当前」。
            harness.Engine.OnList = _ =>
            {
                if (harness.Engine.Extracted)
                {
                    harness.Coordinator.CancelCurrentTask();
                }
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.Cancelled, task.Status);
            Assert.True(File.Exists(source), "取消时源包必须原地不动");
            Assert.Equal(SourcePackageMoveState.NotAttempted, task.SourcePackageMove);
            Assert.False(
                Directory.Exists(Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName)),
                "取消时不该生成其余物目录");
        }

        // ================================================================ ①之二 KeepInPlace 档（两条路径都钉住）

        [Theory]
        [InlineData(false)]   // 手动「只解压」
        [InlineData(true)]    // 一键处理
        public async Task KeepInPlace档_两条路径都不搬源包(bool oneClick)
        {
            Harness harness = CreateHarness(settings => settings.SourceHandling = "KeepInPlace");

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            if (oneClick)
            {
                await harness.Coordinator.StartExtractForOneClickAsync();
            }
            else
            {
                await harness.Coordinator.StartExtractAsync();
            }

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(File.Exists(source));
            Assert.Equal(source, task.CurrentPath);
            Assert.Equal(SourcePackageMoveState.NotAttempted, task.SourcePackageMove);
            Assert.False(
                Directory.Exists(Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName)),
                "KeepInPlace 档不该生成其余物目录");
        }

        // ================================================================ ② 一键处理默认档

        /// <summary>
        /// <b>形状 A（回归）</b>：第一层直接出内容物 → 源包（这里是分卷组，整组）当场搬进其余物。
        /// 这条形状在修复前就是好的，必须继续成立。
        /// </summary>
        [Fact]
        public async Task 形状A_第一层直接出内容物_源包整组当场移入其余物()
        {
            Harness harness = CreateHarness();

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            string workDirectory = harness.PathService.BuildTaskWorkDirectory(task);

            await harness.Coordinator.StartExtractForOneClickAsync();

            string restDirectory = Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName);
            string movedSource = Path.Combine(restDirectory, "pack.7z");

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(task.IsOutputVerified);

            // 源包：原位没有了，其余物里有；内容一个字节都没变。
            Assert.False(File.Exists(source), "一键处理默认档应该把源包搬进其余物");
            Assert.True(File.Exists(movedSource), $"源包没有落到 {movedSource}");
            Assert.Equal("not a real archive - the engine is faked in these tests", File.ReadAllText(movedSource));

            // **当场**搬（不是链结束后的补搬）：记账直接是 Done，日志里没有"补搬"字样。
            Assert.Equal(SourcePackageMoveState.Done, task.SourcePackageMove);
            Assert.DoesNotContain(harness.LogTexts, line => line.Contains("链结束后的补搬", StringComparison.Ordinal));

            // 内容物照常（其余物里那份源包不算内容物）。
            Assert.Equal(5, CountFiles(task.OutputPath, excludeArtifactDirectory: true));
            Assert.Contains(
                Directory.GetFiles(task.OutputPath, "*.bin"),
                file => Path.GetFileName(file) == "payload-00000.bin");

            // 任务对象跟着改：CurrentPath 指向新位置的源包。
            // 这一条不是"顺手"：一键处理的续解扫描靠它把"刚搬走的源包"排除掉，
            // 不改的话源包会被当成一个新内层包再解一遍（内容物凭空多一份）。
            Assert.Equal(movedSource, task.CurrentPath);

            // 日志必须说清"从哪搬到哪"。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("源包移入其余物", StringComparison.Ordinal) &&
                        line.Contains(source, StringComparison.Ordinal) &&
                        line.Contains(movedSource, StringComparison.Ordinal));

            // 工作区照常清理：它必须按**暂存目录**反推（源包搬家后 CurrentPath 的哈希变了，
            // 按 CurrentPath 重算会算到另一个目录上，于是近 1 GB 中间件静默留在工作区）。
            Assert.False(Directory.Exists(workDirectory), "源包搬家之后工作区没被清理");
        }

        // ================================================================ ②之二 形状 B：内容物在第 2 层才出现

        /// <summary>
        /// **形状 B —— 本次修的缺陷**：第一层只出内层中间件（**0 个内容物**），内容物要等续解出来的
        /// 子任务才出现（用户真实文件：<c>222.mp4</c> = 假 MP4 头 + 尾部完整 ZIP + ZIP 里是头加密的 7z 分卷）。
        ///
        /// <para>修复前的现场：</para>
        /// <code>
        /// [WARN] 222.mp4：内容物未全部定稿，源包留在原地（未移入其余物）。
        /// [INFO] 222.mp4：定稿完成 —— 内容物 0 个文件 → …\111\222；其余物 4 项 → …\111\222\其余物
        /// </code>
        /// <para>
        /// 最外层那一轮的守卫 <c>commit.MovedContentCount == 0</c> 直接放弃了源包搬运，
        /// 而真正产出内容物的续解子任务按设计跳过源包处理 → 整条链跑完，源包仍是原样躺在
        /// <c>111\</c> 下，其余物里只有内层分卷。
        /// </para>
        /// <para>
        /// 修复后：最外层那一轮把源包记账成"留到链结束后补搬"
        /// （<see cref="SourcePackageMoveState.DeferredToChainEnd"/>），链尾由
        /// <c>OneClickCoordinator</c> 调 <c>CompleteRootSourcePackagesAfterChainAsync</c> 补做。
        /// 这条测试用假引擎手工摆出那条链（真 7z 的端到端版在 <c>InnerLayerContinuationTests</c>）。
        /// </para>
        /// </summary>
        [Fact]
        public async Task 形状B_第一层零内容物_链结束后补搬源包进其余物()
        {
            Harness harness = CreateHarness();
            ShapeBChain chain = await BuildShapeBChainAsync(harness);

            Assert.Equal(SourcePackageMoveState.DeferredToChainEnd, chain.Root.SourcePackageMove);
            Assert.True(File.Exists(chain.Source), "链还没结束，源包不该动");
            Assert.True(File.Exists(Path.Combine(chain.RestDirectory, "inner.7z.001")));

            // ---- 链结束：补搬 ----
            await harness.Coordinator.CompleteRootSourcePackagesAfterChainAsync(
                new[] { chain.Root }, new[] { chain.Root, chain.Continuation });

            string movedSource = Path.Combine(chain.RestDirectory, "outer.7z");

            Assert.False(File.Exists(chain.Source), "链结束后源包应该被补搬进其余物");
            Assert.True(File.Exists(movedSource), $"源包没有落到 {movedSource}");
            Assert.Equal(SourcePackageMoveState.Done, chain.Root.SourcePackageMove);
            Assert.Equal(movedSource, chain.Root.CurrentPath);

            // 内容物不受影响，且与源包在同一个最终目录里（不再另建文件夹）。
            Assert.True(File.Exists(Path.Combine(chain.DestinationDirectory, "content.bin")));

            /*
             * 日志必须能区分"本轮直接搬"与"链结束后的补搬"（用户明确要求）——
             * 这一句就是那次补搬的证据，而且写清了判据（链已跑完 + 目录里有内容物 + 校验通过）。
             */
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("链结束后的补搬", StringComparison.Ordinal) &&
                        line.Contains("内容物", StringComparison.Ordinal));
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("源包移入其余物", StringComparison.Ordinal) &&
                        line.Contains(chain.Source, StringComparison.Ordinal) &&
                        line.Contains(movedSource, StringComparison.Ordinal));
        }

        /// <summary>
        /// 幂等（硬要求）：同一个源包**绝不搬第二次** —— 再补一次也不许多出一份，也不许报错。
        /// </summary>
        [Fact]
        public async Task 形状B_补搬幂等_同一个源包不会出现在两个其余物里()
        {
            Harness harness = CreateHarness();
            ShapeBChain chain = await BuildShapeBChainAsync(harness);

            await harness.Coordinator.CompleteRootSourcePackagesAfterChainAsync(
                new[] { chain.Root }, new[] { chain.Root, chain.Continuation });

            string movedSource = Path.Combine(chain.RestDirectory, "outer.7z");

            // 第二次补搬（重复调用 / 下一轮又轮到它）：必须一个字节都不动。
            await harness.Coordinator.CompleteRootSourcePackagesAfterChainAsync(
                new[] { chain.Root }, new[] { chain.Root, chain.Continuation });

            Assert.Equal(StatusText.ExtractSuccess, chain.Root.Status);
            Assert.Equal(SourcePackageMoveState.Done, chain.Root.SourcePackageMove);
            Assert.True(File.Exists(movedSource));

            // 没有 (1) 副本，整个输出根下只有一份源包。
            Assert.False(File.Exists(Path.Combine(chain.RestDirectory, "outer(1).7z")));
            Assert.False(File.Exists(Path.Combine(chain.DestinationDirectory, "outer(1)", "其余物", "outer.7z")));

            string[] copies = Directory.GetFiles(harness.OutputRoot, "outer.7z", SearchOption.AllDirectories);
            Assert.True(copies.Length == 1, $"源包应该只有一份，实际 {copies.Length} 份：{string.Join("、", copies)}");
            Assert.Equal(movedSource, copies[0]);
        }

        /// <summary>
        /// 幂等（跨运行）：用户对同一个包**再点一次**一键处理时，源包已经在其余物里了，
        /// 绝不能被拖到新一轮输出目录的其余物里（否则用户看到"源包又跑回来了 / 凭空多一份"）。
        /// </summary>
        [Fact]
        public async Task 幂等_再点一次一键处理_源包不会被搬第二次()
        {
            Harness harness = CreateHarness();

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            await harness.Coordinator.StartExtractForOneClickAsync();

            string movedSource = Path.Combine(
                task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName, "pack.7z");

            Assert.True(File.Exists(movedSource));
            Assert.Equal(SourcePackageMoveState.Done, task.SourcePackageMove);

            // 第二轮：同一个任务对象，源包已经安顿在其余物里（GUI 里就是"再点一次一键处理"）。
            task.IsSelected = true;
            await harness.Coordinator.StartExtractForOneClickAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(File.Exists(movedSource), "源包必须留在它已经安顿好的位置");

            string[] copies = Directory.GetFiles(harness.OutputRoot, "pack.7z", SearchOption.AllDirectories);
            Assert.True(copies.Length == 1, $"源包应该只有一份，实际 {copies.Length} 份：{string.Join("、", copies)}");
            Assert.Equal(movedSource, copies[0]);
            Assert.Contains(harness.LogTexts, line => line.Contains("幂等", StringComparison.Ordinal));
        }

        /// <summary>
        /// 形状 B + **取消**：链尾补搬时令牌已取消 → 源包原地不动、其余物不为它生成（新红线）。
        /// </summary>
        [Fact]
        public async Task 形状B_补搬前已取消_源包留在原地()
        {
            Harness harness = CreateHarness();
            ShapeBChain chain = await BuildShapeBChainAsync(harness);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await harness.Coordinator.CompleteRootSourcePackagesAfterChainAsync(
                new[] { chain.Root }, new[] { chain.Root, chain.Continuation }, cts.Token);

            Assert.True(File.Exists(chain.Source), "取消时源包必须原地不动");
            Assert.False(File.Exists(Path.Combine(chain.RestDirectory, "outer.7z")));
            Assert.Equal(SourcePackageMoveState.DeferredToChainEnd, chain.Root.SourcePackageMove);
            Assert.Contains(harness.LogTexts, line => line.Contains("补搬被取消", StringComparison.Ordinal));
        }

        /// <summary>
        /// 形状 B + **失败**（第二层没解开、内容物根本没出现）：链尾补搬不许动源包，
        /// 也不许为源包生成其余物 —— 内容物没出来就没有"这次整理成功了"这回事。
        /// </summary>
        [Fact]
        public async Task 形状B_内容物没出来_源包留在原地()
        {
            Harness harness = CreateHarness();

            string source = CreateSourceFile("outer.7z");

            var root = new ArchiveTask(source, 1)
            {
                IsArchive = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized,
                IsSelected = true
            };

            harness.Vm.Tasks.Add(root);

            // 第一层只出中间件 → 源包被记账成"待补搬"。
            harness.Engine.FileNames = new[] { "inner.7z.001" };
            await harness.Coordinator.StartExtractForOneClickAsync();

            string restDirectory = Path.Combine(root.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName);

            Assert.Equal(SourcePackageMoveState.DeferredToChainEnd, root.SourcePackageMove);

            // 续解那一步没能产出内容物（第二层失败 / 没跑）→ 链尾补搬时目录里只有中间件。
            await harness.Coordinator.CompleteRootSourcePackagesAfterChainAsync(new[] { root }, new[] { root });

            Assert.True(File.Exists(source), "内容物没出来时源包必须原地不动");
            Assert.False(File.Exists(Path.Combine(restDirectory, "outer.7z")));
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("没有内容物", StringComparison.Ordinal) &&
                        line.Contains("源包留在原地", StringComparison.Ordinal));
        }

        /// <summary>
        /// 形状 B 的反面对照：内容物文件**落盘了、但校验没过**（引擎声明的条目数与实际不符）→
        /// 不算"内容物已校验通过"，源包不许动。
        ///
        /// 这条是"别拿 move 计数当唯一判据"的另一半：搬是搬了，但校验这一关必须真的过 ——
        /// 而且要看的是**产出内容物那一层**的校验，不是根任务那一层（根任务那一轮只有中间件）。
        /// </summary>
        [Fact]
        public async Task 形状B_内容物校验没过_源包留在原地()
        {
            Harness harness = CreateHarness();

            string source = CreateSourceFile("outer.7z");

            var root = new ArchiveTask(source, 1)
            {
                IsArchive = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized,
                IsSelected = true
            };

            harness.Vm.Tasks.Add(root);

            // ① 第一层只出中间件 → 源包被记账成"待补搬"。
            harness.Engine.FileNames = new[] { "inner.7z.001" };
            await harness.Coordinator.StartExtractForOneClickAsync();

            string destinationDirectory = root.OutputPath;
            string restDirectory = Path.Combine(destinationDirectory, ProcessArtifactLayout.ArtifactDirectoryName);

            ArchiveTask continuation = AddContinuationTask(harness, restDirectory, destinationDirectory, root, index: 2);

            // ② 第二层：产物写出来了，但引擎声明的条目数比落盘多 → 校验不通过。
            harness.Engine.FileNames = new[] { "content.bin" };
            harness.Engine.ExpectedFileCountOverride = 99;
            await harness.Coordinator.StartExtractForOneClickAsync();

            Assert.False(continuation.IsOutputVerified, "前提：第二层的输出校验应该没过");
            Assert.True(File.Exists(Path.Combine(destinationDirectory, "content.bin")));

            await harness.Coordinator.CompleteRootSourcePackagesAfterChainAsync(
                new[] { root }, new[] { root, continuation });

            Assert.True(File.Exists(source), "校验没过时源包必须原地不动");
            Assert.False(File.Exists(Path.Combine(restDirectory, "outer.7z")));
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("校验", StringComparison.Ordinal) &&
                        line.Contains("不补搬源包", StringComparison.Ordinal));
        }

        /// <summary>
        /// 形状 B 的"分卷组"版：源包本身是一组分卷时，补搬必须**整组**一起进其余物（决策 D-12）。
        /// </summary>
        [Fact]
        public async Task 形状B_分卷组源包_链结束后整组补搬()
        {
            Harness harness = CreateHarness();

            string first = CreateSourceFile("222.7z.001");
            string second = CreateSourceFile("222.7z.002");

            var root = new ArchiveTask(first, 1)
            {
                IsArchive = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized,
                IsSelected = true,
                IsVolumeGroup = true,
                VolumeGroupKey = Path.Combine(_root, "src") + "|222"
            };

            root.VolumePaths.Add(first);
            root.VolumePaths.Add(second);
            harness.Vm.Tasks.Add(root);

            // 第一层只出中间件（0 内容物）→ 延期。
            harness.Engine.FileNames = new[] { "inner.7z.001" };
            await harness.Coordinator.StartExtractForOneClickAsync();

            string destinationDirectory = root.OutputPath;
            string restDirectory = Path.Combine(destinationDirectory, ProcessArtifactLayout.ArtifactDirectoryName);

            var continuation = AddContinuationTask(harness, restDirectory, destinationDirectory, root, index: 2);
            harness.Engine.FileNames = new[] { "content.bin" };
            await harness.Coordinator.StartExtractForOneClickAsync();

            Assert.Equal(SourcePackageMoveState.DeferredToChainEnd, root.SourcePackageMove);

            await harness.Coordinator.CompleteRootSourcePackagesAfterChainAsync(
                new[] { root }, new[] { root, continuation });

            Assert.False(File.Exists(first), "第一卷没有被补搬");
            Assert.False(File.Exists(second), "第二卷被落下了（决策 D-12：整组一起移）");
            Assert.True(File.Exists(Path.Combine(restDirectory, "222.7z.001")));
            Assert.True(File.Exists(Path.Combine(restDirectory, "222.7z.002")));
            Assert.Equal(
                new[] { Path.Combine(restDirectory, "222.7z.001"), Path.Combine(restDirectory, "222.7z.002") },
                root.VolumePaths.ToArray());
        }

        // ================================================================ 形状 B 的搭台

        /// <summary>形状 B 的现场：源包、最终目录、其余物目录、续解子任务。</summary>
        private sealed class ShapeBChain
        {
            public ArchiveTask Root { get; init; } = null!;

            public ArchiveTask Continuation { get; init; } = null!;

            public string Source { get; init; } = string.Empty;

            public string DestinationDirectory { get; init; } = string.Empty;

            public string RestDirectory { get; init; } = string.Empty;
        }

        /// <summary>
        /// 摆出形状 B 的前两步（与一键处理真实跑的那两轮一一对应）：
        /// ① 根任务那一轮只写 <c>inner.7z.001</c>（归档 → 归到其余物，0 个内容物）；
        /// ② 以"续解子任务"的身份把 <c>content.bin</c> 解进**同一个**输出目录。
        ///
        /// 刻意**不**在这里做链尾补搬：那是每条测试自己要断言的动作。
        /// </summary>
        private async Task<ShapeBChain> BuildShapeBChainAsync(Harness harness)
        {
            string source = CreateSourceFile("outer.7z");

            var root = new ArchiveTask(source, 1)
            {
                IsArchive = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized,
                IsSelected = true
            };

            harness.Vm.Tasks.Add(root);

            // ① 第一层：只有内层分卷，一个内容物都没有。
            harness.Engine.FileNames = new[] { "inner.7z.001" };
            await harness.Coordinator.StartExtractForOneClickAsync();

            string destinationDirectory = root.OutputPath;
            string restDirectory = Path.Combine(destinationDirectory, ProcessArtifactLayout.ArtifactDirectoryName);

            Assert.Equal(StatusText.ExtractSuccess, root.Status);
            Assert.Equal(SourcePackageMoveState.DeferredToChainEnd, root.SourcePackageMove);
            Assert.True(File.Exists(Path.Combine(restDirectory, "inner.7z.001")), "内层分卷应该已经归到其余物里");

            // ② 续解子任务：产物归入父任务那一个最终目录。
            ArchiveTask continuation = AddContinuationTask(harness, restDirectory, destinationDirectory, root, index: 2);

            harness.Engine.FileNames = new[] { "content.bin" };
            await harness.Coordinator.StartExtractForOneClickAsync();

            Assert.Equal(StatusText.ExtractSuccess, continuation.Status);
            Assert.True(File.Exists(Path.Combine(destinationDirectory, "content.bin")));

            return new ShapeBChain
            {
                Root = root,
                Continuation = continuation,
                Source = source,
                DestinationDirectory = destinationDirectory,
                RestDirectory = restDirectory
            };
        }

        /// <summary>把"续解出来的内层包"加进任务列表：它的落点由父任务给定（不再另建目录）。</summary>
        private static ArchiveTask AddContinuationTask(
            Harness harness,
            string restDirectory,
            string destinationDirectory,
            ArchiveTask parent,
            int index)
        {
            /*
             * 父任务必须**取消勾选**：解压流程只认勾选状态，`OneClickCoordinator` 在把内层包
             * 加进列表之前也是这么做的（`foreach (task in processed) task.IsSelected = false;`）。
             * 不取消勾选，下一轮会把父任务重新解一遍 —— 假引擎的产物清单是按"当前 FileNames"
             * 给的，那一轮就会给根任务写出内容物来，这条测试摆出来的形状当场就变形了。
             */
            parent.IsSelected = false;

            var continuation = new ArchiveTask(Path.Combine(restDirectory, "inner.7z.001"), index)
            {
                IsArchive = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized,
                IsSelected = true,
                ParentOutputDirectory = destinationDirectory,
                ParentTaskName = parent.FileName
            };

            harness.Vm.Tasks.Add(continuation);
            return continuation;
        }

        [Fact]
        public async Task 一键处理_留在原地档_不动源包也不建其余物()
        {
            Harness harness = CreateHarness(settings => settings.SourceHandling = "KeepInPlace");

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            await harness.Coordinator.StartExtractForOneClickAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(File.Exists(source));
            Assert.Equal(source, task.CurrentPath);
            Assert.False(Directory.Exists(Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName)));
        }

        [Fact]
        public async Task 一键处理_删除档_校验通过后删源包()
        {
            Harness harness = CreateHarness(settings => settings.SourceHandling = "DeleteAfterVerify");

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            await harness.Coordinator.StartExtractForOneClickAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.False(File.Exists(source), "DeleteAfterVerify 档应当在校验通过后删掉源包");
            Assert.Equal(5, CountFiles(task.OutputPath));
        }

        // ================================================================ ③ 失败与跨盘

        [Fact]
        public async Task 一键处理_源包搬不动_任务标部分完成_内容物不受影响()
        {
            Harness harness = CreateHarness();

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            // 模拟"源包只读 / 被别的程序占用"：同盘改名直接抛。
            harness.FileSystem.MoveFailure = new IOException("文件被占用");

            await harness.Coordinator.StartExtractForOneClickAsync();

            // 内容物已经好了：产物一个不少，输出校验仍然通过。
            Assert.Equal(5, CountFiles(task.OutputPath));
            Assert.True(task.IsOutputVerified);

            // 但任务整体没做完 —— 不许只报成功（不变量 6）。
            Assert.Equal(StatusText.PartiallyCompleted, task.Status);
            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.Contains("内容物已好，源包未能移入其余物", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("内容物已好，源包未能移入其余物", task.VerifyMessage, StringComparison.Ordinal);

            // 源包原地不动，其余物目录不该被建出来。
            Assert.True(File.Exists(source));
            Assert.False(Directory.Exists(Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName)));
        }

        [Fact]
        public async Task 一键处理_分卷组整组一起移入其余物()
        {
            Harness harness = CreateHarness();

            string first = CreateSourceFile("222.7z.001");
            string second = CreateSourceFile("222.7z.002");

            var task = new ArchiveTask(first, 1)
            {
                IsArchive = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized,
                IsSelected = true,
                IsVolumeGroup = true,
                VolumeGroupKey = Path.Combine(_root, "src") + "|222"
            };

            task.VolumePaths.Add(first);
            task.VolumePaths.Add(second);

            harness.Vm.Tasks.Add(task);

            await harness.Coordinator.StartExtractForOneClickAsync();

            string restDirectory = Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName);

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.False(File.Exists(first), "第一卷没有被搬走");
            Assert.False(File.Exists(second), "第二卷被落下了（决策 D-12：整组一起移）");
            Assert.True(File.Exists(Path.Combine(restDirectory, "222.7z.001")));
            Assert.True(File.Exists(Path.Combine(restDirectory, "222.7z.002")));

            // 分卷清单也要跟着改到新位置（后续清理 / 续解都读它）。
            Assert.Equal(
                new[] { Path.Combine(restDirectory, "222.7z.001"), Path.Combine(restDirectory, "222.7z.002") },
                task.VolumePaths.ToArray());
        }

        [Fact]
        public async Task 一键处理_跨盘搬运_复制成功后再删原件()
        {
            Harness harness = CreateHarness();

            // 强制"跨盘"：走复制 + 删原件那条路（真机上要造第二个卷，这里由假文件系统给结论）。
            harness.FileSystem.SameVolume = false;

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            await harness.Coordinator.StartExtractForOneClickAsync();

            string movedSource = Path.Combine(
                task.OutputPath,
                ProcessArtifactLayout.ArtifactDirectoryName,
                "pack.7z");

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(harness.FileSystem.CopyWasUsed);
            Assert.False(harness.FileSystem.MoveWasUsed, "跨盘路径不许走同盘改名");
            Assert.False(File.Exists(source), "复制成功之后原件才该消失");
            Assert.True(File.Exists(movedSource));
            Assert.Contains(harness.LogTexts, line => line.Contains("跨盘", StringComparison.Ordinal));
        }

        // ================================================================ 归集之后源包跟着走

        [Fact]
        public async Task 一键处理_归集开启时_源包跟着落到归集目录的其余物里()
        {
            string collectRoot = Path.Combine(_root, "collect");

            Harness harness = CreateHarness(settings =>
            {
                settings.CollectResultsToDirectory = true;
                settings.CollectTargetDirectory = collectRoot;
            });

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            await harness.Coordinator.StartExtractForOneClickAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.StartsWith(collectRoot, task.CollectedPath, StringComparison.OrdinalIgnoreCase);

            /*
             * 归集把整个产物目录搬走了（其余物跟着走），所以源包必须落进**归集之后**的那个其余物里。
             * 落在原来那个已经不存在的位置上，等于把源包扔进一个空目录 —— 用户再也找不到。
             */
            string movedSource = Path.Combine(
                task.CollectedPath,
                ProcessArtifactLayout.ArtifactDirectoryName,
                "pack.7z");

            Assert.False(File.Exists(source));
            Assert.True(File.Exists(movedSource), $"源包没有跟着归集走：{movedSource}");
            Assert.Equal(5, CountFiles(task.CollectedPath, excludeArtifactDirectory: true));
        }

        // ================================================================ 装配

        private Harness CreateHarness(Action<AppSettings>? configure = null)
        {
            string dataRoot = Path.Combine(_root, "data");
            string outputRoot = Path.Combine(_root, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = dataRoot;
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var engine = new FakeEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);
            var fileSystem = new SwitchableSourceMoveFileSystem();

            // MainViewModel 的构造会顺手写两个进程级静态：先存后还原，
            // 免得别的测试拿到我这边马上要删的临时目录（与 ExtractionPipelineFixTests 同一套做法）。
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                engine,
                passwordService,
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var coordinator = new ExtractionCoordinator(
                vm, engine, passwordService, pathService, new DialogService(), fileSystem);

            return new Harness(vm, engine, coordinator, logService, pathService, fileSystem, outputRoot);
        }

        private string CreateSourceFile(string fileName)
        {
            string directory = Path.Combine(_root, "src");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, fileName);
            File.WriteAllText(path, "not a real archive - the engine is faked in these tests");
            return path;
        }

        private static ArchiveTask AddTask(Harness harness, string sourcePath)
        {
            var task = new ArchiveTask(sourcePath, harness.Vm.Tasks.Count + 1)
            {
                IsArchive = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized,
                IsSelected = true
            };

            harness.Vm.Tasks.Add(task);
            return task;
        }

        private static int CountFiles(string? directory, bool excludeArtifactDirectory = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return 0;
                }

                if (!excludeArtifactDirectory)
                {
                    return Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length;
                }

                return Directory
                    .GetFiles(directory, "*", SearchOption.AllDirectories)
                    .Count(file => !file.StartsWith(
                        Path.Combine(directory, ProcessArtifactLayout.ArtifactDirectoryName) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return -1;
            }
        }

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                FakeEngine engine,
                ExtractionCoordinator coordinator,
                LogService log,
                PathService pathService,
                SwitchableSourceMoveFileSystem fileSystem,
                string outputRoot)
            {
                Vm = vm;
                Engine = engine;
                Coordinator = coordinator;
                Log = log;
                PathService = pathService;
                FileSystem = fileSystem;
                OutputRoot = outputRoot;
            }

            public MainViewModel Vm { get; }

            public FakeEngine Engine { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            /// <summary>本测试实例的输出根（"整个输出根下只有一份源包"这类断言要用它递归找）。</summary>
            public string OutputRoot { get; }

            /// <summary>屏幕日志的文本（无 WPF 应用的测试进程里照样会被填充）。</summary>
            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);

            public PathService PathService { get; }

            public SwitchableSourceMoveFileSystem FileSystem { get; }
        }

        /// <summary>
        /// 可控的假引擎：解压时往引擎输出目录（= 暂存目录）写 <see cref="FileNames"/> 里的那些文件，
        /// 列目录返回同样的条目（于是输出校验能通过）。与 ExtractionPipelineFixTests 的同一套，
        /// 只多了三个钩子：换个产物清单（造"第一层只出中间件"的形状）、注入解压失败、
        /// 在收尾那一刻按取消。
        /// </summary>
        private sealed class FakeEngine : IArchiveEngine
        {
            /// <summary>默认产物：5 个像内容物的文件。</summary>
            private static readonly string[] DefaultFileNames =
            {
                "payload-00000.bin", "payload-00001.bin", "payload-00002.bin",
                "payload-00003.bin", "payload-00004.bin"
            };

            /// <summary>解压时写出的文件名（也是列目录返回的条目）。</summary>
            public IReadOnlyList<string> FileNames { get; set; } = DefaultFileNames;

            /// <summary>非 null = 解压直接返回这个失败结论（用来验"失败时源包不动"）。</summary>
            public ArchiveOperationResult? ExtractFailure { get; set; }

            /// <summary>列目录前回调（收尾第一件事就是列目录：用来在那一刻按取消）。</summary>
            public Action<ArchiveRequest>? OnList { get; set; }

            /// <summary>列目录时报出的条目数；比真实产物多（校验就会不通过）。</summary>
            public int? ExpectedFileCountOverride { get; set; }

            /// <summary>解压是否已经发生过（取消类用例靠它区分"解压前"与"收尾时"）。</summary>
            public bool Extracted { get; private set; }

            public string Id => "fake";

            public string DisplayName => "假引擎";

            public string Version => "1.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new()
            {
                CanProbe = true,
                CanList = true,
                CanTest = true,
                CanExtract = true,
                SupportsPassword = true
            };

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });
            }

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                OnList?.Invoke(request);

                IReadOnlyList<string> fileNames = FileNames;
                int fileCount = ExpectedFileCountOverride ?? fileNames.Count;

                return Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = fileCount,
                    TotalUncompressedSize = 0,
                    Entries = Enumerable.Range(0, fileCount)
                        .Select(i => new ArchiveEntry
                        {
                            Path = i < fileNames.Count ? fileNames[i] : $"extra-{i:D5}.bin",
                            Size = 1
                        })
                        .ToList(),
                    EngineId = "fake",
                    EngineVersion = "1.0"
                });
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Succeeded());
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                if (ExtractFailure != null)
                {
                    return Task.FromResult(ExtractFailure);
                }

                string output = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);

                    foreach (string name in FileNames)
                    {
                        File.WriteAllText(Path.Combine(output, name), "x");
                    }
                }

                Extracted = true;

                return Task.FromResult(Succeeded());
            }

            private static ArchiveOperationResult Succeeded()
            {
                return new ArchiveOperationResult
                {
                    Success = true,
                    Status = StatusText.ExtractSuccess,
                    Message = "解压成功",
                    DetectedErrorType = "None"
                };
            }
        }

        /// <summary>
        /// 只接管"同盘 / 跨盘"这个判断与失败注入，真正的复制 / 删除仍落在临时目录里。
        /// </summary>
        private sealed class SwitchableSourceMoveFileSystem : ISourceMoveFileSystem
        {
            private readonly ISourceMoveFileSystem _inner = FileSystemSourceMoveFileSystem.Instance;

            public bool SameVolume { get; set; } = true;

            public Exception? MoveFailure { get; set; }

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
                _inner.CopyFile(source, target);
            }

            public void DeleteFile(string path) => _inner.DeleteFile(path);
        }
    }
}
