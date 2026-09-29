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
        private Harness CreateHarness()
        {
            string dataRoot = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N"));
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
            settings.TryEmptyPasswordFirst = false;
            settings.RemindBeforeExtract = false;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            settings.MaxParallelExtractCount = 1;
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
