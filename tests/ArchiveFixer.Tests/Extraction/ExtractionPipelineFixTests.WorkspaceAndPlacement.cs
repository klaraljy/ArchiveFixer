using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 落点、工作区与清理的回归用例：
    /// 日志导出落点、安全上限、分卷首片、输出改名、越界结论、工作区清理、机器终态。
    /// </summary>
    public partial class ExtractionPipelineFixTests
    {


        // ================================================================ 日志导出的落点（2026-10-01 真机 giu910）

        /// <summary>
        /// **日志导出不许落在"程序自己会整份删掉"的地方**（其余物 / 工作区）。
        ///
        /// <para>用户 2026-10-01 真机 `giu910` 问「**这个是为什么，为什么**」：他把上一轮被收进其余物的源包
        /// 又跑了一遍，于是"导出日志"的保存对话框默认开在其余物里，那份 txt 就落在了一个
        /// **下一批成功就会被整份删掉**的文件夹里。</para>
        ///
        /// <para>判据唯一出口 <c>ProcessArtifactLayout.IsInsideDeletableProcessFolders</c>：
        /// 导出的**起始目录**与"记住上次导出到哪儿"都读它（⛔ 用户自己在对话框里挑哪儿，程序不干预）。</para>
        /// </summary>
        [Fact]
        public void 日志导出不记其余物和工作区这种会被删掉的目录()
        {
            Assert.True(ProcessArtifactLayout.IsInsideDeletableProcessFolders(@"H:\pkg\其余物"));
            Assert.True(ProcessArtifactLayout.IsInsideDeletableProcessFolders(@"H:\pkg\其余物\giu"));
            Assert.True(ProcessArtifactLayout.IsInsideDeletableProcessFolders(@"H:\pkg\.ArchiveFixer.work\recursive"));

            Assert.False(ProcessArtifactLayout.IsInsideDeletableProcessFolders(@"H:\BaiduNetdiskDownload\日志"));
            Assert.False(ProcessArtifactLayout.IsInsideDeletableProcessFolders(@"H:\pkg\普通目录"));
            Assert.False(ProcessArtifactLayout.IsInsideDeletableProcessFolders(null));
        }

        /// <summary>
        /// 安全上限也是**设置项**，而且必须真的管到解压管线（用户 2026-09-25 第 36 条）。
        ///
        /// <para>真机现场：他那个 12.22 GiB 的包里有 4.88 GiB 的单条目，而单文件上限当时是**硬编码**的
        /// 4 GiB —— 界面上一个字都没有，他看到的只有失败清单上那句像"这个包坏了"的判决。</para>
        ///
        /// <para>这条钉子把上限调到 1 GiB：同一个清单必须**当场**被拒、引擎一次都不许调，
        /// 而且任务上的原因要点名"安全上限"并带上调大的入口（撤掉接线立刻变红：
        /// 预检退回 <c>new ResourceBudget()</c> 的 4 GiB 默认值时，1.0001 GiB 的条目会被放行、引擎被调用）。</para>
        /// </summary>
        [Fact]
        public async Task 安全上限来自设置_调小之后当场拒绝且不调引擎()
        {
            Harness harness = CreateHarness(configure: settings => settings.MaxSingleExtractedFileGiB = 1);

            ArchiveTask task = AddTask(harness, CreateSourceFile("big-parts.7z"));

            long entrySize = (1L * 1024 * 1024 * 1024) + (1L * 1024 * 1024);

            harness.Engine.OnListAsync = _ => Task.FromResult(new ArchiveListResult
            {
                Success = true,
                FileCount = 1,
                TotalUncompressedSize = entrySize,
                Entries = new List<ArchiveEntry>
                {
                    // 名字刻意用他真机那个"括号没配对上"的卷名：文案里必须原样带出来（外面加「」）。
                    new() { Path = "Code Complete-BZ.7z(删掉.001", Size = entrySize }
                },
                EngineId = "fake",
                EngineVersion = "1.0"
            });

            await harness.Coordinator.StartExtractAsync();

            Assert.Empty(harness.Engine.ExtractCalls);
            Assert.Equal(StatusText.ExtractFailed, task.Status);
            Assert.Contains("安全上限", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("条目「Code Complete-BZ.7z(删掉.001」", task.ErrorMessage, StringComparison.Ordinal);
        }

        /// <summary>
        /// 「一组分卷的第一卷名字被改坏」时必须判「分卷缺失」，**不是**解出一个等大的垃圾文件还显示成功
        /// （用户 2026-09-25 第 36 条追加；真 7z 的这条行为由 <c>RawSplitStreamTests</c> 固化）。
        ///
        /// <para>真机链条：7-Zip 顺着名字找不到同组的后续卷 → 退化成通用分片 → 把这一卷**照解不误**（退出码 0）
        /// → 解出来一个与它等大的垃圾文件 → 结果校验拿"清单那一条"比"盘上那一个文件"会判**通过**
        /// → 用户拿到垃圾却看到「成功」。所以管线要在**写盘之前**拦下：引擎一次都不许调。</para>
        /// </summary>
        [Fact]
        public async Task 第一卷名字被改坏时_判分卷缺失而不是解出一个垃圾文件()
        {
            Harness harness = CreateHarness();

            string directory = Path.Combine(_root, "src");
            Directory.CreateDirectory(directory);

            string firstVolume = Path.Combine(directory, "Code Complete-BZ.7z(删掉.001");

            File.WriteAllText(firstVolume, "not a real archive - the engine is faked in these tests");

            // 同目录里像后续卷的那两个（真机上它们就躺在旁边，只是名字对不上）
            File.WriteAllText(Path.Combine(directory, "Code Complete-BZ.7z.002"), "x");
            File.WriteAllText(Path.Combine(directory, "Code Complete-BZ.7z.003"), "x");

            ArchiveTask task = AddTask(harness, firstVolume);

            long size = new FileInfo(firstVolume).Length;

            harness.Engine.OnListAsync = _ => Task.FromResult(new ArchiveListResult
            {
                Success = true,
                FileCount = 1,
                TotalUncompressedSize = size,
                Entries = new List<ArchiveEntry> { new() { Path = "Code Complete-BZ.7z(删掉", Size = size } },
                IsRawSplitStream = true,
                EngineId = "fake",
                EngineVersion = "1.0"
            });

            await harness.Coordinator.StartExtractAsync();

            /*
             * ⚠ 2026-10-23 用户口径：「第一大步只改第一大步的名字，一定要分开弄」——
             * 分卷改名归第二大步，所以这里名字**原样留着**；本条只钉"不许把垃圾文件当成功解出来"：
             * 状态不能是解压成功，这一单按分卷成员跳过、留给第二大步。
             */
            Assert.True(File.Exists(firstVolume), "第一大步不许改分卷名（改名归第二大步）");
            Assert.False(
                File.Exists(Path.Combine(directory, "Code Complete-BZ.7z.001")),
                "不该被改名成标准名");
            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            // NOTE: unreadable content => conservative verdict may be VolumeMissing; only "not success" is required here.
        }

        // ================================================================ P1-6：实际输出目录写回任务


        /// <summary>
        /// 输出目录已存在且非空时会自动改用 <c>xxx(1)</c>。
        /// 旧行为只把这件事写进日志，任务对象上的 <see cref="ArchiveTask.OutputPath"/> 是"打算输出到哪"；
        /// 一键处理的续解就按解压前的目录快照找内层包，于是第二层静默不跑，汇总却还写"完成"。
        ///
        /// 现在：实际落点写回 <see cref="ArchiveTask.OutputPath"/>（界面"输出目录"列直接看得见），
        /// 校验结论里带一句 <see cref="ArchiveTask.VerifyMessage"/>，日志里也明说一次。
        /// </summary>
        [Fact]
        public async Task 输出目录冲突自动改名_实际落点写回任务且看得见()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("pack.7z"));

            string requested = harness.PathService.BuildOutputPath(task, DefaultOptions(harness));

            // 现场：目标目录已存在且非空（重跑一次、上次失败留下的目录都会这样）。
            Directory.CreateDirectory(requested);
            File.WriteAllText(Path.Combine(requested, "上次留下的旧文件.txt"), "old");

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
                WriteFiles(request.OutputPath!, 2);
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(2));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            /*
             * ① 写回的是**实际**落点（用户看得见的那个目录），不是解压前算出来的那个。
             * 入仓之后引擎写的是暂存目录，产物由定稿搬进最终目录 —— 所以这两件事要分开断言：
             * 引擎的落点在工作区里，任务的落点是改名后的 out\pack(1)。
             */
            Assert.NotEqual(requested, task.OutputPath);
            Assert.True(Directory.Exists(task.OutputPath));
            Assert.Equal(2, CountFiles(task.OutputPath));
            Assert.StartsWith(
                harness.PathService.WorkDirectory,
                harness.Engine.LastExtractOutputPath,
                StringComparison.OrdinalIgnoreCase);

            // ② 旧目录里的东西一个都没动（绝不覆盖/清空既有文件）。
            Assert.True(File.Exists(Path.Combine(requested, "上次留下的旧文件.txt")));

            // ③ "输出到别处"这件事在任务与日志里都看得见，不许静默。
            Assert.Contains("实际输出到", task.VerifyMessage, StringComparison.Ordinal);
            Assert.Contains(
                harness.Log.Logs.Select(x => x.Message),
                message => message.Contains("本次实际输出目录", StringComparison.Ordinal) &&
                           message.Contains(task.OutputPath, StringComparison.OrdinalIgnoreCase));
        }

        // ================================================================ 不变量 4：单层落点越界 = 失败结论
        /// <summary>
        /// 单层解压的"解压后落点校验"以前只写一行 ERROR 日志：任务照样是**解压成功**，
        /// 产物照样被归集走、源包照样被删 —— 越界的产物已经落在目标根之外，程序却报一切正常，
        /// 等于把不变量 4 降级成一条没人看的提示。
        ///
        /// 现场：产物目录里有个**指向目录外的目录联接点**（名字干干净净，第一道"条目名预检"看不出问题，
        /// 真实落点却在外面）—— 这正是第二道落点校验存在的理由。用联接点而不是符号链接：
        /// 前者（mklink /J）不需要管理员权限。
        ///
        /// 判据（三条一起才说明口径改对了）：①不是成功状态 ②没有归集 ③没有清理源包。
        /// 这里刻意让结果校验**通过**（产物数量对得上）——否则"跳过归集/清理"是校验失败的副作用，
        /// 测不到本次修的那条路。
        /// </summary>
        [Fact]
        public async Task 单层产物越出目标根目录_不算成功且不归集不清理()
        {
            string collectRoot = Path.Combine(_root, "collect");

            Harness harness = CreateHarness(configure: settings =>
            {
                settings.CollectResultsToDirectory = true;
                settings.CollectTargetDirectory = collectRoot;

                // 开着"会动源包"的那两档才有意义：越界结论下源文件一个都不许少。
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
            });

            string source = CreateSourceFile("escape.7z");
            ArchiveTask task = AddTask(harness, source);

            string outside = Path.Combine(_root, "outside");

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                string output = request.OutputPath ?? string.Empty;
                harness.Engine.LastExtractOutputPath = output;

                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "innocent.txt"), "正常产物");

                Directory.CreateDirectory(outside);

                string link = Path.Combine(output, "link-out");

                if (!Directory.Exists(link))
                {
                    CreateJunction(link, outside);
                }

                File.WriteAllText(Path.Combine(link, "escaped.txt"), "越界产物");

                harness.Engine.Extracted = true;
                return Succeeded();
            });

            // 清单说 1 个文件 → 结果校验会通过（innocent.txt 1 个，联接点里的那份按"不跟随链接"不计）。
            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(1));

            await harness.Coordinator.StartExtractAsync();

            // ① 结论：不是成功，而是失败，且原因写在任务上（不是只躺在日志里）。
            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.Equal(StatusText.ExtractFailed, task.Status);
            Assert.Contains("越出目标根目录", task.ErrorMessage, StringComparison.Ordinal);
            Assert.False(task.IsOutputVerified, "结论不成立时不许显示「输出校验通过」");

            // 日志里也要有证据（无界面宿主时日志是唯一线索），而且不许再出现"解压成功：xxx"。
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("越出目标根目录", StringComparison.Ordinal));
            Assert.DoesNotContain(
                harness.Log.Logs,
                x => x.Message.Contains("解压成功：", StringComparison.Ordinal));

            // ② 不清理源包（AGENTS.md §9.5：校验失败/结论不成立一律不删）。
            Assert.True(File.Exists(source), "越界结论下源包被删了");

            // ③ 不归集：目标目录连建都不该建，产物留在原地。
            Assert.False(Directory.Exists(collectRoot), "越界结论下产物被归集走了");
            Assert.Equal(string.Empty, task.CollectedPath);

            /*
             * 默认档（用户 2026-09-25 第 25 条追加）：这一单没成功 → 工作区整份清掉。
             * ⚠ 这一条用例的工作区里有一个**目录联接点**（那正是越界的来源）：Windows 会拒绝递归删除它，
             * 于是走"删不掉"那一条路 —— 那时**必须**留一条 WARN 说清"没清掉 + 路径"，
             * 绝不许假装清干净了（这条同时钉住"一次清理失败绝不改任务结论"）。
             * 要害始终是"定稿一步都没跑"：最终目录连建都不该建、归集目录一个字节都没有、
             * 越界落点的那个目录也没被我们删。
             */
            string taskWorkDirectory = TaskWorkDirectory(harness, task);

            if (Directory.Exists(taskWorkDirectory))
            {
                Assert.Contains(
                    harness.Log.Logs,
                    x => x.Level == "WARN" &&
                         x.Message.Contains("工作区没清掉", StringComparison.Ordinal) &&
                         x.Message.Contains(taskWorkDirectory, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                Assert.False(
                    Directory.Exists(TaskStageDirectory(harness, task)),
                    "工作区都清掉了，暂存目录不该还在");
            }

            Assert.False(Directory.Exists(task.OutputPath), $"越界结论下最终目录被建出来了：{task.OutputPath}");
            Assert.True(Directory.Exists(outside), "越界落点的目录本身不该被我们删掉");
        }

        // ================================================================ 中间工作区清理（成功后不留 1 GB 垃圾）

        /// <summary>
        /// 双面文件（内嵌归档）要先按偏移把尾部那段 ZIP 抠进 <c>work\&lt;任务名&gt;\</c> 再解压。
        /// 端到端验收实测：一次**成功**的一键处理在那里留下近 1 GB 过程物（733 MB + 167 MB）。
        /// 这些是从源文件可再生的派生数据 —— 成功且校验通过后必须清掉。
        /// </summary>
        [Fact]
        public async Task 成功解压且校验通过后_本任务的中间工作区被清理()
        {
            Harness harness = CreateHarness();
            string source = CreateEmbeddedSourceFile("embedded-ok.7z");

            ArchiveTask task = AddTask(harness, source);
            task.EmbeddedArchiveOffset = EmbeddedPaddingBytes;

            string taskWorkDirectory = TaskWorkDirectory(harness, task);

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
                WriteFiles(request.OutputPath!, 1);
                harness.Engine.Extracted = true;
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(1));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 抠包是真的发生了（否则这条测试证明不了任何事）：引擎拿到的是工作区里那个抠出来的文件。
            Assert.StartsWith(
                harness.PathService.WorkDirectory,
                harness.Engine.ExtractCalls.Single(),
                StringComparison.OrdinalIgnoreCase);

            Assert.False(Directory.Exists(taskWorkDirectory), $"成功之后中间工作区还在：{taskWorkDirectory}");

            // 源文件一个字节都不许动（AGENTS.md 不变量 1）。
            Assert.True(File.Exists(source), "清理中间工作区时把源文件删了");

            // 删什么、为什么，日志里必须留得下（不可逆操作要留证据）。
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("清理本任务的中间工作区", StringComparison.Ordinal) &&
                     x.Message.Contains("校验通过", StringComparison.Ordinal));
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("中间工作区已清理", StringComparison.Ordinal));

            /*
             * 成功这一支**不是**靠"失败不留残留"那条新逻辑清的（用户 2026-09-25 第 25 条追加）：
             * 那一条的日志是「已清理工作区…（这次没成功…）」，这里一条都不许出现 ——
             * 否则"成功路径的清理口径一个字不改"就成了空话，两套清理也会互相掩盖。
             */
            Assert.DoesNotContain(
                harness.Log.Logs,
                x => x.Message.Contains("已清理工作区：", StringComparison.Ordinal));
        }

        /// <summary>
        /// 取消：中间工作区默认**整份清掉**（用户 2026-09-25 第 25 条追加）。
        ///
        /// <para>旧口径是"取消时过程物是用户唯一还能看的东西，一律留着"（2026-09-24 第 23 条）。
        /// 用户后来说得很直接："失败了就失败了，成功了就成功了"+"我不希望有这么多的失败残留"，
        /// 而且失败清单已经能说清每个包为什么失败 —— 所以默认档改成不留现场；
        /// 要看现场的人在 ③ 页打开「失败时保留中间产物」（那一条有独立用例钉住）。</para>
        /// </summary>
        [Fact]
        public async Task 取消后_中间工作区默认被清掉()
        {
            Harness harness = CreateHarness();
            string source = CreateEmbeddedSourceFile("embedded-cancel.7z");

            ArchiveTask task = AddTask(harness, source);
            task.EmbeddedArchiveOffset = EmbeddedPaddingBytes;

            string taskWorkDirectory = TaskWorkDirectory(harness, task);

            /*
             * 取消的时刻 = **产物已经写完**（"任务真在跑"的那一刻）。
             *
             * ⚠ 2026-10-16 重摆 + 红检（两条都是实测取的运行期事实，⛔ 不是读代码推的）：
             * ① 原来挂在 `OnListAsync` 里"解压已完成才按取消"，而新流程把列目录挪到了**解压之前**
             *    （识别 / 空间门 / 层计划各一次）—— 探针实测 `listCalls=3`、日志里**一条取消记录都没有**
             *    （`CancelCurrentTask` 从未被调用）⇒ 取消根本没发生，跑完落「解压成功」；
             * ② ⛔ **本用例钉的是行为、不是收尾内部那两处检查**：把 `PostProcessSuccessAsync` 入口
             *    与中段那两句 `ThrowIfCancellationRequested()` 临时撤掉，用例**照旧绿** ⇒ 看见这次取消的
             *    是更早的那一道闸门。判据意图一个字没改（**取消 ⇒ 状态「已取消」＋ 源包原地不动
             *    ＋ 产物一个都不许移动**），但⛔ 别把它读成"收尾内部的取消检查已验过"——
             *    那个缺口已如实记进 `docs/大改动-2026-10-14-口径与冲突.md`。
             */
            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
                WriteFiles(request.OutputPath!, 5);
                harness.Engine.Extracted = true;

                // 产物已经在盘上了：此刻按取消，收尾必须看得见它。
                harness.Coordinator.CancelCurrentTask();
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(5));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.Cancelled, task.Status);
            Assert.False(Directory.Exists(taskWorkDirectory), $"取消之后中间工作区还在：{taskWorkDirectory}");
            Assert.True(File.Exists(source), "取消时源文件必须原样保留");

            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("已清理工作区：", StringComparison.Ordinal) &&
                     x.Message.Contains("这次没成功", StringComparison.Ordinal));
        }

        /// <summary>失败（密码不对）：默认同样整份清掉 —— 用户要的是"失败了就失败了"。</summary>
        [Fact]
        public async Task 解压失败后_中间工作区默认被清掉()
        {
            Harness harness = CreateHarness();
            string source = CreateEmbeddedSourceFile("embedded-fail.7z");

            ArchiveTask task = AddTask(harness, source);
            task.EmbeddedArchiveOffset = EmbeddedPaddingBytes;

            string taskWorkDirectory = TaskWorkDirectory(harness, task);

            harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
            harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.WrongPassword, task.Status);
            Assert.False(Directory.Exists(taskWorkDirectory), $"解压失败之后中间工作区还在：{taskWorkDirectory}");
            Assert.True(File.Exists(source), "失败时源文件必须原样保留");

            // 删了什么要说清：几个文件 / 多大 / 怎么改主意（去 ③ 页打开保留开关）。
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("已清理工作区：", StringComparison.Ordinal) &&
                     x.Message.Contains("失败时保留中间产物", StringComparison.Ordinal));
        }

        // ================================================================ 递归路径的工作区（data\work\recursive）

        /// <summary>
        /// 递归模式（<c>RecursionMode != SingleLayer</c>）的这一路同样不许漏垃圾，而且**有两处**：
        /// ① 递归核心自己的逐层工作区（<c>data\work\recursive\&lt;taskId&gt;\</c>，动辄几百 MB）；
        /// ② 双面文件抠出来的过程物所在的 <c>work\&lt;任务名&gt;\</c>。
        /// 递归成功后两处都必须清掉 —— 否则"解压成功"就是一次几百 MB 的泄漏。
        /// </summary>
        [Fact]
        public async Task 递归成功且产物发布后_抠出来的过程物与递归工作区一起被清理()
        {
            Harness harness = CreateHarness(configure: settings => { });
            string source = CreateEmbeddedSourceFile("embedded-recursive.7z");

            ArchiveTask task = AddTask(harness, source);
            task.EmbeddedArchiveOffset = EmbeddedPaddingBytes;

            string taskWorkDirectory = TaskWorkDirectory(harness, task);
            string recursiveRoot = Path.Combine(harness.PathService.WorkDirectory, "recursive");

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
                WriteFiles(request.OutputPath!, 1);
                harness.Engine.Extracted = true;
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(1));

            // 递归工作区根目录是**进程级静态**（见 CreateHarness 的说明），用完必须还原。
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;

            try
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = harness.PathService.WorkDirectory;

                await harness.Coordinator.StartExtractAsync();
            }
            finally
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            }

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 抠包真的发生了，而且这一单确实走的递归核心（引擎拿到的是工作区里抠出来的那个文件）。
            Assert.StartsWith(
                harness.PathService.WorkDirectory,
                harness.Engine.ExtractCalls.Single(),
                StringComparison.OrdinalIgnoreCase);

            // 产物已经被递归核心发布出去 —— 清理工作区不能连产物一起清掉。
            Assert.True(
                File.Exists(Path.Combine(task.OutputPath, "payload-00000.bin")),
                $"递归产物应当已经发布：{task.OutputPath}");

            Assert.False(Directory.Exists(taskWorkDirectory), $"递归成功之后中间工作区还在：{taskWorkDirectory}");

            // 递归核心自己的逐层工作区也必须清干净（这一条就是"几百 MB 泄漏"的钉子）。
            Assert.Equal(0, CountSubdirectories(recursiveRoot));

            // 源文件一个字节都不许动（AGENTS.md 不变量 1）。
            Assert.True(File.Exists(source), "清理中间工作区时把源文件删了");
        }

        /// <summary>
        /// **成功那一行的结论句不许把"入仓目录"说成产物 / 已发布**（2026-10-07 真机 EEEE）。
        ///
        /// <para>现场：`111(3).rar` 报「解压成功 100%」，①页「错误信息」列却写着
        /// 「已完成 1 层递归解压（没有更多内层归档）；产物：…\.ArchiveFixer.work\111(3).rar-84ed4efb\stage；
        /// 已发布 1 个文件到 …（同一个 stage）」—— 用户读成"报成功、产物却留在工作区没发布"，
        /// 而日志下一行其实已经是「定稿完成 → 落点」。</para>
        ///
        /// <para>判据：递归的发布目标落在**工作区根里面** ⇒ 那是这一单的**入仓目录**，不是落点。
        /// 结论句只许说「已入仓 N 个文件（中间产物，定稿时才搬进落点）」，
        /// ⛔ 不许出现「产物：…」或「已发布 N 个文件到 …」。</para>
        ///
        /// <para><b>红检</b>：把 <c>RecursiveExtractor</c> 里 <c>intakeStaging:</c> 那一句与
        /// <c>BuildSummary</c> 里 <c>!intoIntakeStaging</c> 那一半撤掉 ⇒ 本条变红。</para>
        /// </summary>
        [Fact]
        public async Task 成功那一行_入仓目录不许被说成产物或已发布()
        {
            Harness harness = CreateHarness(configure: settings => { });
            string source = CreateEmbeddedSourceFile("embedded-recursive-intake.7z");

            ArchiveTask task = AddTask(harness, source);
            task.EmbeddedArchiveOffset = EmbeddedPaddingBytes;

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
                WriteFiles(request.OutputPath!, 1);
                harness.Engine.Extracted = true;
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(1));

            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;

            try
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = harness.PathService.WorkDirectory;

                await harness.Coordinator.StartExtractAsync();
            }
            finally
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            }

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            string summary = task.ErrorMessage ?? string.Empty;

            Assert.Contains("已入仓", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("产物：", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("已发布", summary, StringComparison.Ordinal);
        }

        /// <summary>
        /// 递归失败（第一层就没解开，一个文件都没产出）：**两处**工作区默认都要清掉
        /// （用户 2026-09-25 第 25 条追加）。
        ///
        /// <para>用户原话：「如果解压 40G，两层，解压失败有 80G 的卸载残留，用户不得气死」——
        /// 双层包失败时留得最多的正是递归核心那份逐层工作区，所以这一条同时钉两处：
        /// ① 抠出来的过程物所在的 <c>work\&lt;任务名&gt;\</c>；② <c>work\recursive\&lt;taskId&gt;\</c>。
        /// 结论是**失败**（密码错误那一档），源包一个字节不动（不变量 1）。</para>
        ///
        /// <para>⚠ 状态口径 2026-09-27 改过一次（用户真机 `giu.7z.001`）：**什么都没产出**时
        /// 过去落「部分完成」，现在落「密码错误 / 文件损坏 / 解压失败」那一档 ——
        /// "部分完成"会让用户去暂存目录里找根本不存在的产物。
        /// 「解出来一半再失败」仍然是「部分完成」（见 <c>BatchSummaryDiagnosticsTests</c> 那条反向对照）。</para>
        /// </summary>
        [Fact]
        public async Task 递归失败后_两处工作区默认都被清掉()
        {
            Harness harness = CreateHarness(configure: settings => { });
            string source = CreateEmbeddedSourceFile("embedded-recursive-fail.7z");

            ArchiveTask task = AddTask(harness, source);
            task.EmbeddedArchiveOffset = EmbeddedPaddingBytes;

            string taskWorkDirectory = TaskWorkDirectory(harness, task);
            string recursiveRoot = Path.Combine(harness.PathService.WorkDirectory, "recursive");

            harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
            harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;

            try
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = harness.PathService.WorkDirectory;

                await harness.Coordinator.StartExtractAsync();
            }
            finally
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            }

            // 递归没走完、且一个文件都没产出 → **失败**（绝不显示成功，也绝不显示成"做了一半"）。
            Assert.Equal(StatusText.WrongPassword, task.Status);
            Assert.Equal(TaskOutcome.Failed, task.Outcome);
            Assert.False(Directory.Exists(taskWorkDirectory), $"递归失败之后中间工作区还在：{taskWorkDirectory}");

            // 递归核心的逐层工作区（几百 MB / 几十 GB 的那一份）也必须清干净。
            Assert.Equal(0, CountSubdirectories(recursiveRoot));

            Assert.True(File.Exists(source), "失败时源文件必须原样保留");
        }

        /// <summary>
        /// 资源预算在解压前拒绝时，必须把**机器终态**也落成 Failed。
        /// 912200014 现场：状态写着「解压失败」，Outcome 却是 Pending，批末汇总把它算成
        /// 「未处理」；这里用假引擎给一个超过单文件上限的清单，直接钉住这条。
        /// </summary>
        [Fact]
        public async Task 资源预算未通过_机器终态必须是失败()
        {
            Harness harness = CreateHarness(configure: settings =>
            {
                settings.MaxSingleExtractedFileGiB = 1;
            });

            ArchiveTask task = AddTask(harness, CreateSourceFile("bomb.7z"));

            harness.Engine.OnListAsync = _ => Task.FromResult(new ArchiveListResult
            {
                Success = true,
                FileCount = 1,
                TotalUncompressedSize = 2L * 1024 * 1024 * 1024,
                Entries = new List<ArchiveEntry>
                {
                    new() { Path = "bomb.bin", Size = 2L * 1024 * 1024 * 1024 }
                },
                EngineId = "fake",
                EngineVersion = "1.0"
            });

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractFailed, task.Status);
            Assert.Equal(TaskOutcome.Failed, task.Outcome);
            Assert.Contains("资源预算未通过", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Empty(harness.Engine.ExtractCalls);
        }
    }
}
