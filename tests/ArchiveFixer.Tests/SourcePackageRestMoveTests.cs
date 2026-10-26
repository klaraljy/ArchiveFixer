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
    // 碰进程级静态（构造 MainViewModel 会写工作区根）：与同类用例串行跑，
    // 不与别的集合并行 —— 见 InnerLayerContinuationTests 顶部的 CollectionDefinition。
    [Collection("ArchiveFixerGlobalState")]
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
        public async Task 手动只解压_彻底删除档_源包就地删掉()
        {
            /*
             * ⚠ 2026-10-15：这一条替换掉旧的 `手动只解压_成功后源包也移入其余物` —— 用户 2026-10-14 口径
             * 之后「放入其余物」那一档整条删除，手动「只解压」这条地基路径读的是**同一个**源包档
             * （`SourceHandlingMode`），所以它也必须跟着走「就地删」。
             */
            Harness harness = CreateHarness(
                settings => settings.SourceHandling = nameof(SourceHandlingMode.DeleteInPlace));

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(task.IsOutputVerified);

            // ① 源包**没了** —— 就地删（⛔ 不是搬进其余物）。
            Assert.False(File.Exists(source), "手动「只解压」在彻底删除档下：成功后源包必须被就地删掉");

            // ② ⛔ 不许建「其余物」目录、更不许把源包放进去。
            string restDirectory = Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName);
            Assert.False(Directory.Exists(restDirectory), "彻底删除档不该建其余物目录");
            Assert.False(File.Exists(Path.Combine(restDirectory, "pack.7z")));

            // ③ 内容物照常（5 个）。
            Assert.Equal(5, CountFiles(task.OutputPath, excludeArtifactDirectory: true));
        }

        /// <summary>
        /// <b>红线（不变量 1）</b>：解压失败 → 源包原地不动，其余物不生成。
        ///
        /// <para>⚠ 2026-10-15 强化：档位设成**最危险的那一档**（彻底删除）—— 这一条要证明的是
        /// "**即使用户选了彻底删除**，失败时也一个字节都不动"（只测默认档证明不了这条红线）。</para>
        /// </summary>
        [Fact]
        public async Task 手动只解压_失败时_源包留在原地且不生成其余物_即使选了彻底删除()
        {
            Harness harness = CreateHarness(
                settings => settings.SourceHandling = nameof(SourceHandlingMode.DeleteInPlace));

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            /*
             * ⚠ 2026-10-21：这里必须用**普通失败**，不能用"损坏压缩包"。
             * 用户 2026-10-14 的口径：引擎明说包损坏 ⇒ 自动把已经解出来的留下
             * （`IsDamagedArchive` 读 `EngineErrorTypes.CorruptedArchive`，唯一出口），
             * 那一档的状态格是「文件损坏」、机器终态按内容物分档 —— 它有自己的守门
             * （`PartialPublishTests`）。本条钉的是另一条红线：**普通失败 ⇒ 一个字节都不动**。
             */
            harness.Engine.ExtractFailure = new ArchiveOperationResult
            {
                Success = false,
                Status = StatusText.ExtractFailed,
                Message = "解压失败：引擎报错",
                DetectedErrorType = string.Empty
            };

            await harness.Coordinator.StartExtractAsync();

            // 普通失败 ⇒ 状态格就是 `ExtractFailed`（引擎原因进 ErrorMessage，不升级成「文件损坏」那一档）。
            Assert.Equal(StatusText.ExtractFailed, task.Status);
            Assert.True(File.Exists(source), "失败时源包必须原地不动（哪怕用户选了彻底删除）");
            Assert.Equal(SourcePackageMoveState.NotAttempted, task.SourcePackageMove);
            Assert.False(
                Directory.Exists(Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName)),
                "失败时不该生成其余物目录");
        }

        /// <summary>
        /// 同一条红线的取消版：用户按了「取消当前」→ 源包原地不动、其余物不生成。
        ///
        /// <para>⚠ 2026-10-15 两处修正：① 档位设成**最危险的那一档**（彻底删除）；
        /// ② 取消挂在 <c>OnExtract</c>（"任务真在跑"的那一刻）—— 挂收尾列目录那一刻会走
        /// `CancelCurrentTask` 的早退支，等于没取消（实测踩到）。</para>
        /// </summary>
        [Fact]
        public async Task 手动只解压_取消时_源包留在原地且不生成其余物_即使选了彻底删除()
        {
            Harness harness = CreateHarness(
                settings => settings.SourceHandling = nameof(SourceHandlingMode.DeleteInPlace));

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            // 任务真在跑的那一刻按下「取消当前」。
            harness.Engine.OnExtract = _ => harness.Coordinator.CancelCurrentTask();

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.Cancelled, task.Status);
            Assert.True(File.Exists(source), "取消时源包必须原地不动（哪怕用户选了彻底删除）");
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

        // ================================================================ ②之二 形状 B：内容物在第 2 层才出现

        /// <summary>
        /// 形状 B + **失败**（第二层没解开、内容物根本没出现）：⛔ 一个字节都不动源包，
        /// 也不许为源包生成其余物 —— 内容物没出来就没有"这次整理成功了"这回事。
        ///
        /// <para>⚠ 2026-10-15 强化：档位设成**最危险的那一档**（彻底删除）—— 钉的是
        /// "**即使用户选了彻底删除**，内容物没出来也一个字节都不动"。</para>
        /// </summary>
        [Fact]
        public async Task 形状B_内容物没出来_源包留在原地_即使选了彻底删除()
        {
            Harness harness = CreateHarness(
                settings => settings.SourceHandling = nameof(SourceHandlingMode.DeleteInPlace));

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

            // 第一层只出过程物 → 源包被记账成"待补搬"。
            harness.Engine.FileNames = new[] { "inner.7z.001" };
            await harness.Coordinator.StartExtractForOneClickAsync();

            string restDirectory = Path.Combine(root.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName);

            Assert.True(
                root.SourcePackageMove == SourcePackageMoveState.DeferredToChainEnd,
                $"实际 = {root.SourcePackageMove}；日志：\n" + string.Join("\n", harness.LogTexts));

            // 续解那一步没能产出内容物（第二层失败 / 没跑）→ 链尾补判时目录里只有过程物。
            await harness.Coordinator.CompleteRootSourcePackagesAfterChainAsync(new[] { root }, new[] { root });

            Assert.True(File.Exists(source), "内容物没出来时源包必须原地不动（哪怕用户选了彻底删除）");
            Assert.False(File.Exists(Path.Combine(restDirectory, "outer.7z")));
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("没有内容物", StringComparison.Ordinal) &&
                        line.Contains("源包留在原地", StringComparison.Ordinal));
        }

        /// <summary>
        /// 形状 B 的反面对照：内容物文件**落盘了、但校验没过**（引擎声明的条目数与实际不符）⇒
        /// ⛔ 源包不许动。
        ///
        /// <para>⚠ 2026-10-15 强化：档位设成**最危险的那一档**（彻底删除）—— 校验这一关必须真的过，
        /// 过不了就一个字节都不许删（⛔ 不是"用户选了删除就照删"）。</para>
        /// </summary>
        [Fact]
        public async Task 形状B_内容物校验没过_源包留在原地_即使选了彻底删除()
        {
            Harness harness = CreateHarness(
                settings => settings.SourceHandling = nameof(SourceHandlingMode.DeleteInPlace));

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

            // ① 第一层只出过程物 → 源包被记账成"待补搬"。
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

            Assert.True(File.Exists(source), "校验没过时源包必须原地不动（哪怕用户选了彻底删除）");
            Assert.False(File.Exists(Path.Combine(restDirectory, "outer.7z")));
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("源包留在原地", StringComparison.Ordinal));
        }

        /// <summary>
        /// ⛔ **L4（用户 2026-09-30 检验等级）：根任务"判不出完整性" ⇒ 一个字节都不动源包。**
        ///
        /// <para>现场：校验写着通过，但那是**底线校验** —— 机器手上根本没有可信清单
        /// （<c>OutputManifestCrossChecked = false</c>）。缺了这道闸门，用户那个**唯一无法再生**的源包
        /// 就会被删掉。</para>
        ///
        /// <para>判据的唯一出口是 <see cref="ResultCompletenessClassifier"/>：判不出 ⇒ 什么都不做，
        /// 结论如实写"无法确认完整性"（⛔ 既不许说成"校验通过"，也不许说成"校验失败"）。</para>
        ///
        /// <para>⚠ 2026-10-15 强化：档位设成**最危险的那一档**（彻底删除）—— 判不出完整性时必须
        /// **照样不删**（这条替换掉旧的"链尾不补搬源包"）。</para>
        /// </summary>
        [Fact]
        public async Task 形状B_根任务判不出完整性_一个字节都不动源包_即使选了彻底删除()
        {
            Harness harness = CreateHarness(
                settings => settings.SourceHandling = nameof(SourceHandlingMode.DeleteInPlace));
            ShapeBChain chain = await BuildShapeBChainAsync(harness);

            // 前提：管线本来**拿到过清单**（否则这条用例钉的就不是"判不出"那一档）。
            Assert.True(chain.Root.IsOutputVerified);
            Assert.True(chain.Root.OutputManifestCrossChecked);

            // 把"逐条核对过清单"这个事实抹掉 = 只剩非空底线校验那一档。
            chain.Root.OutputManifestCrossChecked = false;

            await harness.Coordinator.CompleteRootSourcePackagesAfterChainAsync(
                new[] { chain.Root }, new[] { chain.Root, chain.Continuation });

            Assert.True(File.Exists(chain.Source), "判不出完整性时源包必须原地不动（哪怕用户选了彻底删除）");
            Assert.False(File.Exists(Path.Combine(chain.RestDirectory, "outer.7z")));

            // ⛔ 源包**没有被处理过**（既没搬也没删）—— 不再钉某个具体中间态的名字。
            Assert.NotEqual(SourcePackageMoveState.Done, chain.Root.SourcePackageMove);

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("无法确认", StringComparison.Ordinal) &&
                        line.Contains("源包留在原地", StringComparison.Ordinal));
        }

        /// <summary>
        /// ⛔ **L4：链上任何一层判不出完整性 ⇒ 同样一个字节都不动源包。**
        ///
        /// <para>判据看的是**每一个把那个目录当落点的任务**，不是只看根任务 —— 用户那个形状里
        /// 内容物是续解子任务产出的，只核根任务等于放行"内容物那一层根本没证据"。</para>
        ///
        /// <para>⚠ 2026-10-15 强化：档位设成**最危险的那一档**（彻底删除）。</para>
        /// </summary>
        [Fact]
        public async Task 形状B_链上有一层判不出完整性_一个字节都不动源包_即使选了彻底删除()
        {
            Harness harness = CreateHarness(
                settings => settings.SourceHandling = nameof(SourceHandlingMode.DeleteInPlace));
            ShapeBChain chain = await BuildShapeBChainAsync(harness);

            Assert.True(chain.Continuation.IsOutputVerified);
            Assert.True(chain.Continuation.OutputManifestCrossChecked);

            // 产出内容物的那一层判不出 ⇒ "内容物已定稿并校验通过"这个事实不成立。
            chain.Continuation.OutputManifestCrossChecked = false;

            await harness.Coordinator.CompleteRootSourcePackagesAfterChainAsync(
                new[] { chain.Root }, new[] { chain.Root, chain.Continuation });

            Assert.True(File.Exists(chain.Source), "链上有判不出的层时源包必须原地不动（哪怕用户选了彻底删除）");
            Assert.False(File.Exists(Path.Combine(chain.RestDirectory, "outer.7z")));
            Assert.NotEqual(SourcePackageMoveState.Done, chain.Root.SourcePackageMove);
        }

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

            /*
             * ⚠ 2026-10-15：这里原来断言"内层分卷已经归到其余物里" —— 那是**旧口径**（过程物集中进其余物）。
             * 新口径下"源包/过程物搬进其余物"整条删除 ⇒ 改钉**这一片还在成品树里**（⛔ 不被搬走），
             * 位置与名字按新模型的实际形状来（过程物留在落点那一层）。
             */
            Assert.True(
                Directory.GetFiles(destinationDirectory, "inner.7z.001", SearchOption.AllDirectories).Length > 0,
                "那一份内层分卷应该还在成品树里（⛔ 新口径不再把它集中进其余物）");

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

            /*
             * ⚠ 2026-10-15：入口路径**从盘上找**，⛔ 不再写死 `其余物\inner.7z.001` ——
             * 新口径下"过程物集中进其余物"那条路已删，那一片就留在**成品树**里
             * （`BuildShapeBChainAsync` 里刚断言过它在成品树里）。写死旧位置会让这一单
             * 找不到文件、直接"解压失败"（实测：本类三条 shape-B 用例就是这么红的）。
             */
            string piecePath = Directory
                .GetFiles(destinationDirectory, "inner.7z.001", SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? Path.Combine(destinationDirectory, "inner.7z.001");

            var continuation = new ArchiveTask(piecePath, index)
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
        public async Task 一键处理_独立包成功立刻删源包_不再等整批结束()
        {
            /*
             * 用户 2026-10-26 口径：删除看**这一单自己**是否成功，不看整批是否全成功。
             * 独立单包成功、校验通过、定稿完成 ⇒ 立刻删自己的源包，不拖到批末。
             */
            Harness harness = CreateHarness(
                settings => settings.SourceHandling = nameof(SourceHandlingMode.DeleteInPlace));

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            await harness.Coordinator.StartExtractForOneClickAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.False(File.Exists(source), "独立包成功后应当立刻删除自己的源包");
            Assert.Equal(SourcePackageMoveState.Done, task.SourcePackageMove);
        }

        [Fact]
        public async Task 一键处理_彻底删除档_链尾把源包就地删掉_绝不搬进其余物()
        {
            /*
             * ⚠ 2026-10-15：这一条钉的是**链尾那一站**的行为 —— 一键处理把源包处理**延期**到链尾
             * （见上面那条用例），链尾按「彻底删除」**就地删**（⛔ 不是搬进其余物。
             * 用户逐字：「原包一直在原来的位置，选『彻底删除』也是就地删、不用移入其余物」）。
             *
             * <para>喂进去的是**真证据**：链尾那一站自己算出来的 L4 结论
             * （`ResultCompletenessClassifier.Classify(rootTask)`）+ 机器终态 + 分卷完整性 + 内容物存在，
             * ⛔ 不是放宽闸门。</para>
             */
            Harness harness = CreateHarness(
                settings => settings.SourceHandling = nameof(SourceHandlingMode.DeleteInPlace));

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            await harness.Coordinator.StartExtractForOneClickAsync();
            await harness.Coordinator.CompleteRootSourcePackagesAfterChainAsync(new[] { task }, new[] { task });

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // ① 源包**没了** —— 就地删。
            Assert.False(File.Exists(source), "链尾按「彻底删除」处理之后源包必须被就地删掉");

            // ② ⛔ 它**不许**出现在「其余物」里（"搬进其余物"那条路已随用户口径删除）。
            Assert.False(
                File.Exists(Path.Combine(
                    task.OutputPath,
                    ProcessArtifactLayout.ArtifactDirectoryName,
                    "pack.7z")),
                "源包⛔ 不许被搬进其余物（那条路已随用户口径删除）");
        }

        // ================================================================ ③ 失败与跨盘

        /// <summary>
        /// ⛔ <b>新口径（2026-10-15，用户 2026-10-14 拍板）：成功链上的过程物**无条件**彻底删 ——
        /// 与③页「删除操作」那一档**解绑**。</b>
        ///
        /// <para>用户口径逐字：「删除操作只针对**最后一层过程物**……**其他的过程物统一彻底删除**
        /// （因为他们都是成功的情况下），统一彻底删除」。⇒ 续解层跑成了，它消费掉的那个内层包
        /// （= 上一层的过程物）**当场就没了**，哪怕用户在③页选的是「留下」。</para>
        ///
        /// <para>判据三条：① 内容物真的出来了（不然"内层包没了"可能只是这一层压根没跑）；
        /// ② 续解层的源（<c>inner.7z.001</c>）**不在盘上**、也**不在其余物里**（是删掉不是搬走）；
        /// ③ 最外层源包一个字节都不动（「源包操作 = 留下」那一档，⛔ 过程物与源包是两件事）。</para>
        /// </summary>
        [Fact]
        public async Task 续解层跑成之后_它消费的过程物当场彻底删掉_即使删除操作选的是留下()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            });

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

            // ① 第一层只出过程物（内层分卷）：它**留在成品树里**（不再是"集中进其余物"）。
            harness.Engine.FileNames = new[] { "inner.7z.001" };
            await harness.Coordinator.StartExtractForOneClickAsync();

            string destinationDirectory = root.OutputPath;
            string restDirectory = Path.Combine(destinationDirectory, ProcessArtifactLayout.ArtifactDirectoryName);

            ArchiveTask continuation = AddContinuationTask(harness, restDirectory, destinationDirectory, root, index: 2);

            string consumed = continuation.CurrentPath;

            Assert.True(File.Exists(consumed), $"前提：这一层要消费的那一份必须先在盘上（{consumed}）");

            // ② 第二层跑成（产物与声明的条目数一致 ⇒ 校验通过）。
            harness.Engine.FileNames = new[] { "content.bin" };
            await harness.Coordinator.StartExtractForOneClickAsync();

            // 判据 ①：内容物出来了。
            Assert.Equal(StatusText.ExtractSuccess, continuation.Status);
            Assert.True(File.Exists(Path.Combine(destinationDirectory, "content.bin")));

            // 判据 ②：它消费掉的那份过程物**当场没了** —— 而且不是被搬进其余物。
            Assert.False(
                File.Exists(consumed),
                "成功链上的过程物必须当场彻底删掉（③页选「留下」也一样）");

            Assert.False(
                File.Exists(Path.Combine(restDirectory, Path.GetFileName(consumed))),
                "过程物是被**彻底删掉**，⛔ 不是搬进其余物");

            // 判据 ③：最外层源包一个字节都不动（「源包操作 = 留下」）。
            Assert.True(File.Exists(source), "源包操作 = 留下 ⇒ 源包原地不动");

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("逐层回收", StringComparison.Ordinal));
        }

        [Fact]
        public async Task 一键处理_彻底删除档_分卷组整组一起就地删掉()
        {
            /*
             * ⚠ 2026-10-15：这一条替换掉旧的 `一键处理_分卷组整组一起移入其余物` ——
             * 「搬进其余物」那条路已随用户口径删除，但**"整组一起处理"这条不变量照旧成立**
             * （决策 D-12：分卷组要么整组都不动、要么整组一起处理，⛔ 绝不许只动其中几卷 ——
             * 那会让用户再也拼不起来）。
             *
             * 判据：① 两卷都**没了**（就地删，⛔ 不在其余物里）；② 没有任何一卷被单独留下。
             */
            Harness harness = CreateHarness(
                settings => settings.SourceHandling = nameof(SourceHandlingMode.DeleteInPlace));

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

            // 一键处理的源包处理延期到链尾 ⇒ 补上链尾那一步。
            await harness.Coordinator.CompleteRootSourcePackagesAfterChainAsync(new[] { task }, new[] { task });

            string restDirectory = Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName);

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // ① 整组一起就地删掉 —— ⛔ 一卷都不许被落下（那等于把这一组拆散）。
            Assert.False(File.Exists(first), "第一卷没有被就地删掉");
            Assert.False(File.Exists(second), "第二卷被落下了（决策 D-12：整组一起处理）");

            // ② ⛔ 不许出现在「其余物」里（"搬进其余物"那条路已随用户口径删除）。
            Assert.False(File.Exists(Path.Combine(restDirectory, "222.7z.001")));
            Assert.False(File.Exists(Path.Combine(restDirectory, "222.7z.002")));
        }

        // ================================================================ 归集之后源包跟着走

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
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.AutoScanAfterDrop = false;

            /*
             * ⚠ 这一组测的是"源包搬进其余物"这条路，所以显式把档位设成 MoveToRest：
             * 2026-09-25 第 32 条把**默认档**改成了「原来的位置不动」（用户拍板），
             * 不显式设置的话这些用例测的就变成"默认不搬"了（那样它们会集体变红）。
             */
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);

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

            // 这个用例拿「细节日志」当行为证据（第 44 条之后，成功时默认只留两行）。
            coordinator.KeepTaskDetailInLog = true;

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
        /// 只多了三个钩子：换个产物清单（造"第一层只出过程物"的形状）、注入解压失败、
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

            /// <summary>
            /// **解压那一刻**的回调（"任务真在跑"的时刻）。
            ///
            /// <para>⚠ 取消类用例必须挂这里，⛔ 不能挂 <see cref="OnList"/>：实测（2026-10-15）挂在收尾列目录
            /// 那一刻，`CancelCurrentTask()` 走到"当前没有正在执行的任务可取消"那条早退支 ⇒ 取消根本没发生，
            /// 用例量到的是"没取消成功"而不是"取消后的行为"（这个坑在本项目已经踩过两次）。</para>
            /// </summary>
            public Action<ArchiveRequest>? OnExtract { get; set; }

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
                OnExtract?.Invoke(request);

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
