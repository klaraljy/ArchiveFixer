using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Engines.WinRar;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 用户 2026-09-30 第 1 条：**批末那个汇总框要具体指出错在哪**。
    ///
    /// <para>他原话："我说的最后的汇总框，不单是要改颜色，而且你要特别说明哪里出错了，就像一般的编译器里面
    /// 最后会报出错在什么地方，比如空间不足、哪个压缩包密码不对、111.7z.001 的分卷找不到……
    /// 你和我这总会仔细看日志，但是用户不会，他们只想看看错误出在哪里。"</para>
    ///
    /// <para>这里钉住四件事：① 分组齐全且**逐组点名**（不是只给数字）；
    /// ② 每组的"还有 K 个"与总长度上限；③ **成功的那条不许出现在任何组里**、全成功时一个字都不写；
    /// ④ 判据只有一处（<see cref="BatchSummaryDiagnosticsRules"/>），颜色与文字出自同一次调用。</para>
    /// </summary>
    // 碰进程级静态（构造 MainViewModel 会写工作区根、并读 WorkspaceRootIndex 账本）：
    // 与同类用例串行跑，不与别的集合并行 —— 见 InnerLayerContinuationTests 顶部的 CollectionDefinition。
    [Collection("ArchiveFixerGlobalState")]
    public class BatchSummaryDiagnosticsTests : IDisposable
    {
        private readonly string _root;

        public BatchSummaryDiagnosticsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerDiagnostics", Guid.NewGuid().ToString("N"));
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
            finally
            {
                DialogService.ClearFallbackLog();
            }
        }

        // ================================================================ ① 分组 + 点名

        /// <summary>
        /// 混合批（1 空间不足 + 2 密码类 + 1 缺卷 + 1 损坏 + 1 成功）：
        /// 四组都要在、包名都要点到、缺的卷名要被点名、**成功那条一个字都不许出现**。
        /// </summary>
        [Fact]
        public void 混合批_分组齐全_包名点到_缺卷名被点名_成功的不进任何组()
        {
            BatchSummaryReport report = BatchSummaryDiagnosticsRules.Build(BuildMixedBatch());

            Assert.Equal(
                new[]
                {
                    BatchProblemKind.DiskSpace,
                    BatchProblemKind.Password,
                    BatchProblemKind.MissingVolume,
                    BatchProblemKind.Corrupted
                },
                report.Groups.Select(group => group.Kind).ToArray());

            // 逐组点名（用户要求的是"具体包名"，不是只给数字）。
            Assert.Contains("space.7z", report.Text, StringComparison.Ordinal);
            Assert.Contains("pw-a.rar", report.Text, StringComparison.Ordinal);
            Assert.Contains("pw-b.rar", report.Text, StringComparison.Ordinal);
            Assert.Contains("111.7z.001", report.Text, StringComparison.Ordinal);
            Assert.Contains("broken.zip", report.Text, StringComparison.Ordinal);

            // 缺卷要**点名缺哪几个**（含 .002 这种后续卷）。
            Assert.Contains("缺 111.7z.002、111.7z.003", report.Text, StringComparison.Ordinal);

            // 空间不足要带**空间门已经算好的**数字（差多少）。
            Assert.Contains("差 ", report.Text, StringComparison.Ordinal);

            // 成功的那条不许出现在任何失败组里（用户明确要求，逐字检查整段文字）。
            Assert.DoesNotContain("good.7z", report.Text, StringComparison.Ordinal);

            // 标题与"下一步"都在，而且下一步要指到具体动作上。
            Assert.StartsWith(StatusText.BatchDiagnosticsTitle, report.Text, StringComparison.Ordinal);
            Assert.Contains(StatusText.BatchDiagnosticsNextStepPrefix, report.Text, StringComparison.Ordinal);
            Assert.Contains("「密码」页", report.Text, StringComparison.Ordinal);

            // 密码那一组必须带"可能"（AGENTS.md §11：⛔ 不许断言就是密码问题）。
            Assert.Contains(StatusText.BatchDiagnosticsPasswordNote, report.Text, StringComparison.Ordinal);
        }

        /// <summary>
        /// 逐组截断："还有 K 个"的数字必须**等于**该组实际没列出来的个数
        /// （数字错了比不写还坏：用户会拿它去对任务列表）。
        /// </summary>
        [Fact]
        public void 超过上限时_每组只点几个名字_其余折成还有K个()
        {
            var tasks = new List<ArchiveTask>
            {
                Broken("c1.zip"),
                Broken("c2.zip"),
                Broken("c3.zip"),
                Broken("c4.zip"),
                Broken("c5.zip")
            };

            BatchSummaryReport report = BatchSummaryDiagnosticsRules.Build(tasks);

            BatchProblemGroup corrupted = Assert.Single(report.Groups);

            Assert.Equal(BatchProblemKind.Corrupted, corrupted.Kind);
            Assert.Equal(5, corrupted.TotalCount);
            Assert.Equal(BatchSummaryDiagnosticsRules.MaxNamesPerGroup, corrupted.Items.Count);
            Assert.Equal(2, corrupted.HiddenCount);

            Assert.Contains("c1.zip、c2.zip、c3.zip", report.Text, StringComparison.Ordinal);
            Assert.Contains("（还有 2 个）", report.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("c4.zip", report.Text, StringComparison.Ordinal);

            // 上限可调（上限 2 时同一批的"还有 K 个"跟着变）。
            BatchSummaryReport narrower = BatchSummaryDiagnosticsRules.Build(tasks, maxNamesPerGroup: 2);

            Assert.Equal(2, narrower.Groups[0].Items.Count);
            Assert.Equal(3, narrower.Groups[0].HiddenCount);
            Assert.Contains("（还有 3 个）", narrower.Text, StringComparison.Ordinal);
        }

        /// <summary>缺的卷名多到列不下时，同样按上限截断（⛔ 不许把一整组卷名刷满屏）。</summary>
        [Fact]
        public void 缺卷名太多时_也按上限截断()
        {
            var task = new ArchiveTask(Path.Combine(_root, "many.7z.001"))
            {
                IsVolumeGroup = true,
                Status = StatusText.VolumeMissing,
                Outcome = TaskOutcome.Failed
            };

            for (int index = 2; index <= 9; index++)
            {
                task.MissingVolumeNames.Add($"many.7z.{index:000}");
            }

            BatchSummaryReport report = BatchSummaryDiagnosticsRules.Build(new[] { task });

            Assert.Contains("缺 many.7z.002、many.7z.003、many.7z.004（还有 5 个）", report.Text, StringComparison.Ordinal);
        }

        /// <summary>
        /// 全成功（含空批）：**一个字都不写** —— 不许出现空标题，更不许留一个空的"出错在哪："。
        /// </summary>
        [Fact]
        public void 全成功时_不出清单_也不许出现空标题()
        {
            var tasks = new List<ArchiveTask>
            {
                Succeeded("a.7z"),
                Succeeded("b.7z")
            };

            BatchSummaryReport report = BatchSummaryDiagnosticsRules.Build(tasks);

            Assert.Equal(BatchSummarySeverity.Success, report.Severity);
            Assert.Empty(report.Groups);
            Assert.Empty(report.Lines);
            Assert.Equal(string.Empty, report.Text);
            Assert.False(report.HasProblems);

            // 空批同理（"什么都没发生"用蓝色最诚实，也不写清单）。
            BatchSummaryReport empty = BatchSummaryDiagnosticsRules.Build(Array.Empty<ArchiveTask>());

            Assert.Equal(BatchSummarySeverity.Success, empty.Severity);
            Assert.Equal(string.Empty, empty.Text);
            Assert.Equal(string.Empty, BatchSummaryDiagnosticsRules.Build(null).Text);
        }

        /// <summary>
        /// 总长度必须能一眼看完（用户："⛔ 不许把 500 行日志塞进弹窗"）：
        /// 极端批（每一档各 50 个任务）也要封顶 —— 组数由分类表封顶，名字由每组上限封顶。
        /// </summary>
        [Fact]
        public void 极端批_总长度仍然封顶()
        {
            var tasks = new List<ArchiveTask>();

            for (int index = 0; index < 50; index++)
            {
                tasks.Add(SpaceBlocked($"space-{index}.7z"));
                tasks.Add(Password($"pw-{index}.rar", StatusText.WrongPassword));
                tasks.Add(Password($"limit-{index}.rar", StatusText.PasswordAttemptLimitReached));
                tasks.Add(Password($"mhe-{index}.rar", StatusText.EncryptedHeaders));
                tasks.Add(VolumeMissing($"vol-{index}.7z.001"));
                tasks.Add(Broken($"broken-{index}.zip"));
                tasks.Add(Denied($"denied-{index}.zip"));
                tasks.Add(Conflict($"conflict-{index}.zip"));
                tasks.Add(Changed($"changed-{index}.zip"));
                tasks.Add(Unknown($"unknown-{index}.zip"));
                tasks.Add(Partial($"partial-{index}.zip"));
                tasks.Add(Skipped($"skipped-{index}.zip"));
                tasks.Add(Cancelled($"cancelled-{index}.zip"));
                tasks.Add(Pending($"pending-{index}.zip"));
            }

            BatchSummaryReport report = BatchSummaryDiagnosticsRules.Build(tasks);

            // 12 组 = 分类表的全部档位（密码类的三种终态合成一组，所以不是 14）。
            Assert.Equal(12, report.Groups.Count);
            Assert.True(report.Text.Length <= 2000, $"清单太长（{report.Text.Length} 字符）：\n" + report.Text);
            Assert.True(report.Lines.Count <= 16, $"清单行数太多（{report.Lines.Count} 行）：\n" + report.Text);

            // 每一组都只点 3 个名字、其余折成"还有 K 个"
            // （密码那一组是三种终态合起来的 150 个，所以这里只钉"截断"这件事）。
            foreach (BatchProblemGroup group in report.Groups)
            {
                Assert.Equal(3, group.Items.Count);
                Assert.Equal(group.TotalCount - 3, group.HiddenCount);
                Assert.Contains($"（还有 {group.HiddenCount} 个）", group.ToLine(), StringComparison.Ordinal);
            }

            BatchProblemGroup password = Assert.Single(
                report.Groups,
                group => group.Kind == BatchProblemKind.Password);

            Assert.Equal(150, password.TotalCount);
        }

        // ================================================================ ②b 跟班卷不算"没做成"

        /// <summary>
        /// **同一分卷组的后续卷（跟班卷）不进批末的"没做成"**（用户 2026-10-01 第三报）：
        /// 真机上一批 10 个任务里 4 个是各组后续卷（同组已由首卷那一单整组解完），
        /// 汇总却报成「成功 6 / 跳过 4」、框是橙的、诊断还写「已跳过：4 个」——
        /// 用户读成"还有 4 个没弄完"（原话：「这四个应该是要跳过的，我绝对没必要，你这样会让用户觉得还有任务没弄完」）。
        ///
        /// <para>两条一起钉：① 严重度是**蓝**（全成功）；② 诊断文案里**一个跟班卷都不许出现**。
        /// ⛔ 对照组：用户自己在冲突框里选的"跳过"照旧算橙并点名（那一档确实有东西没做成）。</para>
        /// </summary>
        [Fact]
        public void 跟班卷不算没做成_整批该是蓝的且不点名()
        {
            var owner = Succeeded("111.part1.rar");

            ArchiveTask follower = Skipped("111.part2.rar");
            follower.IsVolumeGroupFollower = true;

            List<ArchiveTask> tasks = new() { owner, follower };

            BatchSummaryReport report = BatchSummaryDiagnosticsRules.Build(tasks);

            Assert.Equal(BatchSummarySeverity.Success, report.Severity);
            Assert.DoesNotContain("111.part2.rar", report.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(StatusText.Skipped, report.Text, StringComparison.Ordinal);

            // ⛔ 对照：不是跟班卷的"跳过"照旧算橙并点名。
            BatchSummaryReport control = BatchSummaryDiagnosticsRules.Build(
                new[] { Succeeded("a.7z"), Skipped("junk.txt") });

            Assert.Equal(BatchSummarySeverity.Partial, control.Severity);
            Assert.Contains("junk.txt", control.Text, StringComparison.Ordinal);
        }

        /// <summary>
        /// **那一行汇总本身**也要按同一个事实位算（用户 2026-10-01 第三报 + 第五报的日志）：
        /// 跟班卷从"跳过"那一档剔出去、单列一句解释，而**各分项之和 + 未处理 = 本次任务数**这条恒等式
        /// 一个字都不许破（破了用户根本没法判断到底发生了什么 —— 这一行当初就是为它重写的）。
        ///
        /// <para>第五报真机日志里那一行是「一键处理完成：成功 6 / 失败 0 / **跳过 4**（本次 10 个任务）」，
        /// 而 4 个全是各组后续卷 —— 用户会再报一次「这四个应该是要跳过的，我绝对没必要」。</para>
        /// </summary>
        [Fact]
        public void 汇总那一行_跟班卷从跳过里剔出去_且恒等式不许破()
        {
            Harness harness = CreateHarness();

            var owner = Succeeded("111.part1.rar");

            ArchiveTask follower = Skipped("111.part2.rar");
            follower.IsVolumeGroupFollower = true;

            // 用户自己在冲突框里选的"跳过"：照旧算在"跳过"那一档里（⛔ 不许被这一改顺手吞掉）。
            ArchiveTask userSkip = Skipped("junk.txt");

            string line = harness.OneClick.BuildSummaryLine(new[] { owner, follower, userSkip });

            // 跟班卷不许混进"跳过"那一档。
            Assert.Contains("跳过 1", line, StringComparison.Ordinal);

            // 但它要说清是什么、以及"不是没做成"。
            Assert.Contains("另有 1 个是同一分卷组的后续卷", line, StringComparison.Ordinal);
            Assert.Contains("不是没做成", line, StringComparison.Ordinal);

            // 恒等式：成功 1 + 跳过 1 + 跟班 1 = 3 = 本次任务数（⛔ 不许凭空多一个"未处理"）。
            // （夹具里列表是空的，所以 scope 会写「本次 3 个 / 列表共 0 个」—— 这里只钉前半截。）
            Assert.Contains("本次 3 个", line, StringComparison.Ordinal);
            Assert.DoesNotContain("未处理", line, StringComparison.Ordinal);

            // ⛔ 对照：一个跟班卷都没有时，这一句一个字都不该出现。
            string withoutFollowers = harness.OneClick.BuildSummaryLine(new[] { owner, userSkip });

            Assert.DoesNotContain("同一分卷组的后续卷", withoutFollowers, StringComparison.Ordinal);
        }

        // ================================================================ ② 判据只有一处

        /// <summary>
        /// 颜色与文字出自**同一次**调用：严重度就是既有那一个出口（<see cref="BatchSummarySeverityRules"/>）
        /// 对同一份任务清单算出来的结论 —— 不存在"各算一遍"。
        /// </summary>
        [Fact]
        public void 严重度与文字出自同一次调用()
        {
            List<ArchiveTask> tasks = BuildMixedBatch();

            BatchSummaryReport report = BatchSummaryDiagnosticsRules.Build(tasks);

            Assert.Equal(BatchSummarySeverityRules.FromTasks(tasks), report.Severity);
            Assert.Equal(BatchSummarySeverity.Failed, report.Severity);

            // 只有"跳过"那一批是橙：清单照样点名（颜色管颜色、文字管文字，两者互不代替）。
            BatchSummaryReport orange = BatchSummaryDiagnosticsRules.Build(new[] { Succeeded("a.7z"), Skipped("junk.txt") });

            Assert.Equal(BatchSummarySeverity.Partial, orange.Severity);
            Assert.Contains("junk.txt", orange.Text, StringComparison.Ordinal);
        }

        /// <summary>
        /// 组名沿用既有口径：能复用状态常量的就复用（⛔ 不新造第二套说法），
        /// 密码与"其他失败"这两个**分组**名才允许是新写的。
        /// </summary>
        [Fact]
        public void 组名沿用既有的状态常量()
        {
            Assert.Equal(StatusText.DiskSpaceInsufficient, BatchSummaryDiagnosticsRules.DescribeKind(BatchProblemKind.DiskSpace));
            Assert.Equal(StatusText.VolumeMissing, BatchSummaryDiagnosticsRules.DescribeKind(BatchProblemKind.MissingVolume));
            Assert.Equal(StatusText.Corrupted, BatchSummaryDiagnosticsRules.DescribeKind(BatchProblemKind.Corrupted));
            Assert.Equal(StatusText.AccessDenied, BatchSummaryDiagnosticsRules.DescribeKind(BatchProblemKind.AccessDenied));
            Assert.Equal(StatusText.OutputConflict, BatchSummaryDiagnosticsRules.DescribeKind(BatchProblemKind.OutputConflict));
            Assert.Equal(StatusText.SourceChanged, BatchSummaryDiagnosticsRules.DescribeKind(BatchProblemKind.SourceChanged));
            Assert.Equal(StatusText.PartiallyCompleted, BatchSummaryDiagnosticsRules.DescribeKind(BatchProblemKind.PartiallyCompleted));
            Assert.Equal(StatusText.Skipped, BatchSummaryDiagnosticsRules.DescribeKind(BatchProblemKind.Skipped));
            Assert.Equal(StatusText.Cancelled, BatchSummaryDiagnosticsRules.DescribeKind(BatchProblemKind.Cancelled));
        }

        /// <summary>
        /// **既有失败口径里的任务一个都不会从清单里消失**（这正是"不新造第二套分类"的守门线）：
        /// 凡是 <c>TaskSummaryService</c> 那一份名单算作失败的终态，在这里都得落到某一组上
        /// （落不到具体原因的进"其他失败"）。
        /// </summary>
        [Fact]
        public void 既有失败名单里的状态_一个都不会消失()
        {
            var summary = new TaskSummaryService();

            string[] failureStatuses =
            {
                StatusText.ExtractFailed,
                StatusText.WrongPassword,
                StatusText.Corrupted,
                StatusText.AccessDenied,
                StatusText.OutputConflict,
                // ⚠ 2026-10-08：缺卷**留在**这份"要处理的清单"里（不变量 7 要报"缺哪几个"）；
                //    用户口径改变的是分桶与配色（缺卷 = 部分完成），不是清单成员资格。
                StatusText.VolumeMissing,
                StatusText.PathTooLong,
                StatusText.SevenZipMissing,
                StatusText.NoEngineAvailable,
                StatusText.PasswordAttemptLimitReached,
                StatusText.DiskSpaceInsufficient,
                StatusText.SourceChanged,
                StatusText.EncryptedHeaders,
                StatusText.PartiallyCompleted,
                StatusText.UnknownError,
                StatusText.RenameFailed,
                StatusText.TestFailed
            };

            foreach (string status in failureStatuses)
            {
                Assert.True(summary.IsFailedStatus(status), $"{status} 应该在既有失败名单里");

                var task = new ArchiveTask(Path.Combine(_root, "one.7z"))
                {
                    Status = status,
                    Outcome = TaskOutcome.Failed
                };

                Assert.NotNull(BatchSummaryDiagnosticsRules.Classify(task));
            }
        }

        /// <summary>
        /// 密码类三种终态合成一组（用户点名的口径：密码错误 / 达到密码尝试上限 / 文件名已加密）。
        /// </summary>
        [Fact]
        public void 密码类三种终态合成一组()
        {
            BatchSummaryReport report = BatchSummaryDiagnosticsRules.Build(new[]
            {
                Password("a.rar", StatusText.WrongPassword),
                Password("b.rar", StatusText.PasswordAttemptLimitReached),
                Password("c.rar", StatusText.EncryptedHeaders)
            });

            BatchProblemGroup password = Assert.Single(report.Groups);

            Assert.Equal(BatchProblemKind.Password, password.Kind);
            Assert.Equal(3, password.TotalCount);
            Assert.Contains("a.rar、b.rar、c.rar", report.Text, StringComparison.Ordinal);
        }

        /// <summary>
        /// 只有**失败**才进"其他失败"：终态是失败、状态说不出原因的（校验判否 / 未知错误 / 没有可用的解压引擎）
        /// 一律兜底进那一组，⛔ 不许静默丢掉。
        /// </summary>
        [Fact]
        public void 说不出原因的失败_兜底进其他失败()
        {
            var verifiedFailed = new ArchiveTask(Path.Combine(_root, "verify.7z"))
            {
                // 真机出现过的那一帧：终态写着成功、校验已经判否（不变量 6）。
                Status = StatusText.ExtractSuccess,
                Outcome = TaskOutcome.Succeeded,
                OutputVerification = OutputVerificationOutcome.Failed
            };

            var noEngine = new ArchiveTask(Path.Combine(_root, "engine.7z"))
            {
                Status = StatusText.NoEngineAvailable,
                Outcome = TaskOutcome.Failed
            };

            BatchSummaryReport report = BatchSummaryDiagnosticsRules.Build(new[] { verifiedFailed, noEngine });

            BatchProblemGroup other = Assert.Single(report.Groups);

            Assert.Equal(BatchProblemKind.Other, other.Kind);
            Assert.Equal(2, other.TotalCount);
            Assert.Contains("verify.7z", report.Text, StringComparison.Ordinal);
            Assert.Contains("engine.7z", report.Text, StringComparison.Ordinal);
        }

        // ================================================================ ③' 2026-10-04 真机的两档

        /// <summary>
        /// **定稿搬运失败那一档不许被指去"看引擎原话"**（用户 2026-10-04 真机）：
        /// 这一类失败**根本没有引擎原话**（引擎那一步早就成功、校验也通过了，是程序自己搬不动），
        /// 而原来那一组的「下一步」只有 <see cref="StatusText.BatchDiagnosticsActionOther"/>
        /// —— 用户翻遍日志也找不到那句话。
        ///
        /// <para><b>红检</b>：把 <c>DescribeAction</c> 里那一支撤掉（回到底部的原句）
        /// ⇒ 本用例当场红（`Assert.DoesNotContain() Failure: BatchDiagnosticsActionOther`）。</para>
        /// </summary>
        [Fact]
        public void 定稿搬运失败_下一步不许指去看引擎原话()
        {
            var commitFailed = new ArchiveTask(Path.Combine(_root, "apk.7z"))
            {
                Status = StatusText.ExtractFailed,
                Outcome = TaskOutcome.Failed,
                CommitMoveFailed = true,
                ErrorMessage = StatusText.FinalizeMoveFailedPrefix + "内容物 6 个文件 → E:\\x；6 项没能搬运；没能搬运的前 5 项：c01.txt（挪开旧文件失败）"
            };

            BatchSummaryReport report = BatchSummaryDiagnosticsRules.Build(new[] { commitFailed });

            BatchProblemGroup other = Assert.Single(report.Groups);
            Assert.Equal(BatchProblemKind.Other, other.Kind);

            // 组注脚点出"这一类失败没有引擎原话"。
            Assert.Contains(StatusText.BatchDiagnosticsCommitMoveFailedNoteFormat.Replace("{0}", "1"), report.Text, StringComparison.Ordinal);

            // 「下一步」换成定稿那一句，⛔ 不许再出现"看引擎原话"那句。
            Assert.Contains(StatusText.BatchDiagnosticsActionCommitMoveFailed, report.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(StatusText.BatchDiagnosticsActionOther, report.Text, StringComparison.Ordinal);
        }

        /// <summary>
        /// **引擎类失败照旧用原句**（反向对照）：组里不是定稿搬运失败时，
        /// 「下一步」必须还是原来那一句 —— ⛔ 别把对的东西一刀切改掉。
        /// </summary>
        [Fact]
        public void 引擎类失败_下一步照旧用原句()
        {
            var noEngine = new ArchiveTask(Path.Combine(_root, "engine.7z"))
            {
                Status = StatusText.NoEngineAvailable,
                Outcome = TaskOutcome.Failed
            };

            BatchSummaryReport report = BatchSummaryDiagnosticsRules.Build(new[] { noEngine });

            Assert.Contains(StatusText.BatchDiagnosticsActionOther, report.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(StatusText.BatchDiagnosticsActionCommitMoveFailed, report.Text, StringComparison.Ordinal);
        }

        /// <summary>
        /// **其余物没处理要进批末诊断**（用户 2026-10-04 真机）：那一批的日志写着
        /// 「Sociology.7z：链尾的其余物不处理（链上的「老王.apk」没有成功…）」，可这个框里一个字都没有。
        ///
        /// <para><b>红检</b>：撤掉 <c>BuildRestKeptLines</c> 那两处调用 ⇒ 本用例当场红。</para>
        /// </summary>
        [Fact]
        public void 其余物没处理_批末诊断逐条点名()
        {
            var root = new ArchiveTask(Path.Combine(_root, "sociology.7z"))
            {
                // ⚠ 关键：这一单**是成功的**（内容物出来了），只有链上另一层没成功 ⇒
                // 它按口径不进任何问题组，所以这一句必须独立成行才会出现。
                Status = StatusText.ExtractSuccess,
                Outcome = TaskOutcome.Succeeded,
                RestDirectoryPath = @"E:\x\sociology\其余物",
                RestKeptReason = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.RestKeptNoteFormat,
                    @"E:\x\sociology\其余物",
                    "链上的「老王.apk」没有成功（机器终态：Failed）")
            };

            var failed = new ArchiveTask(Path.Combine(_root, "老王.apk"))
            {
                Status = StatusText.ExtractFailed,
                Outcome = TaskOutcome.Failed
            };

            BatchSummaryReport report = BatchSummaryDiagnosticsRules.Build(new[] { root, failed });

            Assert.Contains(
                report.Lines,
                line => line.Contains("其余物没有处理", StringComparison.Ordinal)
                        && line.Contains("sociology.7z", StringComparison.Ordinal));

            // 成功的那一单照旧不进任何问题组（⛔ 这一条不许被这次改动破掉）。
            Assert.DoesNotContain(
                report.Groups,
                group => group.Kind != BatchProblemKind.Other);
        }

        // ================================================================ ③ 隐私与排版

        /// <summary>
        /// 名字里含用户路径时**只写文件名**（隐私红线 §8）：目录一个字都不许进清单。
        /// </summary>
        [Fact]
        public void 清单里只写文件名_不带目录()
        {
            string directory = Path.Combine(_root, "private-folder");
            Directory.CreateDirectory(directory);

            var task = new ArchiveTask(Path.Combine(directory, "secret.rar"))
            {
                Status = StatusText.WrongPassword,
                Outcome = TaskOutcome.Failed
            };

            BatchSummaryReport report = BatchSummaryDiagnosticsRules.Build(new[] { task });

            Assert.Contains("secret.rar", report.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("private-folder", report.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(_root, report.Text, StringComparison.Ordinal);
        }

        /// <summary>超长名字保留头尾（尾巴上常常是 <c>.7z.001</c> 这种能定位到卷的信息）。</summary>
        [Fact]
        public void 超长名字保留头尾()
        {
            string name = new string('x', 90) + ".7z.001";

            var task = new ArchiveTask(Path.Combine(_root, name))
            {
                Status = StatusText.Corrupted,
                Outcome = TaskOutcome.Failed
            };

            BatchSummaryReport report = BatchSummaryDiagnosticsRules.Build(new[] { task });

            Assert.Contains("…", report.Text, StringComparison.Ordinal);
            Assert.Contains(".7z.001", report.Text, StringComparison.Ordinal);
        }

        // ================================================================ ④ 接线（真跑一次一键处理）

        /// <summary>
        /// 一键处理跑完之后，那个汇总框的正文里必须**看得见出错在哪**：
        /// 标题、失败的那两个包名都在，而**成功的那一条一个字都不出现**。
        /// </summary>
        [Fact]
        public async Task 一键处理汇总框_正文点名说清错在哪()
        {
            Harness harness = CreateHarness();

            string good = harness.CreateSource("good.7z");
            string broken = harness.CreateSource("broken.zip");
            string wrongPassword = harness.CreateSource("pw.rar");

            harness.Engine.Failures[Path.GetFileName(broken)] = new ArchiveOperationResult
            {
                Success = false,
                Status = StatusText.Corrupted,
                Message = "文件损坏",
                DetectedErrorType = "Corrupted"
            };

            harness.Engine.Failures[Path.GetFileName(wrongPassword)] = new ArchiveOperationResult
            {
                Success = false,
                Status = StatusText.WrongPassword,
                Message = "密码错误",
                DetectedErrorType = "WrongPassword"
            };

            await harness.AddTasksAsync(good, broken, wrongPassword);

            await harness.OneClick.RunAsync();

            (string Message, BatchSummarySeverity Severity) summary = Assert.Single(harness.Dialog.BatchSummaries);

            Assert.Equal(BatchSummarySeverity.Failed, summary.Severity);
            Assert.StartsWith("一键处理", summary.Message, StringComparison.Ordinal);

            Assert.Contains(StatusText.BatchDiagnosticsTitle, summary.Message, StringComparison.Ordinal);
            Assert.Contains("broken.zip", summary.Message, StringComparison.Ordinal);
            Assert.Contains("pw.rar", summary.Message, StringComparison.Ordinal);
            Assert.Contains(StatusText.BatchDiagnosticsPasswordNote, summary.Message, StringComparison.Ordinal);

            // 成功的那条不许出现在清单里（它只在任务列表里）。
            Assert.DoesNotContain("good.7z", summary.Message, StringComparison.Ordinal);

            // 同一份文字也逐组进了日志（"框里写了什么"与"日志里写了什么"必须一样）。
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains(StatusText.BatchDiagnosticsLogPrefix + "· " + StatusText.Corrupted, StringComparison.Ordinal));
        }

        /// <summary>反向：全成功那一批**一个字都不写**（连标题都不许出现）。</summary>
        [Fact]
        public async Task 一键处理汇总框_全成功时不出清单()
        {
            Harness harness = CreateHarness();

            await harness.AddTasksAsync(harness.CreateSource("good.7z"));

            await harness.OneClick.RunAsync();

            (string Message, BatchSummarySeverity Severity) summary = Assert.Single(harness.Dialog.BatchSummaries);

            Assert.Equal(BatchSummarySeverity.Success, summary.Severity);
            Assert.DoesNotContain(StatusText.BatchDiagnosticsTitle, summary.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                harness.LogTexts,
                text => text.Contains(StatusText.BatchDiagnosticsLogPrefix, StringComparison.Ordinal));
        }

        // ================================================================ ⑤ 三处口径一致

        /// <summary>
        /// **用户 2026-09-27 真机 `giu.7z.001`：同一个任务三处说三种话。**
        ///
        /// <para>现场：①页说「部分完成」、一键汇总说「成功 0 / 失败 0 / 未处理 1」、
        /// 批末诊断说「下一步：其他」。根子是两处：</para>
        /// <list type="number">
        /// <item><description>递归失败那条路只写了状态，`Outcome` 留在 `Pending` —— 汇总把它算成"没轮到"；</description></item>
        /// <item><description>"一个文件都没解出来"被落成了「部分完成」，用户会去暂存目录找不存在的产物。</description></item>
        /// </list>
        ///
        /// <para><b>红检</b>（三处任改一处回旧行为都会红）：① `ApplyRecursionResult` 的 default 支
        /// 改回"一律部分完成"→ 状态 + 汇总 + 诊断三处全红；② 只撤 `Outcome` 那两行 →
        /// 汇总变「未处理 1」当场红；③ `BatchSummaryDiagnosticsRules` 把「部分完成」无条件归组
        /// （不看终态）→ 诊断那两条红。</para>
        /// </summary>
        [Fact]
        public async Task 什么都没产出_三处口径一致且点名原因()
        {
            Harness harness = CreateHarness(settings => settings.RecursionMode = "SingleChain");

            string source = harness.CreateSource("giu.7z.001");

            /*
             * 假引擎：列目录成功、解压永远报"密码错误"，**一个字节的产物都不写** ——
             * 这正是真机那一单的形状（走的是递归路径，不是单层候选循环）。
             */
            harness.Engine.Failures[Path.GetFileName(source)] = new ArchiveOperationResult
            {
                Success = false,
                Status = StatusText.WrongPassword,
                Message = "密码错误或缺少正确密码（7-Zip：Cannot open encrypted archive. Wrong password?）",
                DetectedErrorType = "WrongPassword",
                EngineId = "fake",
                EngineVersion = "1.0",
                StandardOutput = "ERROR: giu.7z.001\nCannot open encrypted archive. Wrong password?\n"
            };

            await harness.AddTasksAsync(source);

            await harness.OneClick.RunAsync();

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);

            /*
             * 批末汇总那一行（一键汇总行）与对话框正文（批末诊断）都从日志 / 记录里取。
             *
             * ⚠ 汇总行在日志里出现**两次**：协调器写的原文，以及一键处理收尾把它抄一份
             * 加上"一键处理汇总："前缀（那是给排障对照用的）。取带前缀那一份 ——
             * 它的正文与协调器那一行**逐字相同**（同一份 <c>OneClickOutcome.Summary</c>）。
             */
            const string summaryLogPrefix = "一键处理汇总：";

            string summaryLine = Assert.Single(
                    harness.LogTexts,
                    text => text.Contains(summaryLogPrefix, StringComparison.Ordinal))
                .Split(summaryLogPrefix, 2)[1];

            (string Message, BatchSummarySeverity Severity) dialog = Assert.Single(harness.Dialog.BatchSummaries);
            string diagnostics = dialog.Message;

            // ---- ①页：状态必须是**失败**那一档，不许是"部分完成"（一个文件都没解出来）
            Assert.Equal(StatusText.WrongPassword, task.Status);
            Assert.Equal(TaskOutcome.Failed, task.Outcome);
            Assert.False(
                string.Equals(task.Status, StatusText.PartiallyCompleted, StringComparison.Ordinal),
                "一个文件都没解出来时不许显示成「部分完成」");

            // ---- 批汇总行：必须算进"失败 1"，⛔ 不许出现"未处理"
            Assert.Contains("失败 1", summaryLine, StringComparison.Ordinal);
            Assert.DoesNotContain("未处理", summaryLine, StringComparison.Ordinal);
            Assert.DoesNotContain("已停止", summaryLine, StringComparison.Ordinal);

            // ---- 批末诊断：必须**点名**"密码可能不对"，⛔ 不许落到"归不到具体原因"
            Assert.Equal(BatchSummarySeverity.Failed, dialog.Severity);
            Assert.Contains(StatusText.BatchDiagnosticsPasswordTitle, diagnostics, StringComparison.Ordinal);
            Assert.Contains(StatusText.BatchDiagnosticsPasswordNote, diagnostics, StringComparison.Ordinal);
            Assert.Contains("giu.7z.001", diagnostics, StringComparison.Ordinal);
            Assert.Contains(
                StatusText.BatchDiagnosticsNextStepPrefix + StatusText.BatchDiagnosticsActionPassword,
                diagnostics,
                StringComparison.Ordinal);
            Assert.DoesNotContain(StatusText.BatchDiagnosticsActionOther, diagnostics, StringComparison.Ordinal);

            // ---- 三处说的是**同一件事**：状态名与诊断组名都指向"密码"这一档
            Assert.Contains(StatusText.WrongPassword, diagnostics, StringComparison.Ordinal);
        }

        /// <summary>
        /// **递归内层包撞"磁盘空间不足"⇒ 必须归到空间那一组、点名空间，⛔ 不许落到"其他"**
        /// （用户 2026-09-27 真机：递归路径撞空间不足，批末诊断只说「下一步：其他」——
        /// 归档本身没问题，用户要做的是清空间，被指去"看错误信息列"等于没指路）。
        ///
        /// <para>判据只读引擎的**结构化错误码**（<c>EngineErrorTypes.NoDiskSpace</c> ⇒
        /// <see cref="TaskOutcomeClassifier"/> ⇒ 状态），⛔ 不比中文文案。</para>
        ///
        /// <para><b>红检</b>：把 <c>TaskOutcomeClassifier.TryResolveRecursionStop</c> 里
        /// <c>RecursionStopReason.DiskSpaceInsufficient</c> 那一支撤掉（落回
        /// <c>ExtractFailed</c>）⇒ 本用例当场红（①页状态、诊断分组、下一步三处一起变）。</para>
        /// </summary>
        [Fact]
        public async Task 递归撞磁盘空间不足_三处口径一致且点名空间()
        {
            Harness harness = CreateHarness(settings => settings.RecursionMode = "SingleChain");

            string source = harness.CreateSource("full.7z");

            /*
             * 假引擎：列目录成功、解压报"写不下盘"（结构化错误码 NoDiskSpace），一个字节产物都不写 ——
             * 这正是递归路径上撞空间不足的形状（单层路径的空间门在动手前就拦下了，
             * 这条路是**解压途中**才写满的）。
             */
            harness.Engine.Failures[Path.GetFileName(source)] = new ArchiveOperationResult
            {
                Success = false,
                Status = StatusText.DiskSpaceInsufficient,
                Message = "磁盘空间不足，7-Zip 写不下去",
                DetectedErrorType = EngineErrorTypes.NoDiskSpace,
                EngineId = "fake",
                EngineVersion = "1.0",
                StandardOutput = "ERROR: Can not create file : out\\payload.bin\n" +
                                 "There is not enough space on the disk.\n"
            };

            await harness.AddTasksAsync(source);

            await harness.OneClick.RunAsync();

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);

            string summaryLine = Assert.Single(
                    harness.LogTexts,
                    text => text.Contains("一键处理汇总：", StringComparison.Ordinal))
                .Split("一键处理汇总：", 2)[1];

            (string Message, BatchSummarySeverity Severity) dialog = Assert.Single(harness.Dialog.BatchSummaries);

            // ---- ①页：状态必须是「磁盘空间不足」（不是「解压失败」，更不是「部分完成」）
            Assert.Equal(StatusText.DiskSpaceInsufficient, task.Status);
            Assert.Equal(TaskOutcome.Failed, task.Outcome);

            // ---- 批汇总行：算进"失败 1"，⛔ 不许出现"未处理"
            Assert.Contains("失败 1", summaryLine, StringComparison.Ordinal);
            Assert.DoesNotContain("未处理", summaryLine, StringComparison.Ordinal);

            // ---- 批末诊断：**点名空间那一组**，⛔ 不许归"其他失败"、不许出现"下一步：其他"
            Assert.Equal(BatchSummarySeverity.Failed, dialog.Severity);
            Assert.Contains(StatusText.DiskSpaceInsufficient, dialog.Message, StringComparison.Ordinal);
            Assert.Contains("full.7z", dialog.Message, StringComparison.Ordinal);
            Assert.Contains(
                StatusText.BatchDiagnosticsNextStepPrefix + StatusText.BatchDiagnosticsActionDiskSpace,
                dialog.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(StatusText.BatchDiagnosticsOtherTitle, dialog.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(StatusText.BatchDiagnosticsActionOther, dialog.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// 引擎两侧都要认得"写不下盘"这句**系统错误文本**，而且必须**排在"权限不足"之前** ——
        /// 7-Zip / UnRAR 写不下去时打的都是 <c>Can not create file</c>（那一句同时是权限不足的关键字），
        /// 只有紧跟的系统错误文本能把它俩分开。
        ///
        /// <para><b>红检</b>：把两个解析器里那段"空间不足"关键字撤掉 ⇒ 本用例当场红
        /// （分类落回 <c>AccessDenied</c>，用户被指去改权限而盘还是满的）。</para>
        /// </summary>
        [Fact]
        public void 引擎两侧都认得出写不下盘_且不会被误判成权限不足()
        {
            const string diskFull =
                "ERROR: Can not create file : C:\\out\\payload.bin\n" +
                "There is not enough space on the disk.\n";

            Assert.Equal(
                EngineErrorTypes.NoDiskSpace,
                SevenZipOutputParser.DetectSevenZipErrorType(2, diskFull, string.Empty, "full.7z", EngineOperation.Extract));

            // 权限不足那句仍然归权限不足（反向对照：⛔ 别把两档混成一个）
            Assert.Equal(
                "AccessDenied",
                SevenZipOutputParser.DetectSevenZipErrorType(
                    2,
                    "ERROR: Can not create file : C:\\out\\payload.bin\nAccess is denied.\n",
                    string.Empty,
                    "full.7z",
                    EngineOperation.Extract));

            Assert.Equal(
                EngineErrorTypes.NoDiskSpace,
                UnRarOutputParser.DetectErrorType(9, "Cannot create out\\payload.bin\nThere is not enough space on the disk.\n", string.Empty, EngineOperation.Extract));

            Assert.Equal(
                EngineErrorTypes.AccessDenied,
                UnRarOutputParser.DetectErrorType(9, "Cannot create out\\payload.bin\nAccess is denied.\n", string.Empty, EngineOperation.Extract));

            // 结构化错误码 → 任务状态：两侧都落「磁盘空间不足」（⛔ 不是「解压失败」）
            Assert.Equal(StatusText.DiskSpaceInsufficient, SevenZipOutputParser.ErrorTypeToTaskStatus(EngineErrorTypes.NoDiskSpace));
            Assert.Equal(StatusText.DiskSpaceInsufficient, UnRarOutputParser.ErrorTypeToTaskStatus(EngineErrorTypes.NoDiskSpace));
        }

        /// <summary>
        /// **递归 Completed 但源包没能搬进其余物 ⇒ 终态不许还是「成功」**（不变量 6：
        /// 部分成功不得显示为成功）。
        ///
        /// <para>现场：递归解完了，<c>PostProcessSuccessAsync</c> 已经按"源包没搬成"落了
        /// 「部分完成」+ 机器终态 <c>PartiallyCompleted</c>；旧写法紧接着在
        /// <c>ApplyRecursionResult</c> 里无条件写 <c>Succeeded</c>，于是**状态说「部分完成」、
        /// 终态说成功** —— 而删源 / 搬源 / 续解那几道门读的都是终态，一件没做完的事会被当成做完了。</para>
        ///
        /// <para>造法：让源包**搬不动**（用独占句柄占住它，Windows 上是确定性的失败），
        /// 其余全走正常路径（单链递归 + 源包操作 = 放入其余物）。</para>
        ///
        /// <para><b>红检</b>：把 <c>ApplyRecursionResult</c> 里那个
        /// <c>Outcome == Pending</c> 判据撤掉（改回无条件 <c>Succeeded</c>）⇒ 本用例当场红。</para>
        /// </summary>
        [Fact]
        public async Task 递归完成但源包没搬成_终态不许是成功()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.RecursionMode = "SingleChain";
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            });

            string source = harness.CreateSource("pack.7z");

            // 独占句柄：其余物那边一切正常，唯独这一组的源包搬不动（用户把包开着 / 杀软占用）。
            using (var hold = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await harness.AddTasksAsync(source);
                await harness.OneClick.RunAsync();
            }

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);

            /*
             * 内容物是好的、只是"源包没搬进其余物"：状态是「部分完成」（**不是**「解压失败」）。
             *
             * ⚠ 实测定序（探针，2026-09-30）：`ApplyRecursionResult` 跑在 `PostProcessSuccessAsync`
             * **之前**，所以这个结论是后者写下的 —— 见 `ApplyRecursionResult` 里那道防回归闸门的说明。
             */
            Assert.Equal(StatusText.PartiallyCompleted, task.Status);

            // ⛔ 机器终态必须与状态同一档：**不许是 Succeeded**（不变量 6）。
            // 读这一位的四道门（删源 / 搬源 / 续解 / 危险模式删其余物）靠它拦下"没做完"的事。
            Assert.Equal(TaskOutcome.PartiallyCompleted, task.Outcome);

            // 原因写得出来（用户要能看懂为什么不是成功）
            Assert.Contains("源包", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("其余物", task.ErrorMessage, StringComparison.Ordinal);

            /*
             * ---- 三处口径一致（①页 / 批汇总行 / 批末诊断）----
             *
             * ①页那一格由 `TaskSummaryService.ClassifyOutcome(task)` 决定（用户 2026-09-30
             * 那条"批末那个汇总框要指出错在哪"的验收口径）：它必须落在"其他失败"那一桶
             * （与①页的"失败总数"同侧），⛔ 不许被算进成功。
             */
            Assert.Equal(SummaryBucket.OtherFailed, TaskSummaryService.ClassifyOutcome(task));

            // 批汇总行：算"部分完成 1"，⛔ 既不是"成功"也不是"未处理"
            string summaryLine = Assert.Single(
                    harness.LogTexts,
                    text => text.Contains("一键处理汇总：", StringComparison.Ordinal))
                .Split("一键处理汇总：", 2)[1];

            Assert.Contains("成功 0", summaryLine, StringComparison.Ordinal);
            Assert.Contains("部分完成 1", summaryLine, StringComparison.Ordinal);
            Assert.DoesNotContain("未处理", summaryLine, StringComparison.Ordinal);

            // 批末诊断：点名"部分完成"这一组 + 说清差在哪一步 + 给一句能行动的话
            (string Message, BatchSummarySeverity Severity) dialog = Assert.Single(harness.Dialog.BatchSummaries);

            Assert.Equal(BatchSummarySeverity.Partial, dialog.Severity);
            Assert.Contains(StatusText.PartiallyCompleted, dialog.Message, StringComparison.Ordinal);
            Assert.Contains("pack.7z", dialog.Message, StringComparison.Ordinal);
            Assert.Contains("源包", dialog.Message, StringComparison.Ordinal);
            Assert.Contains(
                StatusText.BatchDiagnosticsNextStepPrefix + StatusText.BatchDiagnosticsActionPartiallyCompleted,
                dialog.Message,
                StringComparison.Ordinal);

            // ⛔ 不许再落到那句"归不到具体原因"的兜底（用户点名的就是这句）
            Assert.DoesNotContain(StatusText.BatchDiagnosticsActionOther, dialog.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// 反向对照：源包搬得动的时候，递归成功仍然是 <c>Succeeded</c>（⛔ 别把这一档一并改坏）。
        /// </summary>
        [Fact]
        public async Task 递归完成且源包搬成了_终态仍是成功()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.RecursionMode = "SingleChain";
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            });

            string source = harness.CreateSource("pack-ok.7z");

            await harness.AddTasksAsync(source);
            await harness.OneClick.RunAsync();

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
        }

        /// <summary>
        /// 反向：**真解出来了一半**的失败仍然是「部分完成」（不变量 6 的反面同样成立：
        /// 不许把"做了一半"说成全盘失败）。
        /// </summary>
        [Fact]
        public void 有产物时_部分完成仍然是部分完成()
        {
            var task = new ArchiveTask(Path.Combine(_root, "half.7z"))
            {
                Status = StatusText.PartiallyCompleted,
                Outcome = TaskOutcome.PartiallyCompleted
            };

            Assert.Equal(BatchProblemKind.PartiallyCompleted, BatchSummaryDiagnosticsRules.Classify(task));
        }

        /// <summary>
        /// 状态说「部分完成」而机器终态是**失败**时，这一单**不许被当成"做了一半"**收进
        /// 「部分完成」那一组 —— 终态才是事实（用户 2026-09-27 真机那一帧的守门）。
        ///
        /// <para>⚠ 这一帧在本版里**已经造不出来**了（<c>ApplyRecursionResult</c> 一次落齐状态与终态），
        /// 但清单的分类器仍然要挡住它：老数据 / 未来某条岔路再写出这一帧时，
        /// 它要么按具体状态归组，要么落"归不到具体原因"那一组，⛔ 绝不能顶着一个错误的"部分完成"。
        /// 真机那条"密码错但什么都没产出"的路走的是 <c>Status = 密码错误</c>，
        /// 由 <see cref="什么都没产出_三处口径一致且点名原因"/> 钉住。</para>
        /// </summary>
        [Fact]
        public void 状态说部分完成但终态是失败_不会被当成做了一半()
        {
            var task = new ArchiveTask(Path.Combine(_root, "stale.7z"))
            {
                // 旧口径留下的帧：状态「部分完成」、终态已经是失败。
                Status = StatusText.PartiallyCompleted,
                Outcome = TaskOutcome.Failed,
                ErrorMessage = "密码错误或缺少正确密码"
            };

            Assert.NotEqual(BatchProblemKind.PartiallyCompleted, BatchSummaryDiagnosticsRules.Classify(task));

            // 真解出来一半的那一帧仍然归「部分完成」（反向对照，见下一条）。
            Assert.Equal(BatchProblemKind.Other, BatchSummaryDiagnosticsRules.Classify(task));
        }

        // ================================================================ 造数据

        /// <summary>用户点名的那一批：1 空间不足 + 2 密码类 + 1 缺卷（含缺卷名）+ 1 损坏 + 1 成功。</summary>
        private List<ArchiveTask> BuildMixedBatch() => new()
        {
            SpaceBlocked("space.7z"),
            Password("pw-a.rar", StatusText.WrongPassword),
            Password("pw-b.rar", StatusText.PasswordAttemptLimitReached),
            VolumeMissing("111.7z.001"),
            Broken("broken.zip"),
            Succeeded("good.7z")
        };

        private ArchiveTask NewTask(string name, string status, TaskOutcome outcome) =>
            new(Path.Combine(_root, name))
            {
                Status = status,
                Outcome = outcome
            };

        private ArchiveTask Succeeded(string name) => NewTask(name, StatusText.ExtractSuccess, TaskOutcome.Succeeded);

        /// <summary>被空间门拦下：三个数就是空间门当时算好的那一份（这里照 SpaceGate 的形状填）。</summary>
        private ArchiveTask SpaceBlocked(string name)
        {
            ArchiveTask task = NewTask(name, StatusText.DiskSpaceInsufficient, TaskOutcome.Failed);

            task.SpaceBlocked = new SpaceBlockedFacts
            {
                RequiredBytes = 1288490188,
                AvailableBytes = 536870912,
                ShortfallBytes = 751619276
            };

            return task;
        }

        private ArchiveTask Password(string name, string status) => NewTask(name, status, TaskOutcome.Failed);

        /// <summary>缺卷：名字来自识别阶段（<see cref="ArchiveTask.MissingVolumeNames"/>）。</summary>
        private ArchiveTask VolumeMissing(string name)
        {
            ArchiveTask task = NewTask(name, StatusText.VolumeMissing, TaskOutcome.Failed);

            task.IsVolumeGroup = true;
            task.MissingVolumeNames.Add("111.7z.002");
            task.MissingVolumeNames.Add("111.7z.003");

            return task;
        }

        private ArchiveTask Broken(string name) => NewTask(name, StatusText.Corrupted, TaskOutcome.Failed);

        private ArchiveTask Denied(string name) => NewTask(name, StatusText.AccessDenied, TaskOutcome.Failed);

        private ArchiveTask Conflict(string name) => NewTask(name, StatusText.OutputConflict, TaskOutcome.Failed);

        private ArchiveTask Changed(string name) => NewTask(name, StatusText.SourceChanged, TaskOutcome.Failed);

        private ArchiveTask Unknown(string name) => NewTask(name, StatusText.UnknownError, TaskOutcome.Failed);

        private ArchiveTask Partial(string name) => NewTask(name, StatusText.PartiallyCompleted, TaskOutcome.PartiallyCompleted);

        private ArchiveTask Skipped(string name) => NewTask(name, StatusText.Skipped, TaskOutcome.Skipped);

        private ArchiveTask Cancelled(string name) => NewTask(name, StatusText.Cancelled, TaskOutcome.Cancelled);

        private ArchiveTask Pending(string name) => NewTask(name, StatusText.WaitingExtract, TaskOutcome.Pending);

        // ================================================================ 宿主

        /// <summary>
        /// 假引擎 + 假盘（与 <c>SpaceRiskAndSummaryTests</c> 同一形态）：
        /// 这一组测的是**清单说了什么**，不是 7z 的能力。
        /// </summary>
        private Harness CreateHarness(Action<AppSettings>? configure = null)
        {
            string dataRoot = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N"));
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
            settings.TryEmptyPasswordFirst = false;
            settings.RemindBeforeExtract = false;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            settings.MaxParallelExtractCount = 1;

            // 逐用例的覆盖（例如"这一单要走递归路径"）—— 放最后，⛔ 别让默认值把它盖回去。
            configure?.Invoke(settings);

            settingsService.Save(settings);

            var engine = new FakeEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);
            var dialog = new RecordingDialogService();

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
                dialog);

            var coordinator = new ExtractionCoordinator(vm, engine, passwordService, pathService, dialog)
            {
                // 假盘：这一组要的是"有失败"，空间足够宽，一个都别被空间门拦下。
                SpaceProbeOverride = _ => 100L * 1024 * 1024 * 1024,
                SpaceReserveOverride = 0
            };

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), dialog);
            var rename = new RenameCoordinator(vm, scan, new RenameService(), dialog);
            var oneClick = new OneClickCoordinator(vm, scan, rename, coordinator, dialog)
            {
                OptionsPromptOverride = _ => OneClickOptionsPrompt.NotShown()
            };

            return new Harness(vm, engine, coordinator, oneClick, dialog, logService, _root);
        }

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                FakeEngine engine,
                ExtractionCoordinator coordinator,
                OneClickCoordinator oneClick,
                RecordingDialogService dialog,
                LogService log,
                string root)
            {
                Vm = vm;
                Engine = engine;
                Coordinator = coordinator;
                OneClick = oneClick;
                Dialog = dialog;
                Log = log;
                Root = root;
            }

            public MainViewModel Vm { get; }

            public FakeEngine Engine { get; }

            public ExtractionCoordinator Coordinator { get; }

            public OneClickCoordinator OneClick { get; }

            public RecordingDialogService Dialog { get; }

            public LogService Log { get; }

            public string Root { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(entry => entry.DisplayText);

            public string CreateSource(string fileName)
            {
                string directory = Path.Combine(Root, "src");
                Directory.CreateDirectory(directory);

                string path = Path.Combine(directory, fileName);
                File.WriteAllBytes(path, new byte[512]);

                return path;
            }

            /// <summary>
            /// ⚠ 一次把这一批全加进去：<c>Vm.AddPathsAsync</c> 是**替换语义**（先清空整张表），
            /// 分几次调只会剩下最后一个任务（与既有用例同一个坑）。
            /// </summary>
            public Task AddTasksAsync(params string[] paths) => Vm.AddPathsAsync(paths);
        }

        private sealed class RecordingDialogService : DialogService
        {
            private readonly object _lock = new();

            public List<(string Message, BatchSummarySeverity Severity)> BatchSummaries { get; } = new();

            public override void ShowBatchSummary(string message, BatchSummarySeverity severity)
            {
                lock (_lock)
                {
                    BatchSummaries.Add((message, severity));
                }
            }
        }

        /// <summary>假引擎：可以按文件名指定"这一单怎么失败"（不指定就成功）。</summary>
        private sealed class FakeEngine : IArchiveEngine
        {
            private readonly List<(string Name, int Size)> _products = new() { ("payload.bin", 8) };

            public Dictionary<string, ArchiveOperationResult> Failures { get; } =
                new(StringComparer.OrdinalIgnoreCase);

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
                => Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = _products.Count,
                    TotalUncompressedSize = _products.Sum(p => (long)p.Size),
                    Entries = _products
                        .Select(p => new ArchiveEntry { Path = p.Name, Size = p.Size })
                        .ToList(),
                    EngineId = "fake",
                    EngineVersion = "1.0"
                });

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(Succeeded());

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                string source = Path.GetFileName(request.ArchivePath ?? string.Empty);

                if (Failures.TryGetValue(source, out ArchiveOperationResult? failure))
                {
                    return Task.FromResult(failure);
                }

                string output = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);

                    foreach ((string name, int size) in _products)
                    {
                        File.WriteAllBytes(Path.Combine(output, name), new byte[size]);
                    }
                }

                return Task.FromResult(Succeeded());
            }

            private static ArchiveOperationResult Succeeded() => new()
            {
                Success = true,
                Status = StatusText.ExtractSuccess,
                Message = "解压成功",
                DetectedErrorType = "None"
            };
        }
    }
}
