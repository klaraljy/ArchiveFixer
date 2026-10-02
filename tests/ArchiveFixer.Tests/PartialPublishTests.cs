using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「部分完成也把已解出的内容放进目标目录」——**发布动作本身**（用户 2026-10-02 拍板的口径 A）。
    ///
    /// <para>三块各测各的，缺一块这一档就不成立：</para>
    /// <list type="number">
    /// <item><description><b>入口三件事的顺序</b>（<see cref="PartialPublishRunner"/>）：对账 → **发布前二次空间体检** → 真搬。
    /// 空间不够时**一个字节都不搬**（兜底落在"什么都不做"那一档）；</description></item>
    /// <item><description><b>其余物的"半份清理"</b>（<see cref="RestItemPurger.PurgeExcept"/>）：留最外层源包 + 已解出内容物，
    /// 其余过程物删掉；**源包不在盘上就一个字节都不删**（用户 2026-10-02 定的安全闸门）；</description></item>
    /// <item><description><b>管线接线</b>（<see cref="ExtractionCoordinator"/>）：失败 + 开关打开 ⇒ 真的发布；
    /// 开关关 / 空间不足模式 / 取消 ⇒ 什么都不发布；源包在任何一档下都原地不动（红线）。</description></item>
    /// </list>
    ///
    /// <para>管线那一组与 <c>ExtractionPipelineFixTests</c> 同一组（MainViewModel 的构造会写进程级静态）。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class PartialPublishTests : IDisposable
    {
        private readonly string _root;

        public PartialPublishTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerPartialPublish", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论。
            }
        }

        // ================================================================ 一、入口三件事的顺序

        /// <summary>清单读不出来（没有条目）⇒ **一个字节都不发布**，连空间体检都不做。</summary>
        [Fact]
        public void 没有清单时_不发布也不探空间()
        {
            (string staging, string output) = CreatePublishScenario(payloadCount: 3, manifestCount: 0);

            bool probed = false;

            PartialPublishOutcome outcome = PartialPublishRunner.Run(
                staging,
                output,
                manifestEntries: Array.Empty<(string, long)>(),
                probe: (_, _) =>
                {
                    probed = true;
                    return null;
                });

            Assert.False(outcome.Published);
            Assert.Equal(PartialPublishPlanner.ReasonNoManifest, outcome.Verdict.ReasonCode);
            Assert.False(probed, "判不出该不该发布时不该去探空间 —— 顺序是「先对账、再体检」");
            Assert.False(Directory.Exists(Path.Combine(output, PartialPublisher.DirectoryName)));
        }

        /// <summary>
        /// **发布前二次空间体检**：对账通过、但这一刻剩余空间不够 ⇒ **一个字节都不搬**。
        /// 判据 = 兜底永远落在"什么都不做"那一档。
        /// </summary>
        [Fact]
        public void 二次空间体检不过_一个字节都不搬()
        {
            (string staging, string output) = CreatePublishScenario(payloadCount: 3, manifestCount: 3);

            PartialPublishOutcome outcome = PartialPublishRunner.Run(
                staging,
                output,
                Manifest(3),
                probe: (_, required) =>
                {
                    Assert.Equal(3, required);
                    return "可用空间不足：可用 1 字节";
                });

            Assert.False(outcome.Published);
            Assert.True(outcome.BlockedBySpace);
            Assert.Contains("可用空间不足", outcome.Reason, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(output, PartialPublisher.DirectoryName)));
            Assert.Equal(3, Directory.GetFiles(staging).Length);
        }

        /// <summary>对账通过 + 空间够 ⇒ 发布到 <c>部分完成\</c>，而且搬走的文件从暂存目录里消失了。</summary>
        [Fact]
        public void 对账通过且空间够_发布进部分完成目录()
        {
            (string staging, string output) = CreatePublishScenario(payloadCount: 3, manifestCount: 3);

            PartialPublishOutcome outcome = PartialPublishRunner.Run(staging, output, Manifest(3));

            Assert.True(outcome.Published, outcome.Reason);
            Assert.Equal(3, outcome.PublishedCount);
            Assert.Equal(3, Directory.GetFiles(Path.Combine(output, PartialPublisher.DirectoryName)).Length);
            Assert.Empty(Directory.GetFiles(staging));
        }

        /// <summary>
        /// 缺得太多（阈值不过）⇒ 不发布。用户口径：557/558 值得发，1/558 发出去只会污染目标目录。
        /// </summary>
        [Fact]
        public void 缺得太多_不发布()
        {
            (string staging, string output) = CreatePublishScenario(payloadCount: 1, manifestCount: 20);

            PartialPublishOutcome outcome = PartialPublishRunner.Run(staging, output, Manifest(20));

            Assert.False(outcome.Published);
            Assert.Equal(PartialPublishPlanner.ReasonTooMuchMissing, outcome.Verdict.ReasonCode);
        }

        // ================================================================ 二、其余物的"半份清理"

        /// <summary>
        /// 源包还在盘上 ⇒ 其余物里**除它以外**的过程物都删掉；要保留的那一份（在其余物里时）一项都不动。
        /// </summary>
        [Fact]
        public void 源包在盘上_其余物里除源包以外的项都删掉()
        {
            (ArchiveTask task, string restDirectory) = CreatePurgeScenario();

            string sourcePackage = Path.Combine(_root, "src", "222.7z");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePackage)!);
            File.WriteAllText(sourcePackage, "源包（还在盘上）");

            // 源包恰好也躺在其余物里的那一档（上一轮成功搬进去的）：它必须被保留。
            string keptInRest = Path.Combine(restDirectory, "222.7z");
            File.WriteAllText(keptInRest, "源包副本");

            RestPurgeOutcome result = new RestItemPurger().PurgeExcept(
                task,
                new[] { keptInRest },
                DeleteMode.Permanent);

            Assert.True(result.Succeeded, result.Message);

            Assert.True(File.Exists(keptInRest), "要保留的那一份（源包）不许被删");
            Assert.False(File.Exists(Path.Combine(restDirectory, "inner-222.7z.001")), "过程物该删");
            Assert.False(File.Exists(Path.Combine(restDirectory, "222.7z.002")), "过程物该删");
            Assert.True(Directory.Exists(restDirectory), "只是删掉里面的项，其余物目录本身留着");
        }

        /// <summary>
        /// **安全闸门**：这条链的最外层源包不在盘上 ⇒ 一个字节都不删
        /// （用户 2026-10-02；源包是唯一能把整条链重新解出来的东西）。
        /// </summary>
        [Fact]
        public void 最外层源包不在盘上_一个字节都不删()
        {
            (ArchiveTask task, string restDirectory) = CreatePurgeScenario();

            string missingSource = Path.Combine(_root, "src", "gone.7z");

            RestPurgeOutcome result = new RestItemPurger().PurgeExcept(
                task,
                new[] { missingSource },
                DeleteMode.Permanent);

            Assert.False(result.Attempted, result.Message);
            Assert.True(File.Exists(Path.Combine(restDirectory, "inner-222.7z.001")));
            Assert.True(File.Exists(Path.Combine(restDirectory, "222.7z.002")));
            Assert.Contains("一个都不在盘上", result.Message, StringComparison.Ordinal);
        }

        /// <summary>其余物里除了要保留的以外没别的项 ⇒ 不假装删过（Attempted=false，如实说）。</summary>
        [Fact]
        public void 其余物里只有要保留的那一份_不假装删过()
        {
            (ArchiveTask task, string restDirectory) = CreatePurgeScenario();

            File.Delete(Path.Combine(restDirectory, "inner-222.7z.001"));
            File.Delete(Path.Combine(restDirectory, "222.7z.002"));

            string kept = Path.Combine(restDirectory, "222.7z");
            File.WriteAllText(kept, "源包");

            RestPurgeOutcome result = new RestItemPurger().PurgeExcept(
                task,
                new[] { kept },
                DeleteMode.Permanent);

            Assert.False(result.Attempted, result.Message);
            Assert.True(File.Exists(kept));
        }

        // ================================================================ 三、空间账（用户 2026-10-02 顾虑 2）

        /// <summary>
        /// **部分完成发布开着时，放行判据要加一整个源包**：那一档跑完源包一定还在盘上
        /// ⇒ 这一单跑完盘上净多一整个源包的占用。⛔ 不加就等于把"两份"的承诺按"一份"排计划。
        ///
        /// <para>对照：开关关着时判据仍然**不含源包**（2026-09-29 真机那条红线，一个字都没放宽）。</para>
        /// </summary>
        [Fact]
        public void 部分完成发布开着_判据要加一整个源包()
        {
            string sourcePath = Path.Combine(_root, "src", "space.7z");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllBytes(sourcePath, new byte[4096]);

            var task = new ArchiveTask(sourcePath, 1) { FileName = "space.7z" };

            TaskSpaceEstimate plain = SpaceEstimator.FromSourceFiles(task);
            TaskSpaceEstimate withPartial = SpaceEstimator.FromSourceFiles(
                task,
                directReadAvailable: false,
                countsSourceAsNewOccupancy: true);

            Assert.Equal(4096, plain.SourceBytes);
            Assert.Equal(plain.StagingBytes, plain.FreeSpaceDemandBytes);
            Assert.Equal(
                TaskSpaceEstimate.SaturatingSum(withPartial.StagingBytes, withPartial.SourceBytes),
                withPartial.FreeSpaceDemandBytes);

            // 精估（解压前那一遍 list 之后）必须**继承**这个事实，⛔ 不许再传一次。
            TaskSpaceEstimate refined = SpaceEstimator.RefineWithListing(
                withPartial,
                new ArchiveListResult
                {
                    Success = true,
                    FileCount = 1,
                    TotalUncompressedSize = 100,
                    Entries = new[] { new ArchiveEntry { Path = "a.bin", Size = 100 } }
                });

            Assert.True(refined.CountsSourceAsNewOccupancy);
            Assert.Equal(
                TaskSpaceEstimate.SaturatingSum(refined.StagingBytes, refined.SourceBytes),
                refined.FreeSpaceDemandBytes);

            // 「同卷 / 跨卷」那一步是**新造一份**，这个事实也要跟着走（漏了就等于静默放宽判据）。
            Assert.True(refined.WithVolumeLayout(true).CountsSourceAsNewOccupancy);
        }

        // ================================================================ 四、管线接线

        /// <summary>
        /// **默认档一个字不改**（红线）：开关关着时，失败任务一个字节都不发布，
        /// 已解出的产物照旧按③页口径清掉。
        /// </summary>
        [Fact]
        public async Task 开关关着_失败时不发布任何东西()
        {
            Assert.Null(System.Windows.Application.Current);

            Harness harness = CreateHarness(partialPublish: false);
            ArchiveTask task = AddTask(harness, CreateSourceFile("partial.7z"));

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(6));
            harness.Engine.OnExtractAsync = request =>
            {
                WritePayload(request.OutputPath!, 5);
                return Task.FromResult(WrongPassword());
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(TaskOutcome.Failed, task.Outcome);
            Assert.False(
                Directory.Exists(Path.Combine(task.OutputPath!, PartialPublisher.DirectoryName)),
                "开关关着 ⇒ 不许出现「部分完成」目录");
            Assert.True(File.Exists(task.CurrentPath), "源包必须原地不动");
        }

        /// <summary>
        /// **开关打开 + 解到一半失败**：逐条核对过的那 5 个（清单 6 个）发布进
        /// <c>&lt;成品目录&gt;\部分完成\</c>，源包原地不动、终态仍然是失败。
        /// </summary>
        [Fact]
        public async Task 开关打开_失败时把核对过的部分发布出去()
        {
            Assert.Null(System.Windows.Application.Current);

            Harness harness = CreateHarness(partialPublish: true);
            ArchiveTask task = AddTask(harness, CreateSourceFile("partial.7z"));

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(6));
            harness.Engine.OnExtractAsync = request =>
            {
                WritePayload(request.OutputPath!, 5);
                return Task.FromResult(WrongPassword());
            };

            await harness.Coordinator.StartExtractAsync();

            string partialDirectory = Path.Combine(task.OutputPath!, PartialPublisher.DirectoryName);

            Assert.True(Directory.Exists(partialDirectory), "已经解出来、逐条核对过的那部分必须落地");
            Assert.Equal(5, Directory.GetFiles(partialDirectory).Length);

            // 缺口（第 6 个）不许假装有：发布的就是"真解出来的那 5 个"。
            Assert.False(File.Exists(Path.Combine(partialDirectory, "payload-00005.bin")));

            // ⛔ 红线：部分完成这一档源包一律不搬不删；终态也绝不许变成成功。
            Assert.True(File.Exists(task.CurrentPath), "源包必须原地不动");
            Assert.Equal(TaskOutcome.Failed, task.Outcome);
            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);

            List<string> logs = harness.Log.Logs.Select(item => item.DisplayText).ToList();

            Assert.Contains(logs, text => text.Contains("部分完成", StringComparison.Ordinal)
                                         && text.Contains("源包原地不动", StringComparison.Ordinal));
        }

        /// <summary>
        /// **与「空间不足」模式硬互斥**（用户 2026-10-02 顾虑 2）：那一档开着时本开关**不生效**，
        /// 而且必须写一行说明为什么 —— ⛔ 用户的设置本身一个字节都不改。
        /// </summary>
        [Fact]
        public async Task 空间不足模式开着_部分完成发布不生效且说明原因()
        {
            Assert.Null(System.Windows.Application.Current);

            Harness harness = CreateHarness(partialPublish: true);
            harness.Vm.SpaceTightMode = true;

            ArchiveTask task = AddTask(harness, CreateSourceFile("partial.7z"));

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(6));
            harness.Engine.OnExtractAsync = request =>
            {
                WritePayload(request.OutputPath!, 5);
                return Task.FromResult(WrongPassword());
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.False(
                Directory.Exists(Path.Combine(task.OutputPath!, PartialPublisher.DirectoryName)),
                "空间不足模式开着 ⇒ 部分完成发布必须不生效");

            Assert.True(harness.Vm.Settings.PartialPublishEnabled, "⛔ 不许偷偷改用户的设置");

            List<string> logs = harness.Log.Logs.Select(item => item.DisplayText).ToList();

            Assert.Contains(logs, text => text.Contains("本批按「空间不足」模式跑", StringComparison.Ordinal));
        }

        /// <summary>
        /// **取消的任务一个字节都不发布**：用户按了停，他要的是"停下来"，
        /// 不是"把半成品摆进目标目录"。
        /// </summary>
        [Fact]
        public async Task 取消的任务_一个字节都不发布()
        {
            Assert.Null(System.Windows.Application.Current);

            Harness harness = CreateHarness(partialPublish: true);
            ArchiveTask task = AddTask(harness, CreateSourceFile("partial.7z"));

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(6));
            harness.Engine.OnExtractAsync = request =>
            {
                WritePayload(request.OutputPath!, 5);
                throw new OperationCanceledException();
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.Cancelled, task.Status);
            Assert.False(
                Directory.Exists(Path.Combine(task.OutputPath!, PartialPublisher.DirectoryName)),
                "取消 ⇒ 一个字节都不发布（终态兜底成 Failed 也不算数）");
            Assert.True(File.Exists(task.CurrentPath), "源包必须原地不动");
        }

        /// <summary>
        /// **取消那一条路的真实形态**：单层路径上它只落中文状态、不落机器终态，收尾那一步还会把
        /// `Pending` 兜底改成 `Failed` ⇒ 拿终态当唯一判据的话，"取消"与"失败"分不开，
        /// 用户按下取消之后半成品照样会被摆进目标目录。
        ///
        /// <para>判据必须读**任务自己的取消令牌**。这里刻意造成那个竞态：取消请求在引擎那一次里发出，
        /// 而引擎仍然正常返回了结论（不是抛异常）—— 于是失败路径照常登记了候选、
        /// 终态也照常落成「失败」，只有取消令牌这一位能拦住发布。</para>
        /// </summary>
        [Fact]
        public async Task 用户在解压途中点了取消_即使终态落成失败也不发布()
        {
            Assert.Null(System.Windows.Application.Current);

            Harness harness = CreateHarness(partialPublish: true);
            ArchiveTask task = AddTask(harness, CreateSourceFile("partial.7z"));

            /*
             * 取消请求**必须在失败路径登记完候选之后**才发出，否则这个用例会因为"根本没登记候选"
             * 而假绿（那样无论有没有取消这一道闸门，结果都是不发布）。
             *
             * 落点选在「密码已经证实」那一行日志上：它写在收尾结论里，
             * 而登记候选发生在候选循环刚结束、引擎原话落日志之前 —— 这一行必然在它之后。
             * 用例末尾会断言这个钩子真的被触发过，⛔ 不许静默不生效。
             */
            bool cancelRequested = false;

            harness.Log.LogAdded += (_, item) =>
            {
                if (!cancelRequested &&
                    item.DisplayText.Contains("已经由这一趟解压本身证实", StringComparison.Ordinal))
                {
                    cancelRequested = true;
                    harness.Coordinator.CancelCurrentTask();
                }
            };

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(6));
            harness.Engine.OnExtractAsync = request =>
            {
                WritePayload(request.OutputPath!, 5);
                return Task.FromResult(WrongPassword());
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.True(cancelRequested, "这个用例必须真的在登记候选之后发出取消（否则它就是假绿）");

            Assert.False(
                Directory.Exists(Path.Combine(task.OutputPath!, PartialPublisher.DirectoryName)),
                "取消 ⇒ 一个字节都不发布（终态兜底成「失败」也不算数）");
            Assert.True(File.Exists(task.CurrentPath), "源包必须原地不动");
        }

        /// <summary>
        /// 发布成功之后的**半份清理**（接通管线）：成品目录里的 <c>其余物</c> 只留这条链的最外层源包，
        /// 别的过程物删掉 —— 盘上正好是"两份"（源包 + 已解出的内容物）。
        /// </summary>
        [Fact]
        public async Task 发布成功之后_其余物里的过程物被清掉()
        {
            Assert.Null(System.Windows.Application.Current);

            Harness harness = CreateHarness(
                partialPublish: true,

                /*
                 * 这一条要先摆好"成品目录里已经有其余物"的现场（其余物就在成品目录下面），
                 * 而那会让落点冲突档看到"目录已存在且非空"⇒ 默认档（自动重命名）会把成品目录
                 * 改成 `partial(1)`，其余物就不在它里面了、清理自然找不到目标。
                 * 所以这里按用户口径选「覆盖」：沿用该目录（这正是真机上"重跑同一个包"的那一档）。
                 */
                configure: settings => settings.ConflictAction = ConflictActions.Overwrite);

            string sourcePath = CreateSourceFile("partial.7z");
            ArchiveTask task = AddTask(harness, sourcePath);

            // 先按"这一单已经有其余物"摆现场：里面躺着一份过程物（内层包）。
            string restDirectory = Path.Combine(
                harness.Vm.Settings.CustomOutputDirectory,
                FileNameHelper.GetArchiveBaseName(sourcePath),
                "其余物");

            Directory.CreateDirectory(restDirectory);
            File.WriteAllText(Path.Combine(restDirectory, "inner-222.7z.001"), "内层分卷（过程物）");

            task.RestDirectoryPath = restDirectory;

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(6));
            harness.Engine.OnExtractAsync = request =>
            {
                WritePayload(request.OutputPath!, 5);
                return Task.FromResult(WrongPassword());
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.True(
                Directory.Exists(Path.Combine(task.OutputPath!, PartialPublisher.DirectoryName)),
                "前提：这一单真的发布了");

            Assert.False(
                File.Exists(Path.Combine(restDirectory, "inner-222.7z.001")),
                "过程物该被清掉（用户：源包 + 已解出的内容物留着，其他的都删掉）");

            Assert.True(File.Exists(task.CurrentPath), "最外层源包必须还在，而且原地不动");
        }

        // ================================================================ 五、批末诊断（用户要看得见"救回来多少"）

        /// <summary>
        /// 批末诊断要在那一组下面补一句"其中 N 个已经按「部分完成」发布出来了（共 K 个文件）"——
        /// 否则用户看到"1 个没做成"会以为那一个包一个字节都没救回来。
        ///
        /// <para>对照：没发布过的任务**一个字都不加**（⛔ 不写空话、不许无中生有）。</para>
        /// </summary>
        [Fact]
        public void 批末诊断_部分完成发布过的要说清救回来多少()
        {
            var published = new ArchiveTask(@"C:\t\a.7z", 1)
            {
                FileName = "a.7z",
                Status = StatusText.Corrupted,
                Outcome = TaskOutcome.Failed,
                EndTime = DateTime.Now,
                PartialPublishedCount = 557,
                PartialPublishDirectoryPath = @"C:\out\a\部分完成"
            };

            var plain = new ArchiveTask(@"C:\t\b.7z", 2)
            {
                FileName = "b.7z",
                Status = StatusText.Corrupted,
                Outcome = TaskOutcome.Failed,
                EndTime = DateTime.Now
            };

            BatchSummaryReport report = BatchSummaryDiagnosticsRules.Build(new[] { published, plain });
            string all = string.Join(" | ", report.Lines);

            Assert.Contains("已经按「部分完成」发布出来了", all, StringComparison.Ordinal);
            Assert.Contains("557", all, StringComparison.Ordinal);

            // 对照：只有没发布过的那一单时，那句话一个字都不许出现。
            BatchSummaryReport nonePublished = BatchSummaryDiagnosticsRules.Build(new[] { plain });
            string plainLines = string.Join(" | ", nonePublished.Lines);

            Assert.DoesNotContain("部分完成」发布出来了", plainLines, StringComparison.Ordinal);
        }

        // ================================================================ 装配

        private (string Staging, string Output) CreatePublishScenario(int payloadCount, int manifestCount)
        {
            string staging = Path.Combine(_root, "staging");
            string output = Path.Combine(_root, "out", "pkg");

            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(output);

            for (int i = 0; i < payloadCount; i++)
            {
                File.WriteAllText(Path.Combine(staging, $"payload-{i:D5}.bin"), "x");
            }

            _ = manifestCount;
            return (staging, output);
        }

        private static IReadOnlyList<(string Path, long Size)> Manifest(int count)
        {
            return Enumerable.Range(0, count)
                .Select(i => ($"payload-{i:D5}.bin", 1L))
                .ToList();
        }

        /// <summary>造一个"其余物里躺着源包与过程物"的现场（与 DeleteOptionsClarityTests 同一形状）。</summary>
        private (ArchiveTask Task, string RestDirectory) CreatePurgeScenario()
        {
            string outputPath = Path.Combine(_root, "out", "222");
            string restDirectory = Path.Combine(outputPath, "其余物");

            Directory.CreateDirectory(restDirectory);

            File.WriteAllText(Path.Combine(restDirectory, "inner-222.7z.001"), "内层分卷（过程物）");
            File.WriteAllText(Path.Combine(restDirectory, "222.7z.002"), "内层分卷（过程物）");

            var task = new ArchiveTask(Path.Combine(_root, "src", "222.7z"), 1)
            {
                FileName = "222.7z",
                OutputPath = outputPath,
                Status = StatusText.PartiallyCompleted,
                Outcome = TaskOutcome.Failed,
                RestDirectoryPath = restDirectory,
                VolumeGroupKey = "222"
            };

            return (task, restDirectory);
        }

        private sealed class Harness
        {
            public Harness(MainViewModel vm, FakeEngine engine, ExtractionCoordinator coordinator, LogService log)
            {
                Vm = vm;
                Engine = engine;
                Coordinator = coordinator;
                Log = log;
            }

            public MainViewModel Vm { get; }

            public FakeEngine Engine { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }
        }

        private Harness CreateHarness(bool partialPublish, Action<AppSettings>? configure = null)
        {
            string dataRoot = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N")[..6]);
            string outputRoot = Path.Combine(_root, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.PartialPublishEnabled = partialPublish;
            configure?.Invoke(settings);
            settingsService.Save(settings);

            var engine = new FakeEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

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

            var coordinator = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService())
            {
                KeepTaskDetailInLog = true
            };

            return new Harness(vm, engine, coordinator, logService);
        }

        private ArchiveTask AddTask(Harness harness, string sourcePath)
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

        private string CreateSourceFile(string fileName)
        {
            string directory = Path.Combine(_root, "src");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, fileName);
            File.WriteAllText(path, "not a real archive - the engine is faked in these tests");
            return path;
        }

        /// <summary>按清单形状写出前 <paramref name="count"/> 个产物（每个 1 字节，与 <see cref="ListResult"/> 对得上）。</summary>
        private static void WritePayload(string directory, int count)
        {
            Directory.CreateDirectory(directory);

            for (int i = 0; i < count; i++)
            {
                File.WriteAllText(Path.Combine(directory, $"payload-{i:D5}.bin"), "x");
            }
        }

        private static ArchiveOperationResult WrongPassword()
        {
            return new ArchiveOperationResult
            {
                Success = false,
                Status = StatusText.WrongPassword,
                Message = "密码错误",
                DetectedErrorType = "WrongPassword"
            };
        }

        private static ArchiveListResult ListResult(int fileCount)
        {
            return new ArchiveListResult
            {
                Success = true,
                FileCount = fileCount,
                TotalUncompressedSize = fileCount,
                Entries = Enumerable.Range(0, fileCount)
                    .Select(i => new ArchiveEntry { Path = $"payload-{i:D5}.bin", Size = 1 })
                    .ToList(),
                EngineId = "fake",
                EngineVersion = "1.0"
            };
        }

        /// <summary>可控的假引擎（与 ExtractionPipelineFixTests 里那个同一形状）。</summary>
        private sealed class FakeEngine : IArchiveEngine
        {
            public Func<ArchiveRequest, Task<ArchiveOperationResult>>? OnExtractAsync { get; set; }

            public Func<ArchiveRequest, Task<ArchiveListResult>>? OnListAsync { get; set; }

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
                return OnListAsync != null
                    ? OnListAsync(request)
                    : Task.FromResult(ListResult(0));
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = true,
                    Status = StatusText.Success,
                    Message = "操作成功",
                    DetectedErrorType = "None"
                });
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                return OnExtractAsync != null
                    ? OnExtractAsync(request)
                    : Task.FromResult(new ArchiveOperationResult
                    {
                        Success = true,
                        Status = StatusText.ExtractSuccess,
                        Message = "解压成功",
                        DetectedErrorType = "None"
                    });
            }
        }
    }
}
