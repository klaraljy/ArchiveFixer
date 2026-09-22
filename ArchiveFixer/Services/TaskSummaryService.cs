using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 任务汇总的**结果分桶**：每个任务恰好落进一个桶。
    ///
    /// 为什么要有这个东西：旧的 <see cref="TaskSummaryService.BuildSummary"/> 用一堆互不排斥的
    /// <c>Count(...)</c> 拼出来，同一个任务会同时进"解压失败"和"其他失败"（`UnknownError`）、
    /// 同时进"解压失败"和"密码错误"（`WrongPassword`）—— 用户看到的就是"汇总数字前后对不上：
    /// 成功 + 失败 + 跳过 比总数还大"。分桶是唯一真相来源，界面字段只能从桶里取数，
    /// 不允许再出现第二套判断。
    /// </summary>
    public enum SummaryBucket
    {
        /// <summary>还没有结果：等待扫描 / 已识别 / 进行中。它也是分桶的一部分，只是界面上没有单独一项。</summary>
        Pending = 0,

        RenameSuccess,
        RenameFailed,
        TestPassed,
        TestFailed,
        ExtractSuccess,

        /// <summary>密码错误（<see cref="StatusText.WrongPassword"/>）。**只有它算这一类**。</summary>
        PasswordError,

        Corrupted,

        /// <summary>解压侧其它失败：解压失败 / 权限不足 / 输出路径冲突 / 分卷缺失 / 路径过长 / 7z不存在 / 未知错误 / 达到密码尝试上限。</summary>
        ExtractFailed,

        Skipped,
        Cancelled,

        /// <summary>部分完成、格式未知、文件名已加密，以及将来新增但还没归类的失败状态。</summary>
        OtherFailed
    }

    /// <summary>
    /// 各分桶的计数。**硬不变量：<see cref="Total"/> 恒等于任务总数**（互斥且可加），
    /// 所以"各分项之和 == 任务数"是构造出来的性质，不是靠调用方自觉。
    /// </summary>
    public sealed class SummaryBucketCounts
    {
        public int Pending { get; init; }

        public int RenameSuccess { get; init; }

        public int RenameFailed { get; init; }

        public int TestPassed { get; init; }

        public int TestFailed { get; init; }

        public int ExtractSuccess { get; init; }

        public int PasswordError { get; init; }

        public int Corrupted { get; init; }

        public int ExtractFailed { get; init; }

        public int Skipped { get; init; }

        public int Cancelled { get; init; }

        public int OtherFailed { get; init; }

        /// <summary>全部分桶之和 —— 按定义等于任务总数。</summary>
        public int Total =>
            Pending +
            RenameSuccess + RenameFailed +
            TestPassed + TestFailed +
            ExtractSuccess + ExtractFailed +
            PasswordError + Corrupted +
            Skipped + Cancelled + OtherFailed;

        /// <summary>失败侧合计（与界面"失败总数"同口径，不含"待处理"，也不含任何重复计数）。</summary>
        public int FailureTotal =>
            RenameFailed + TestFailed + ExtractFailed + PasswordError + Corrupted + OtherFailed;

        public int Get(SummaryBucket bucket)
        {
            return bucket switch
            {
                SummaryBucket.Pending => Pending,
                SummaryBucket.RenameSuccess => RenameSuccess,
                SummaryBucket.RenameFailed => RenameFailed,
                SummaryBucket.TestPassed => TestPassed,
                SummaryBucket.TestFailed => TestFailed,
                SummaryBucket.ExtractSuccess => ExtractSuccess,
                SummaryBucket.PasswordError => PasswordError,
                SummaryBucket.Corrupted => Corrupted,
                SummaryBucket.ExtractFailed => ExtractFailed,
                SummaryBucket.Skipped => Skipped,
                SummaryBucket.Cancelled => Cancelled,
                SummaryBucket.OtherFailed => OtherFailed,
                _ => Pending
            };
        }
    }

    /// <summary>
    /// 任务汇总服务。
    /// </summary>
    public class TaskSummaryService
    {
        /// <summary>失败清单里第二级（"归档内条目 / 失败层"）的缩进。</summary>
        public const string DetailIndent = "  ";

        private const string ArchiveLinePrefix = "[归档] ";
        private const string FailedListTitle = "ArchiveFixer 失败清单";
        private const string GeneratedAtLabel = "生成时间：";
        private const string EngineLabel = "引擎：";
        private const string EngineVerdictLabel = "引擎结论：";
        private const string LayerLabel = "层级：";
        private const string VerifyLabel = "校验：";
        private const string VolumeLabel = "分卷：缺少 ";
        private const string EntryLabel = "条目：";
        private const string LocationLabel = "位置：";

        /// <summary>
        /// 本次选项那一行（规格 <c>docs/输出与整理模型.md</c> §9.2 硬要求⑥）。
        /// 只在任务上有值时才写行，所以既有清单的格式（与钉住格式的用例）一个字都没变。
        /// </summary>
        private const string RunOptionsLabel = "本次选项：";
        private const string FailedCountLabel = "失败：";
        private const string NoFailedTaskText = "没有失败任务。";

        private EngineIdentity? _engineIdentity;

        /// <summary>
        /// 本次运行实际使用的引擎（失败清单里必须带引擎名 + 版本，AGENTS.md §6 第 14 条）。
        ///
        /// 默认按<b>引擎注册表</b>解析（不是在这里写死 7-Zip），并且是**惰性**的：
        /// 只有真的要出报告时才去查版本，构造服务本身不碰文件系统。
        /// 可注入，测试与将来"多个引擎各报各的"都从这里替换。
        /// </summary>
        public EngineIdentity EngineIdentity
        {
            get => _engineIdentity ??= EngineIdentityResolver.ResolveDefault();
            set => _engineIdentity = value ?? EngineIdentityResolver.Unavailable;
        }

        /// <summary>
        /// 某个任务**实际**用的引擎（可选来源）。
        ///
        /// <para>
        /// 为什么要留这个口子：<see cref="EngineIdentity"/> 回答的是"这个程序的通用引擎是谁"，
        /// 而多引擎接进流程之后，一个 RAR 走 UnRAR、一个 zip 走 7-Zip 是**常态** ——
        /// 报告里逐个任务都写"7-Zip"就把溯源写错了（不变量 14 要求追到**具体**引擎）。
        /// 真正干活的引擎会在结果上盖戳（<c>ArchiveOperationResult.EngineId/EngineVersion</c>），
        /// 由调用方（主窗口）按归档路径取回，这里只管用。
        /// </para>
        /// <para>
        /// 返回 null / 不接这个口子时，逐任务那一行退回 <see cref="EngineIdentity"/> ——
        /// 也就是说**引擎名 + 版本在任何情况下都不会缺**（不变量 14 的底线）。
        /// </para>
        /// </summary>
        public Func<ArchiveTask, EngineIdentity?>? EngineIdentityProvider { get; set; }

        /// <summary>
        /// "归档内条目 / 失败层"明细的**外部来源**（可选）。
        ///
        /// 为什么留这个口子：任务模型本身只记到"这个包失败了"这一层，
        /// 而当一个包里有 3 个条目失败、另 200 个成功时，用户要看的正是那 3 个条目的名字。
        /// 逐条目结果在解压流程里（流水线手上），所以由调用方把行喂进来 ——
        /// 没接这个口子时，第二级仍由任务自身的结构化字段（引擎 / 层级 / 校验 / 分卷 / 位置）组成，
        /// **不会**凭空编条目名。
        /// </summary>
        public Func<ArchiveTask, IEnumerable<string>?>? EntryDetailProvider { get; set; }

        /// <summary>
        /// 把一个任务归到唯一的结果分桶。
        ///
        /// 判定顺序：先看<strong>终态</strong> <see cref="ArchiveTask.Status"/>（它是"这件事最后怎么了"的唯一权威），
        /// 再看 <see cref="ArchiveTask.Operation"/>（只用来兜底"跳过"：有些路径只标了操作没标状态）。
        /// <see cref="ArchiveTask.PasswordStatus"/> **不参与**判定 —— 它会在同一次任务里被反复改
        /// （试了错密码、换了下一个候选、最后解压成功），拿它分桶必然重复计数。
        /// </summary>
        public static SummaryBucket ClassifyOutcome(ArchiveTask? task)
        {
            if (task == null)
            {
                return SummaryBucket.Pending;
            }

            string status = task.Status ?? string.Empty;

            if (status == StatusText.RenameSuccess)
            {
                return SummaryBucket.RenameSuccess;
            }

            if (status == StatusText.RenameFailed)
            {
                return SummaryBucket.RenameFailed;
            }

            if (status == StatusText.TestPassed)
            {
                return SummaryBucket.TestPassed;
            }

            if (status == StatusText.TestFailed)
            {
                return SummaryBucket.TestFailed;
            }

            if (status == StatusText.ExtractSuccess || status == StatusText.Overwritten)
            {
                return SummaryBucket.ExtractSuccess;
            }

            if (status == StatusText.WrongPassword)
            {
                return SummaryBucket.PasswordError;
            }

            if (status == StatusText.Corrupted)
            {
                return SummaryBucket.Corrupted;
            }

            if (IsExtractFailureStatus(status))
            {
                return SummaryBucket.ExtractFailed;
            }

            if (status == StatusText.Skipped)
            {
                return SummaryBucket.Skipped;
            }

            if (status == StatusText.Cancelled)
            {
                return SummaryBucket.Cancelled;
            }

            if (status == StatusText.PartiallyCompleted || status == StatusText.UnknownFormat)
            {
                return SummaryBucket.OtherFailed;
            }

            /*
             * 文件名已加密（RAR -hp / 7z -mhe）单独判，且**不能**并进"密码错误"分桶：
             * 汇总行上那一格叫"密码错误"，把"内容无法判定"的包数进去，
             * 等于在汇总层面重新给出了那个错误结论（用户会以为密码本错了）。
             * 它归"其他失败"，与"部分完成"同一档：要人看一眼，绝不是成功。
             */
            if (status == StatusText.EncryptedHeaders)
            {
                return SummaryBucket.OtherFailed;
            }

            // 非终态：只有操作被标成"跳过"时才算跳过，其余都是"还没结果"。
            if (task.Operation == StatusText.OpSkip)
            {
                return SummaryBucket.Skipped;
            }

            return SummaryBucket.Pending;
        }

        /// <summary>按分桶统计。这是 <see cref="BuildSummary"/> 唯一的取数来源。</summary>
        public SummaryBucketCounts CountBuckets(IEnumerable<ArchiveTask>? tasks)
        {
            int[] counts = new int[Enum.GetValues<SummaryBucket>().Length];

            foreach (ArchiveTask task in tasks ?? Enumerable.Empty<ArchiveTask>())
            {
                if (task == null)
                {
                    continue;
                }

                counts[(int)ClassifyOutcome(task)]++;
            }

            return new SummaryBucketCounts
            {
                Pending = counts[(int)SummaryBucket.Pending],
                RenameSuccess = counts[(int)SummaryBucket.RenameSuccess],
                RenameFailed = counts[(int)SummaryBucket.RenameFailed],
                TestPassed = counts[(int)SummaryBucket.TestPassed],
                TestFailed = counts[(int)SummaryBucket.TestFailed],
                ExtractSuccess = counts[(int)SummaryBucket.ExtractSuccess],
                PasswordError = counts[(int)SummaryBucket.PasswordError],
                Corrupted = counts[(int)SummaryBucket.Corrupted],
                ExtractFailed = counts[(int)SummaryBucket.ExtractFailed],
                Skipped = counts[(int)SummaryBucket.Skipped],
                Cancelled = counts[(int)SummaryBucket.Cancelled],
                OtherFailed = counts[(int)SummaryBucket.OtherFailed]
            };
        }

        /// <summary>
        /// 构建任务汇总。
        ///
        /// 11 个分项（改名成功/改名失败/测试通过/测试失败/解压成功/解压失败/密码错误/文件损坏/已跳过/已取消/其他失败）
        /// 全部来自互斥分桶，**两两不重叠**，因此：
        /// 分项之和 + 待处理（<see cref="SummaryBucket.Pending"/>，界面上没有单项）= 任务总数。
        ///
        /// 已识别 / 格式未知这两项是"识别阶段"的属性，不是结果分桶，**不参与求和**
        /// （一个包可以既"已识别"又"解压成功"）。
        /// </summary>
        public TaskSummary BuildSummary(IEnumerable<ArchiveTask> tasks)
        {
            var list = tasks?.Where(x => x != null).ToList() ?? new List<ArchiveTask>();
            SummaryBucketCounts buckets = CountBuckets(list);

            return new TaskSummary
            {
                TotalCount = list.Count,
                SelectedCount = list.Count(x => x.IsSelected),

                RecognizedCount = list.Count(x =>
                    x.IsArchive ||
                    x.Status == StatusText.Recognized ||
                    x.ExtensionStatus == StatusText.ExtensionNormal ||
                    x.ExtensionStatus == StatusText.ExtensionMissing ||
                    x.ExtensionStatus == StatusText.ExtensionMismatch ||
                    x.ExtensionStatus == StatusText.ExtensionMultiFake ||
                    x.ExtensionStatus == StatusText.ExtensionVolume ||
                    // 内嵌归档也算"已识别"：它的格式是确定的（ZIP，只是藏在文件尾部），
                    // 归到"未知"会让用户以为识别失败，从而去改一个根本不该改的后缀。
                    x.ExtensionStatus == StatusText.ExtensionEmbedded),

                UnknownCount = list.Count(x =>
                    x.DetectedFormat == "Unknown" ||
                    x.Status == StatusText.UnknownFormat ||
                    x.ExtensionStatus == StatusText.UnknownFormat),

                RenameSuccessCount = buckets.RenameSuccess,
                RenameFailedCount = buckets.RenameFailed,

                TestSuccessCount = buckets.TestPassed,
                TestFailedCount = buckets.TestFailed,

                ExtractSuccessCount = buckets.ExtractSuccess,
                ExtractFailedCount = buckets.ExtractFailed,

                PasswordErrorCount = buckets.PasswordError,
                CorruptedCount = buckets.Corrupted,

                SkippedCount = buckets.Skipped,
                CancelledCount = buckets.Cancelled,

                /*
                 * "部分完成"计入失败侧：它确实没做完，用户还要处理。
                 * 宁可让统计显得悲观，也不能让"部分完成"混进成功数里 —— 那正是设计.md 不变量 9 要防的事。
                 */
                OtherFailedCount = buckets.OtherFailed
            };
        }

        /// <summary>解压侧的失败状态（不含密码错误 / 文件损坏：它们各自单独成项）。</summary>
        private static bool IsExtractFailureStatus(string status)
        {
            return status is
                StatusText.ExtractFailed or
                StatusText.AccessDenied or
                StatusText.OutputConflict or
                StatusText.VolumeMissing or
                StatusText.PathTooLong or
                StatusText.SevenZipMissing or
                StatusText.UnknownError or
                StatusText.PasswordAttemptLimitReached;
        }

        /// <summary>
        /// 获取失败任务。
        /// </summary>
        public List<ArchiveTask> GetFailedTasks(IEnumerable<ArchiveTask> tasks)
        {
            return tasks?
                .Where(x => x != null && IsFailedOrUnknownTask(x))
                .ToList()
                ?? new List<ArchiveTask>();
        }

        /// <summary>
        /// 构建失败列表文本（导出与复制共用同一份，格式是 txt）。
        ///
        /// 抄的是 WinRAR <c>-log[AF]</c> 的**两级分组**：一级一行"归档"，二级缩进写
        /// "引擎 / 层级 / 校验 / 分卷 / 条目 / 位置"。旧实现只有一行 <c>文件名 - 原因</c>，
        /// 一个包里有 3 个条目失败、另 200 个成功时根本说不清（M3 的验收判据就是"能说清每个为什么失败"）。
        ///
        /// 表头三行是"结果可追溯"的落点（不变量 14）：生成时间（**固定格式**，见 LogService）
        /// 与引擎名 + 版本。以前失败清单里一个字都没有，事后无法回答"这是哪个版本跑出来的"。
        /// 多引擎接进流程之后，表头列的是**本次实际用过的引擎**（可能不止一个），
        /// 而逐个归档那一行写的是**它自己**用的那个（见 <see cref="EngineIdentityProvider"/>）。
        /// </summary>
        public string BuildFailedListText(IEnumerable<ArchiveTask> tasks)
        {
            var allTasks = tasks?.Where(x => x != null).ToList() ?? new List<ArchiveTask>();
            List<ArchiveTask> failedTasks = GetFailedTasks(allTasks);

            var builder = new StringBuilder();

            builder.AppendLine(FailedListTitle);
            builder.AppendLine(GeneratedAtLabel + LogService.FormatTimestamp(DateTime.Now));
            builder.AppendLine(EngineLabel + DescribeRunEngines(allTasks));

            if (failedTasks.Count == 0)
            {
                builder.Append(NoFailedTaskText);
                return builder.ToString().TrimEnd();
            }

            builder.AppendLine($"{FailedCountLabel}{failedTasks.Count} 个任务（共 {allTasks.Count} 个）");
            builder.AppendLine();

            foreach (ArchiveTask task in failedTasks)
            {
                string fileName = !string.IsNullOrWhiteSpace(task.FileName)
                    ? task.FileName
                    : Path.GetFileName(task.CurrentPath);

                string reason = GetFailureReason(task);

                builder.Append(ArchiveLinePrefix);
                builder.Append(fileName);
                builder.Append(" - ");
                builder.AppendLine(reason);

                foreach (string line in BuildFailureDetailLines(task))
                {
                    builder.Append(DetailIndent);
                    builder.AppendLine(line);
                }

                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// 失败清单的第二级：这个归档内部到底怎么了（"归档内条目 / 失败层"）。
        ///
        /// 只发布**结构化的既有字段**，不解析中文文案（AGENTS.md §7：统计与判定不得依赖文案比较）：
        /// · 引擎名 + 版本 —— 不变量 14，逐任务一行（**优先写这个任务实际用的那个引擎**，
        ///   见 <see cref="EngineIdentityProvider"/>；问不到才退回通用引擎身份）；
        /// · 层级 —— 是用户给的源包，还是续解出来的内层包（含父包名）；
        /// · 引擎结论 / 校验结论 —— 原样带出（里面有 7-Zip 报的条目数、预期与实际的文件数落差）；
        /// · 分卷缺哪几个 —— 不变量 7 要求"报缺哪几个"，失败清单里同样要说清；
        /// · 条目 —— 由 <see cref="EntryDetailProvider"/> 提供（有就写，没有就不编）；
        /// · 位置 —— 完整路径，用户要能直接找到那个包。
        /// </summary>
        public IReadOnlyList<string> BuildFailureDetailLines(ArchiveTask? task)
        {
            var lines = new List<string>();

            if (task == null)
            {
                return lines;
            }

            lines.Add(EngineLabel + (ResolveTaskEngine(task) ?? EngineIdentity).Describe());

            lines.Add(task.IsContinuationTask
                ? LayerLabel + $"内层包（父包：{ResolveParentName(task)}）"
                : LayerLabel + "第 0 层（用户给的源包）");

            if (!string.IsNullOrWhiteSpace(task.EngineVerdict))
            {
                lines.Add(EngineVerdictLabel + task.EngineVerdict);
            }

            if (!string.IsNullOrWhiteSpace(task.VerifyMessage))
            {
                lines.Add(VerifyLabel + task.VerifyMessage);
            }

            if (task.IsVolumeGroup && task.MissingVolumeNames.Count > 0)
            {
                lines.Add(VolumeLabel + string.Join('、', task.MissingVolumeNames));
            }

            IEnumerable<string>? entryLines = null;

            try
            {
                entryLines = EntryDetailProvider?.Invoke(task);
            }
            catch
            {
                // 明细来源坏了不能把整份失败清单带崩：清单是失败后唯一的可读产物。
                entryLines = null;
            }

            foreach (string entryLine in entryLines ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(entryLine))
                {
                    lines.Add(EntryLabel + entryLine.Trim());
                }
            }

            if (!string.IsNullOrWhiteSpace(task.CurrentPath))
            {
                lines.Add(LocationLabel + task.CurrentPath);
            }

            /*
             * 「本次为什么解到这里」（规格 §9.2 硬要求⑥）：一键处理带着"本次选项"跑过时才有值。
             * 放在最后一行 —— 用户顺着读下来是"引擎 → 层级 → 结论 → 条目 → 在哪 → 为什么在那"。
             */
            if (!string.IsNullOrWhiteSpace(task.RunOptionsNote))
            {
                lines.Add(RunOptionsLabel + task.RunOptionsNote);
            }

            return lines;
        }

        /// <summary>
        /// 某个任务**实际**用的引擎；问不到（没接口子、或这个任务根本没跑过引擎）返回 null。
        ///
        /// 与 <see cref="EntryDetailProvider"/> 同一口径：外部来源抛异常**不能**把整份报告带崩 ——
        /// 失败清单是用户失败后唯一能读到的产物。
        /// </summary>
        public EngineIdentity? ResolveTaskEngine(ArchiveTask? task)
        {
            if (task == null || EngineIdentityProvider == null)
            {
                return null;
            }

            try
            {
                return EngineIdentityProvider(task);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 报告表头那一行的引擎信息。
        ///
        /// 优先列**本次这些任务实际用过的**引擎（多引擎分派之下可能不止一个：
        /// RAR 走 UnRAR、zip 走 7-Zip；如实列全，不挑一个当代表）；
        /// 一个都问不到时退回通用引擎身份 —— 两种写法都**一定**带引擎名 + 版本（不变量 14）。
        /// </summary>
        private string DescribeRunEngines(IEnumerable<ArchiveTask>? tasks)
        {
            var used = new List<EngineIdentity>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ArchiveTask task in tasks ?? Enumerable.Empty<ArchiveTask>())
            {
                EngineIdentity? identity = ResolveTaskEngine(task);

                if (identity == null)
                {
                    continue;
                }

                if (seen.Add((identity.EngineId ?? string.Empty) + "|" + (identity.Version ?? string.Empty)))
                {
                    used.Add(identity);
                }
            }

            if (used.Count == 0)
            {
                return EngineIdentity.Describe();
            }

            // 只有一个、且就是通用引擎时保持既有口径（"引擎：7-Zip 命令行 26.01"），不加注解。
            if (used.Count == 1 &&
                string.Equals(used[0].EngineId, EngineIdentity.EngineId, StringComparison.OrdinalIgnoreCase))
            {
                return used[0].Describe();
            }

            return string.Join(" / ", used.Select(x => x.Describe())) + "（本次实际使用）";
        }

        private static string ResolveParentName(ArchiveTask task)
        {
            if (!string.IsNullOrWhiteSpace(task.ParentTaskName))
            {
                return task.ParentTaskName;
            }

            // 父包名字没记下来时用父输出目录兜底 —— 宁可给一个能定位的路径，也不要写"未知"。
            string parentFromPath = Path.GetFileName(
                (task.ParentOutputDirectory ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            return string.IsNullOrWhiteSpace(parentFromPath)
                ? task.ParentOutputDirectory ?? string.Empty
                : parentFromPath;
        }

        /// <summary>
        /// 获取失败原因。
        /// </summary>
        public string GetFailureReason(ArchiveTask task)
        {
            if (task == null)
            {
                return StatusText.UnknownError;
            }

            if (!string.IsNullOrWhiteSpace(task.ErrorMessage))
            {
                return task.ErrorMessage;
            }

            if (!string.IsNullOrWhiteSpace(task.Status))
            {
                return task.Status;
            }

            if (!string.IsNullOrWhiteSpace(task.ExtensionStatus))
            {
                return task.ExtensionStatus;
            }

            return StatusText.UnknownError;
        }

        /// <summary>
        /// 判断是否失败状态。
        /// </summary>
        public bool IsFailedStatus(string status)
        {
            return status is
                StatusText.UnknownFormat or
                StatusText.RenameFailed or
                StatusText.TestFailed or
                StatusText.ExtractFailed or
                StatusText.WrongPassword or
                StatusText.Corrupted or
                StatusText.AccessDenied or
                StatusText.OutputConflict or
                StatusText.VolumeMissing or
                StatusText.PathTooLong or
                StatusText.SevenZipMissing or
                StatusText.PasswordAttemptLimitReached or
                // 部分完成是"没做完"，必须进失败清单（不变量 6：不得显示成成功）。
                StatusText.PartiallyCompleted or
                // 文件名已加密：这一单没拿到可用结论，用户必须看见（否则它就消失在"处理完了"里）。
                StatusText.EncryptedHeaders or
                StatusText.UnknownError;
        }

        /// <summary>
        /// 判断是否需要进入失败列表。
        /// </summary>
        public bool IsFailedOrUnknownTask(ArchiveTask task)
        {
            if (task == null)
            {
                return false;
            }

            if (IsFailedStatus(task.Status))
            {
                return true;
            }

            if (task.DetectedFormat == "Unknown" ||
                task.ExtensionStatus == StatusText.UnknownFormat)
            {
                return true;
            }

            return false;
        }
    }
}
