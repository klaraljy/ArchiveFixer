using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArchiveFixer.Helpers;
using ArchiveFixer.Password;
using ArchiveFixer.Models;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 日志服务。
    /// 负责屏幕日志、文件日志、密码脱敏、导出日志、打开日志目录。
    /// </summary>
    public class LogService
    {
        /// <summary>
        /// 日志 / 报告里时间戳的**固定格式**（不随系统区域设置、也不随系统日历变）。
        ///
        /// 为什么必须显式带上 <see cref="CultureInfo.InvariantCulture"/>：
        /// <c>DateTime.ToString("yyyy-MM-dd HH:mm:ss")</c> 里的 <c>yyyy</c> 用的是**当前区域的日历** ——
        /// 在泰历（th-TH）这类区域里同一个时刻会写成 2569 年，日志立刻变成对不上号的文本。
        /// 日志和报告是要被解析、被比对、被贴进问题反馈的文本，格式必须与用户机器设置无关
        /// （WinRAR 的"生成报告"同样固定成 <c>YYYY-MM-DD hh:mm</c>，理由一样）。
        ///
        /// ⚠ 与 <c>Models/OperationLogItem.TimeText</c> 用的是**同一个格式**：屏幕日志那一行也是
        /// <c>yyyy-MM-dd HH:mm:ss</c>。既有日志解析按这个格式写的，改这里必须连着看那里（口径只能有一份）。
        /// </summary>
        public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

        /// <summary>日志文件名里的紧凑时间戳格式（同样固定文化，避免区域日历换出别的年份）。</summary>
        public const string FileNameTimestampFormat = "yyyyMMdd_HHmmss";

        /// <summary>
        /// 把时间按 <see cref="TimestampFormat"/> 格式化。全仓的日志/报告时间戳都走这里，
        /// **不要**再写 <c>DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")</c>（那样会各自跟随区域日历）。
        /// </summary>
        public static string FormatTimestamp(DateTime value)
        {
            return value.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>把时间按 <see cref="FileNameTimestampFormat"/> 格式化（日志文件名用）。</summary>
        public static string FormatFileTimestamp(DateTime value)
        {
            return value.ToString(FileNameTimestampFormat, CultureInfo.InvariantCulture);
        }

        private readonly PathService _pathService;
        private readonly object _lockObj = new();

        private string _currentLogFilePath = string.Empty;

        /// <summary>
        /// 普通档（用户看到的/导出的）日志文件：只写普通行，不含详细探针。
        /// 内部排查用的 `.log` 仍然全量详细；两者用同一个时间戳基名，靠后缀区分。
        /// </summary>
        private string _currentUserLogFilePath = string.Empty;

        private string _currentJsonlFilePath = string.Empty;
        private string _currentTraceFilePath = string.Empty;
        private bool _enableFileLog = true;

        /// <summary>本次进程运行 id（每次启动一个；日志只靠它就能区分“哪一次运行”）。</summary>
        private readonly string _runId = Guid.NewGuid().ToString("N");

        /// <summary>本次运行内的日志序号（从 1 开始；人读/机器读同一条同号）。</summary>
        private long _sequence;

        /// <summary>诊断事件专用序号（不参与人读/JSONL 的一比一）。</summary>
        private long _traceSequence;

        /// <summary>本进程启动时间（环境头用）。</summary>
        private readonly DateTime _processStarted = Process.GetCurrentProcess().StartTime;

        /// <summary>
        /// **这一行的来源上下文**（批次 / 轮次 / 样本 / 阶段 / 任务 / 层号）—— 大测试的"只看日志排查"靠它。
        ///
        /// <para>写点：调用方开工时 <see cref="UpdateRunContext"/>；读点：<see cref="BuildJsonRecord"/> 把它抄进每一行。
        /// ⛔ 只影响**日志记录**，⛔ 不参与任何判据、⛔ 不改任何行为。</para>
        /// </summary>
        public LogRunContext RunContext { get; private set; } = new();

        /// <summary>换一次上下文（只覆盖**显式给了**的那几格；判据一个字都不受影响）。</summary>
        public void UpdateRunContext(
            string? batch = null,
            int? round = null,
            string? sample = null,
            string? stage = null,
            string? task = null,
            int? layer = null)
        {
            RunContext = RunContext with
            {
                Batch = batch ?? RunContext.Batch,
                Round = round ?? RunContext.Round,
                Sample = sample ?? RunContext.Sample,
                Stage = stage ?? RunContext.Stage,
                Task = task ?? RunContext.Task,
                Layer = layer ?? RunContext.Layer
            };
        }

        public LogService()
            : this(new PathService())
        {
        }

        public LogService(PathService pathService)
        {
            _pathService = pathService;
        }

        /// <summary>
        /// 屏幕日志集合。
        /// MainViewModel 可以直接绑定该集合，或者订阅 LogAdded 事件后自己维护集合。
        /// </summary>
        public ObservableCollection<OperationLogItem> Logs { get; } = new();

        /// <summary>
        /// 日志新增事件。
        /// </summary>
        public event EventHandler<OperationLogItem>? LogAdded;

        /// <summary>
        /// 当前日志文件路径。
        /// </summary>
        public string CurrentLogFilePath => _currentLogFilePath;

        /// <summary>
        /// 当前**机器读**的那一份（`.jsonl`：一行一个 JSON 对象）—— 与人读的 `.log` **一比一**（同一处写入）。
        ///
        /// <para>为什么要有它：用户出问题时**只会发日志**，而人读行里的判据输入是中文散文
        /// ⇒ 逐条筛/汇总/比对都没有稳定形状。JSONL 给人读行配一份**字段固定**的孪生记录
        /// （时间/级别/批次/轮次/样本/阶段/任务/层/文件/身份/判据名/判据输入/结论/动作/耗时），
        /// 于是"只看日志"也能把每一处判据的输入与结论摆出来。</para>
        /// </summary>
        public string CurrentJsonlFilePath => _currentJsonlFilePath;

        /// <summary>
        /// 当前**诊断事件**那一份（`.trace.jsonl`：schema v2，一批一条 JSON，不占人读行）。
        /// 它与 `.log`/`.jsonl` 分离：成功任务的屏幕日志仍只留少量行，详细的批次/任务/文件/引擎事实全在这里。
        /// </summary>
        public string CurrentTraceFilePath => _currentTraceFilePath;

        /// <summary>
        /// 是否往文件里写日志（对应设置项「启用日志文件」，默认开）。
        ///
        /// 关掉只影响**写盘**：屏幕日志照旧（用户还得看见程序在干什么），
        /// 而且 <see cref="Initialize"/> 仍然把当前日志文件路径算好，重新打开就能接着写同一个文件。
        /// </summary>
        public bool EnableFileLog
        {
            get => _enableFileLog;
            set => _enableFileLog = value;
        }

        /// <summary>
        /// 日志目录。
        /// </summary>
        public string LogDirectory => _pathService.LogsDirectory;

        /// <summary>
        /// 本次操作开始处的**屏幕日志下标**（-1 = 还没有标记）。
        ///
        /// <para>用户 2026-09-25 第 38 条："我想要的是本次操作的日志，也就是我最近一次点开操作的日志，
        /// 我看着全部的日志非常的累" —— 所以导出要能只给"最近这一次操作"，
        /// 而不是把历史日志全拼在一起。</para>
        /// </summary>
        private int _operationStartIndex = -1;

        /// <summary>本次操作在日志文件里的起始**行号**（-1 = 没标记 / 不在同一个文件里）。</summary>
        private int _operationStartFileLine = -1;

        /// <summary>同一次操作在普通档 `.user.log` 里的起始行（详细档与普通档行数不同，不能共用）。</summary>
        private int _operationStartUserFileLine = -1;

        /// <summary>标记所在的那个日志文件（换文件之后旧行号就不能用了）。</summary>
        private string _operationStartFile = string.Empty;

        /// <summary>本次操作开始的时间（固定格式字符串；导出时用它过滤 trace 旧事件）。</summary>
        private string _operationStartTimestamp = string.Empty;

        /// <summary>普通档 `.user.log` 的操作起点（文件换名后旧行号不能复用）。</summary>
        private string _operationStartUserFile = string.Empty;

        /// <summary>
        /// 标记"一次新的操作开始了"（用户点了一次处理 / 一次扫描）。
        ///
        /// <para>写一行醒目的分隔线进日志，并记下当前位置 —— 这样**导出的日志**与用户自己打开日志文件
        /// 看到的分界完全一致（"这次操作从哪儿开始"一眼可见）。</para>
        /// </summary>
        public void MarkOperationStart(string operationName)
        {
            AppendOperationSeparator(operationName);

            _operationStartIndex = Math.Max(0, Logs.Count - 1);
            _operationStartFile = _currentLogFilePath;
            _operationStartFileLine = TryCountLines(_currentLogFilePath) - 1;
            _operationStartUserFile = _currentUserLogFilePath;
            _operationStartUserFileLine = TryCountLines(_currentUserLogFilePath) - 1;
            _operationStartTimestamp = FormatTimestamp(DateTime.Now);
        }

        /// <summary>把"本次操作开始"写进日志（屏幕 + 文件；关掉文件日志时只进屏幕日志）。</summary>
        private void AppendOperationSeparator(string operationName)
        {
            string message = $"======== 本次操作开始：{operationName} ========";

            try
            {
                Write("INFO", message);
            }
            catch
            {
                // 写日志失败绝不能影响操作本身（与既有 Write 的口径一致）。
            }
        }

        /// <summary>
        /// **只导出"本次操作"的日志**（最近一次标记之后的部分）。
        ///
        /// <para>优先按日志文件导出（与用户手工打开看到的一致）；没有文件（关掉了文件日志）就退回屏幕日志。
        /// 没有任何标记时导出**当前这一份日志文件**（= 本次运行）—— 那已经是他要的范围，绝不拼历史文件。</para>
        /// </summary>
        /// <returns>导出的行数，以及是不是"本次操作"的范围（false = 退回了整份当前日志）。</returns>
        public (int Lines, bool FromOperationMarker) ExportOperationLog(
            string targetPath,
            IReadOnlyList<string>? headerLines = null,
            bool includeDetails = true,
            IReadOnlyList<string>? extraSections = null)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return (0, false);
            }

            string? directory = Path.GetDirectoryName(targetPath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var builder = new StringBuilder();
            bool fromMarker = false;
            int lines = 0;

            /*
             * 导出文件**加个头**（用户 2026-09-25 第 44 条追加拍板）：
             * 时间范围 / 任务数 / 成功失败 / 引擎版本 / 输出根 + "细节在哪看"。
             * 为什么要它：这份文件是发给我排查用的 —— 没有头，读的人得先自己找"这是哪一批、跑成什么样"。
             * ⚠ 头部行**不算进 lineCount**（那是"日志多少行"的数字，别把它算成日志内容）。
             */
            if (headerLines != null && headerLines.Count > 0)
            {
                foreach (string line in headerLines)
                {
                    builder.AppendLine(line);
                }

                builder.AppendLine();
            }

            /*
             * 用户 2026-10-26：这份 txt 是唯一可能拿到的文件，所以先放“能直接定位问题”的区块：
             * 失败/取消/部分完成、分卷组、源包处理。它们由 MainViewModel 从任务事实生成，
             * 不再靠把整份 trace 倒进来。
             */
            if (extraSections != null && extraSections.Count > 0)
            {
                foreach (string line in extraSections)
                {
                    builder.AppendLine(line);
                }

                builder.AppendLine();
            }

            var body = new List<string>();
            string selectedFile = includeDetails
                ? _currentLogFilePath
                : _currentUserLogFilePath;
            int selectedStart = includeDetails
                ? _operationStartFileLine
                : _operationStartUserFileLine;
            string selectedOperationFile = includeDetails
                ? _operationStartFile
                : _operationStartUserFile;

            if (!string.IsNullOrWhiteSpace(selectedFile) && File.Exists(selectedFile))
            {
                string[] all = File.ReadAllLines(selectedFile);
                int start = 0;

                if (selectedStart >= 0 &&
                    string.Equals(selectedOperationFile, selectedFile, StringComparison.OrdinalIgnoreCase) &&
                    selectedStart < all.Length)
                {
                    start = selectedStart;
                    fromMarker = true;
                }

                for (int i = start; i < all.Length; i++)
                {
                    body.Add(all[i]);
                }
            }
            else
            {
                int start = _operationStartIndex >= 0 ? _operationStartIndex : 0;
                fromMarker = _operationStartIndex >= 0;

                for (int i = start; i < Logs.Count; i++)
                {
                    body.Add(Logs[i].DisplayText);
                }
            }

            builder.AppendLine("================ 以下是日志正文（已去重；任务行快照已移到上面的任务明细） ================");

            foreach (string line in DeduplicateReportBody(body))
            {
                builder.AppendLine(line);
                lines++;
            }

            /*
             * 只附**本次操作范围内、精简后的关键事件**：组事实 / 文件操作 / 异常。
             * ⛔ 不再把 batch.start / batch.end 的整张任务表或 batch 的 JSON 原样塞进来 ——
             * 那正是旧导出“几十行 JSON 占六成体积、目录重复几十遍”的根因。
             */
            AppendCompactDiagnosticEvents(builder);

            File.WriteAllText(targetPath, PasswordMasker.Sanitize(builder.ToString()), new UTF8Encoding(false));

            return (lines, fromMarker);
        }

        /// <summary>
        /// 人读正文去重：去掉任务行快照（上面已有任务明细）与完全相同的重复行。
        /// 保留时间顺序，不改变任何一行的内容。
        /// </summary>
        private static List<string> DeduplicateReportBody(IReadOnlyList<string> body)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string line in body)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.Contains("] [WARN] [行] ", StringComparison.Ordinal)
                    || line.Contains("] [INFO] [行] ", StringComparison.Ordinal))
                {
                    continue;
                }

                if (seen.Add(line))
                {
                    /*
                     * 缺卷清单可能有 100+ 个号码；正文里保留前一段即可，完整清单仍在
                     * data/log 的任务详情与 trace 里。否则单文件报告会被一行几百个数字淹没。
                     */
                    result.Add(line.Length > 1200 && line.Contains("缺=", StringComparison.Ordinal)
                        ? line[..1200] + "…（完整缺卷清单见 data\\logs）"
                        : line);
                }
            }

            return result;
        }

        /// <summary>
        /// 追加本次操作范围内的关键结构化事件（不含批量任务数组）。
        /// 目的：用户只给一个 txt 时仍能看见组事实与文件操作，又不会被重复路径淹没。
        /// </summary>
        private void AppendCompactDiagnosticEvents(StringBuilder builder)
        {
            if (string.IsNullOrWhiteSpace(_currentTraceFilePath) ||
                !File.Exists(_currentTraceFilePath))
            {
                return;
            }

            string[] traceLines;

            try
            {
                traceLines = File.ReadAllLines(_currentTraceFilePath);
            }
            catch
            {
                return;
            }

            var emitted = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string traceLine in traceLines)
            {
                if (traceLine.Length == 0)
                {
                    continue;
                }

                try
                {
                    using JsonDocument document = JsonDocument.Parse(traceLine);
                    JsonElement root = document.RootElement;

                    string timestamp = root.TryGetProperty("ts", out JsonElement ts) ? ts.GetString() ?? string.Empty : string.Empty;

                    if (!string.IsNullOrWhiteSpace(_operationStartTimestamp) &&
                        !string.IsNullOrWhiteSpace(timestamp) &&
                        string.CompareOrdinal(timestamp, _operationStartTimestamp) < 0)
                    {
                        continue;
                    }

                    string eventName = root.TryGetProperty("event", out JsonElement eventElement)
                        ? eventElement.GetString() ?? string.Empty
                        : string.Empty;

                    if (eventName.Length == 0 ||
                        eventName == "batch.start" ||
                        eventName == "batch.end" ||
                        eventName == "task.begin" ||
                        eventName == "session.settings" ||
                        eventName == "session.environment")
                    {
                        continue;
                    }

                    string message = root.TryGetProperty("msg", out JsonElement msgElement)
                        ? msgElement.GetString() ?? string.Empty
                        : eventName;

                    // 组事件展开成一行组事实；其余关键事件只留人话。
                    if (eventName == "group.linked" &&
                        root.TryGetProperty("detail", out JsonElement detailElement) &&
                        detailElement.ValueKind == JsonValueKind.String)
                    {
                        string? detailJson = detailElement.GetString();

                        if (!string.IsNullOrWhiteSpace(detailJson))
                        {
                            using JsonDocument detail = JsonDocument.Parse(detailJson);
                            JsonElement d = detail.RootElement;
                            string group = GetString(d, "group");
                            string family = GetString(d, "family");
                            string state = GetString(d, "state");
                            string verdict = GetString(d, "verdict");
                            int pieces = d.TryGetProperty("pieces", out JsonElement piecesElement) &&
                                         piecesElement.ValueKind == JsonValueKind.Array
                                ? piecesElement.GetArrayLength()
                                : 0;
                            string missing = d.TryGetProperty("missing", out JsonElement missingElement) &&
                                             missingElement.ValueKind == JsonValueKind.Array
                                ? string.Join(",", missingElement.EnumerateArray().Select(x => x.ToString()))
                                : string.Empty;

                            message = $"组「{group}」｜族={family}｜状态={state}｜片={pieces}"
                                + (missing.Length > 0 ? $"｜缺={missing}" : string.Empty)
                                + (verdict.Length > 0 ? $"｜{verdict}" : string.Empty);
                        }
                    }

                    string line = $"[{timestamp}] [{eventName}] {message}";

                    if (seen.Add(line))
                    {
                        emitted.Add(line);
                    }
                }
                catch
                {
                    // trace 单行坏了不影响导出；诊断事件只是辅助。
                }
            }

            if (emitted.Count == 0)
            {
                return;
            }

            const int maxEvents = 200;

            builder.AppendLine();
            builder.AppendLine("================ 关键诊断事件（本次操作范围；已去掉整批任务数组） ================");

            foreach (string line in emitted.Take(maxEvents))
            {
                builder.AppendLine(line);
            }

            if (emitted.Count > maxEvents)
            {
                builder.AppendLine($"……还有 {emitted.Count - maxEvents} 条关键事件未展开（完整原始事件见 data\\logs 的 .trace.jsonl）");
            }
        }

        private static string GetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out JsonElement value)
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        /// <summary>数一个文件有多少行（读不了返回 0）。</summary>
        private static int TryCountLines(string? path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) || !File.Exists(path)
                    ? 0
                    : File.ReadAllLines(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 初始化日志系统。
        /// </summary>
        public void Initialize(bool enableFileLog = true)
        {
            _enableFileLog = enableFileLog;

            try
            {
                _pathService.EnsureBaseDirectories();

                string fileName = $"ArchiveFixer_{FormatFileTimestamp(DateTime.Now)}.log";
                _currentLogFilePath = Path.Combine(LogDirectory, fileName);
                _currentUserLogFilePath = Path.ChangeExtension(_currentLogFilePath, ".user.log");
                _currentJsonlFilePath = Path.ChangeExtension(_currentLogFilePath, ".jsonl");
                _currentTraceFilePath = Path.ChangeExtension(_currentLogFilePath, ".trace.jsonl");

                if (_enableFileLog)
                {
                    /*
                     * 首行时间戳同样走固定格式：这一行是既有日志解析的锚点，别让它随区域设置漂。
                     *
                     * ⚠ **两份都要写**（守门用例 `LogSchemaTests.人读行与机器读行一比一` 当场逮到过：
                     * 原来只有人读那一份被建出来 ⇒ 第一行就破坏了"一比一" ⇒ 后面按行对齐解析全错位）。
                     */
                    DateTime started = DateTime.Now;
                    string header = $"日志初始化";

                    File.WriteAllText(
                        _currentLogFilePath,
                        FormatLogLine(started, "INFO", header) + Environment.NewLine,
                        Encoding.UTF8);

                    if (_currentJsonlFilePath.Length > 0)
                    {
                        File.WriteAllText(
                            _currentJsonlFilePath,
                            BuildJsonRecord(new OperationLogItem("INFO", header, string.Empty), RunContext, null)
                            + Environment.NewLine,
                            Encoding.UTF8);
                    }

                    if (_currentTraceFilePath.Length > 0)
                    {
                        File.WriteAllText(_currentTraceFilePath, string.Empty, Encoding.UTF8);
                    }

                    WriteEnvironmentHeader();

                    // 顺手把老日志收一收（第 44 条追加）：不清理的话日志目录只增不减。
                    CleanupOldLogs();
                }
            }
            catch
            {
                _enableFileLog = false;
                _currentLogFilePath = string.Empty;
            }
        }

        /// <summary>
        /// 日志环境头：OS / .NET / 进程 / 架构 / 启动时间 / 工作目录 / 程序版本。
        /// 以后用户只发日志时，这一条让读日志的人不必再问“什么系统、什么版本、什么时候跑的”。
        /// </summary>
        private void WriteEnvironmentHeader()
        {
            try
            {
                Process process = Process.GetCurrentProcess();
                string detail = JsonSerializer.Serialize(new
                {
                    schema = "archivefixer.log.v2",
                    app_version = typeof(LogService).Assembly.GetName().Version?.ToString() ?? string.Empty,
                    file_version = FileVersionInfo.GetVersionInfo(typeof(LogService).Assembly.Location).FileVersion ?? string.Empty,
                    os = RuntimeInformation.OSDescription,
                    os_arch = RuntimeInformation.OSArchitecture.ToString(),
                    process_arch = RuntimeInformation.ProcessArchitecture.ToString(),
                    framework = RuntimeInformation.FrameworkDescription,
                    dotnet = Environment.Version.ToString(),
                    process_id = process.Id,
                    process_name = process.ProcessName,
                    process_started = FormatTimestamp(process.StartTime),
                    machine = Environment.MachineName,
                    user = Environment.UserName,
                    working_directory = Environment.CurrentDirectory,
                    command_line = Environment.CommandLine,
                    processor_count = Environment.ProcessorCount,
                    is_64bit_process = Environment.Is64BitProcess,
                    log_directory = LogDirectory,
                    culture = CultureInfo.CurrentCulture.Name,
                    ui_culture = CultureInfo.CurrentUICulture.Name
                });

                Event(
                    "session.environment",
                    $"运行环境：{RuntimeInformation.OSDescription}；{RuntimeInformation.FrameworkDescription}；进程 {process.ProcessName}（{process.Id}）",
                    detail: detail);
            }
            catch (Exception exception)
            {
                Exception("写运行环境头失败", exception);
            }
        }

        /// <summary>
        /// 日志目录的**保留策略**（用户 2026-09-25 第 44 条追加：他导出的那两份日志一份 721 KB、一份 1.4 MB，
        /// 目录里已经攒了 56 份、3.4 MB —— 而在这之前**没有任何清理**，只增不减）。
        ///
        /// <para>规则（按时间从旧到新处理，⛔ 绝不动当前这一份、绝不动不匹配 <c>ArchiveFixer_*.log</c> 的文件）：</para>
        /// <list type="number">
        /// <item><description>删掉**超过 <see cref="LogRetentionDays"/> 天**的；</description></item>
        /// <item><description>剩下的若总量超过 <see cref="LogRetentionTotalBytes"/>，从最旧的开始删到限额以内；</description></item>
        /// <item><description>**至少留最近 <see cref="LogRetentionMinFiles"/> 份**（用户随时可能要看昨天那一次）。</description></item>
        /// </list>
        ///
        /// <para>删了就在新日志首行之后写一条 INFO 说清删了几份 / 释放多少 —— 静默删文件是这个项目的红线。</para>
        /// </summary>
        private void CleanupOldLogs()
        {
            try
            {
                var files = new DirectoryInfo(LogDirectory)
                    .GetFiles("ArchiveFixer_*.log", SearchOption.TopDirectoryOnly)
                    .Where(file => !file.Name.EndsWith(".user.log", StringComparison.OrdinalIgnoreCase))
                    .Where(file => !string.Equals(file.FullName, _currentLogFilePath, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(file => file.LastWriteTimeUtc)
                    .ToList();

                if (files.Count <= LogRetentionMinFiles)
                {
                    return;
                }

                DateTime cutoffUtc = DateTime.UtcNow - TimeSpan.FromDays(LogRetentionDays);
                long totalBytes = files.Sum(file => file.Length
                    + TwinLength(file.FullName, ".user.log")
                    + TwinLength(file.FullName, ".jsonl")
                    + TwinLength(file.FullName, ".trace.jsonl"));
                int deleted = 0;
                long freedBytes = 0;

                foreach (FileInfo file in files)
                {
                    // 最近 N 份永远留着（下面是按时间升序，最后 N 份在队尾）。
                    if (files.Count - files.IndexOf(file) <= LogRetentionMinFiles)
                    {
                        break;
                    }

                    bool tooOld = file.LastWriteTimeUtc < cutoffUtc;
                    bool overQuota = totalBytes > LogRetentionTotalBytes;

                    if (!tooOld && !overQuota)
                    {
                        break;
                    }

                    long length = file.Length;

                    try
                    {
                        file.Delete();
                        deleted++;
                        freedBytes += length;
                        totalBytes -= length;

                        /*
                         * 孪生文件跟着人读日志一起收：否则 `.jsonl` / `.trace.jsonl` 只增不减，
                         * 而用户看到的是“日志已经清过了”。当前这一份三件套永远不在这里的 files 里。
                         */
                        freedBytes += TryDeleteLogTwin(file.FullName, ".user.log");
                        freedBytes += TryDeleteLogTwin(file.FullName, ".jsonl");
                        freedBytes += TryDeleteLogTwin(file.FullName, ".trace.jsonl");
                    }
                    catch
                    {
                        // 正被别的进程占用（例如上一份还开着）：跳过，下次启动再收。
                    }
                }

                if (deleted > 0)
                {
                    AppendBothFiles(
                        DateTime.Now,
                        "INFO",
                        $"已清理 {deleted} 份老日志（保留最近 {LogRetentionDays} 天 / "
                        + $"上限 {LogRetentionTotalBytes / 1024 / 1024} MB / 至少 {LogRetentionMinFiles} 份），释放 "
                        + $"{freedBytes / 1024.0 / 1024.0:0.0} MB");
                }
            }
            catch
            {
                // 清理失败绝不能让程序起不来：日志只是辅助。
            }
        }

        /// <summary>孪生文件的长度（用于 100 MB 总量口径；没有就是 0）。</summary>
        private static long TwinLength(string logPath, string extension)
        {
            try
            {
                string twin = Path.ChangeExtension(logPath, extension);
                return File.Exists(twin) ? new FileInfo(twin).Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 删掉人读日志的孪生文件（`.user.log` / `.jsonl` / `.trace.jsonl`），返回释放字节数。
        /// 只由保留策略调用；当前这一份永远不会走到这里。
        /// </summary>
        private static long TryDeleteLogTwin(string logPath, string extension)
        {
            try
            {
                string twin = Path.ChangeExtension(logPath, extension);

                if (!File.Exists(twin))
                {
                    return 0;
                }

                long length = new FileInfo(twin).Length;
                File.Delete(twin);
                return length;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>日志保留天数（第 44 条追加；固定策略，界面上没有开关 —— 要改告诉我）。</summary>
        internal const int LogRetentionDays = 30;

        /// <summary>日志目录总量上限（超过就从最旧的开始删）。</summary>
        internal const long LogRetentionTotalBytes = 100L * 1024 * 1024;

        /// <summary>无论如何至少留最近这么多份（用户随时可能要看"昨天那一次"）。</summary>
        internal const int LogRetentionMinFiles = 10;

        /// <summary>
        /// 写入 INFO 日志。
        /// </summary>
        public void WriteInfo(string message)
        {
            Write("INFO", message);
        }

        /// <summary>
        /// 写入 WARN 日志。
        /// </summary>
        public void WriteWarning(string message)
        {
            Write("WARN", message);
        }

        /// <summary>
        /// 写入 ERROR 日志。
        /// </summary>
        public void WriteError(string message)
        {
            Write("ERROR", message);
        }

        /// <summary>
        /// 写入任务日志。
        /// </summary>
        public void WriteTaskLog(ArchiveTask task, string message)
        {
            if (task == null)
            {
                WriteInfo(message);
                return;
            }

            string fileName = string.IsNullOrWhiteSpace(task.FileName)
                ? Path.GetFileName(task.CurrentPath)
                : task.FileName;

            string text = $"任务[{fileName}] {message}";
            Write("INFO", text, task.CurrentPath);
        }

        /// <summary>
        /// 写入任务警告日志。
        /// </summary>
        public void WriteTaskWarning(ArchiveTask task, string message)
        {
            if (task == null)
            {
                WriteWarning(message);
                return;
            }

            string fileName = string.IsNullOrWhiteSpace(task.FileName)
                ? Path.GetFileName(task.CurrentPath)
                : task.FileName;

            string text = $"任务[{fileName}] {message}";
            Write("WARN", text, task.CurrentPath);
        }

        /// <summary>
        /// 写入任务错误日志。
        /// </summary>
        public void WriteTaskError(ArchiveTask task, string message)
        {
            if (task == null)
            {
                WriteError(message);
                return;
            }

            string fileName = string.IsNullOrWhiteSpace(task.FileName)
                ? Path.GetFileName(task.CurrentPath)
                : task.FileName;

            string text = $"任务[{fileName}] {message}";
            Write("ERROR", text, task.CurrentPath);
        }

        /// <summary>
        /// 详细日志：**只写内部 `.log` / `.jsonl`，不进屏幕、也不进普通 `.user.log`**。
        /// 供任务级细节在用户选择普通档时仍然留下排查证据。
        /// </summary>
        public void WriteFileOnly(string level, string message)
        {
            level = string.IsNullOrWhiteSpace(level) ? "INFO" : level.ToUpperInvariant();
            message = Sanitize(message);

            var item = new OperationLogItem(level, message);

            lock (_lockObj)
            {
                WriteFileLog(item, includeUserLog: false);
            }
        }

        /// <summary>
        /// 写入日志。
        /// </summary>
        public void Write(string level, string message, string taskPath = "")
        {
            WriteInternal(level, message, taskPath, criterion: null);
        }

        /// <summary>
        /// **写一条结构化诊断事件**：人读一行（事件名 + 摘要），机器读那一份带
        /// <c>event/source/target/action/result/detail/stack/engine/exitCode/bytes/durationMs/group/identity</c>。
        /// 这是“只靠日志排查”的入口；⛔ 不改任何业务判据。
        /// </summary>
        public void Event(
            string eventName,
            string message,
            string detail = "",
            string level = "INFO",
            string file = "",
            string source = "",
            string target = "",
            string action = "",
            string result = "",
            string stack = "",
            string engine = "",
            int? exitCode = null,
            long? bytes = null,
            long? durationMs = null,
            string group = "",
            string identity = "")
        {
            string human = $"事件「{eventName}」：{message}"
                + (source.Length > 0 || target.Length > 0
                    ? $"（{source} → {target}）"
                    : string.Empty)
                + (result.Length > 0 ? $" ⇒ {result}" : string.Empty);

            level = string.IsNullOrWhiteSpace(level) ? "INFO" : level.ToUpperInvariant();
            var item = new OperationLogItem(level, Sanitize(human), file);

            /*
             * 诊断事件只进 `.trace.jsonl`：成功任务的屏幕/人读日志仍保持“一行”纪律，
             * 而详细事实（批次清单/任务卡/文件操作/引擎原话）在独立文件里，行数与屏幕无关。
             */
            lock (_lockObj)
            {
                if (!_enableFileLog || string.IsNullOrWhiteSpace(_currentTraceFilePath))
                {
                    return;
                }

                try
                {
                    File.AppendAllText(
                        _currentTraceFilePath,
                        BuildJsonRecord(
                            item,
                            RunContext,
                            null,
                            new LogEventFields(
                                Event: eventName,
                                Source: Sanitize(source),
                                Target: Sanitize(target),
                                Action: Sanitize(action),
                                Result: Sanitize(result),
                                Detail: Sanitize(detail),
                                Stack: Sanitize(stack),
                                Engine: Sanitize(engine),
                                ExitCode: exitCode,
                                Bytes: bytes,
                                DurationMs: durationMs,
                                Group: Sanitize(group),
                                Identity: Sanitize(identity)),
                            ++_traceSequence,
                            _runId,
                            Environment.ProcessId,
                            Environment.CurrentManagedThreadId)
                        + Environment.NewLine,
                        Encoding.UTF8);
                }
                catch
                {
                    // 诊断日志写失败不影响主日志与业务。
                }
            }
        }

        /// <summary>写一条异常事件（异常类型/消息进人读行，完整 ToString 进机器读的 stack）。</summary>
        public void Exception(string context, Exception exception, string taskPath = "")
        {
            if (exception == null)
            {
                return;
            }

            string detail = JsonSerializer.Serialize(new
            {
                type = exception.GetType().FullName,
                message = exception.Message,
                inner = exception.InnerException?.GetType().FullName,
                inner_message = exception.InnerException?.Message,
                source = exception.Source,
                hresult = exception.HResult
            });

            Event(
                "exception",
                $"{context}：{exception.GetType().Name}：{exception.Message}",
                detail: detail,
                level: "ERROR",
                file: taskPath,
                stack: exception.ToString());
        }

        /// <summary>
        /// **写一处判据**（大测试的"只看日志排查"主要靠它）：人读一行 + 机器读一条（字段固定）。
        ///
        /// <para>人读那一行的形状：<c>判据「&lt;名字&gt;」｜输入：&lt;判据输入&gt; ⇒ 结论：&lt;结论&gt;（动作：&lt;动作&gt;）</c>；
        /// 机器读那条把 <paramref name="criterion"/> / <paramref name="inputs"/> / <paramref name="verdict"/> /
        /// <paramref name="action"/> 各自放进**独立字段**（⛔ 不埋在散文里，否则没法逐条筛）。</para>
        ///
        /// <para>⛔ 只是**记录**：它不改任何判据、不写盘上的东西。</para>
        /// </summary>
        public void Criterion(
            string criterion,
            string inputs,
            string verdict,
            string action = "",
            string file = "",
            string identity = "",
            string level = "INFO")
        {
            string human = $"判据「{criterion}」｜输入：{inputs} ⇒ 结论：{verdict}"
                + (action.Length > 0 ? $"（动作：{action}）" : string.Empty);

            WriteInternal(
                level,
                human,
                file,
                new CriterionFields(criterion, inputs, verdict, action, file, identity));
        }

        /// <summary><see cref="Write"/> 与 <see cref="Criterion"/> 的**唯一**收口（屏幕 + 人读文件 + 机器读文件）。</summary>
        private void WriteInternal(
            string level,
            string message,
            string taskPath,
            CriterionFields? criterion,
            LogEventFields? evidence = null)
        {
            level = string.IsNullOrWhiteSpace(level) ? "INFO" : level.ToUpperInvariant();
            message = Sanitize(message);

            var item = new OperationLogItem(level, message, taskPath);

            lock (_lockObj)
            {
                AddScreenLog(item);
                WriteFileLog(item, criterion, evidence);
            }

            LogAdded?.Invoke(this, item);
        }

        /// <summary>
        /// 添加屏幕日志。
        /// </summary>
        private void AddScreenLog(OperationLogItem item)
        {
            try
            {
                Logs.Add(item);

                // 防止屏幕日志无限增长。
                while (Logs.Count > 3000)
                {
                    Logs.RemoveAt(0);
                }
            }
            catch
            {
                // 如果跨线程直接访问 ObservableCollection 出错，
                // ViewModel 可选择订阅 LogAdded 后在 UI 线程添加。
            }
        }

        /// <summary>
        /// 写入文件日志。
        /// </summary>
        /// <summary>
        /// 日志文件里一行的完整文本：<c>[时间] [级别] 消息</c>。
        ///
        /// 形态与屏幕日志（<c>OperationLogItem.DisplayText</c>）**一模一样**，差别只在时间戳走
        /// <see cref="FormatTimestamp"/>（固定文化）。为什么不直接用 <c>item.DisplayText</c>：
        /// 它内部是 <c>Time.ToString("yyyy-MM-dd HH:mm:ss")</c>，用的是**当前区域的日历** ——
        /// 在泰历这类区域里写进文件的就是 2569 年，而日志文件是要被解析、被比对的那一份。
        /// 屏幕那一行由视图直接绑定，改不到（在 Models 里），所以文件这一份先钉死。
        /// </summary>
        public static string FormatLogLine(DateTime time, string? level, string? message)
        {
            string normalizedLevel = string.IsNullOrWhiteSpace(level) ? "INFO" : level;

            return $"[{FormatTimestamp(time)}] [{normalizedLevel}] {message}";
        }

        /// <summary>
        /// **两份一起追加一行**（人读 + 机器读）—— 只给"不走屏幕日志"的那几处内部写入用
        /// （日志保留策略那一行）。⛔ 别的地方一律走 <see cref="WriteInternal"/>，那才是唯一收口。
        /// </summary>
        private void AppendBothFiles(DateTime time, string level, string message)
        {
            if (!_enableFileLog || string.IsNullOrWhiteSpace(_currentLogFilePath))
            {
                return;
            }

            try
            {
                string safe = Sanitize(message);

                File.AppendAllText(
                    _currentLogFilePath,
                    FormatLogLine(time, level, safe) + Environment.NewLine,
                    Encoding.UTF8);

                if (_currentJsonlFilePath.Length > 0)
                {
                    File.AppendAllText(
                        _currentJsonlFilePath,
                        BuildJsonRecord(new OperationLogItem(level, safe, string.Empty), RunContext, null)
                        + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch
            {
                _enableFileLog = false;
            }
        }

        private void WriteFileLog(
            OperationLogItem item,
            CriterionFields? criterion = null,
            LogEventFields? evidence = null,
            bool includeUserLog = true)
        {
            if (!_enableFileLog)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_currentLogFilePath))
            {
                return;
            }

            try
            {
                File.AppendAllText(
                    _currentLogFilePath,
                    FormatLogLine(item.Time, item.Level, item.Message) + Environment.NewLine,
                    Encoding.UTF8);

                if (includeUserLog && _currentUserLogFilePath.Length > 0)
                {
                    File.AppendAllText(
                        _currentUserLogFilePath,
                        FormatLogLine(item.Time, item.Level, item.Message) + Environment.NewLine,
                        Encoding.UTF8);
                }

                /*
                 * **人读行与机器读行一比一**：同一处写入 ⇒ 行数永远相等（守门用例 `LogSchemaTests` 钉着）。
                 * 机器读那一份是 `.jsonl`（一行一个 JSON 对象），字段固定、⛔ 不可省。
                 */
                if (_currentJsonlFilePath.Length > 0)
                {
                    File.AppendAllText(
                        _currentJsonlFilePath,
                        BuildJsonRecord(
                            item,
                            RunContext,
                            criterion,
                            evidence,
                            ++_sequence,
                            _runId,
                            Environment.ProcessId,
                            Environment.CurrentManagedThreadId)
                        + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch
            {
                _enableFileLog = false;
            }
        }

        /// <summary>一处判据的四个结构化字段（唯一出口 <see cref="Criterion"/>）。</summary>
        public sealed record CriterionFields(
            string Criterion,
            string Inputs,
            string Verdict,
            string Action,
            string File,
            string Identity);

        /// <summary>
        /// 一条结构化诊断事件的字段（唯一写入口 <see cref="Event"/>）；⛔ 不参与任何业务判据。
        /// </summary>
        public sealed record LogEventFields(
            string Event = "",
            string Source = "",
            string Target = "",
            string Action = "",
            string Result = "",
            string Detail = "",
            string Stack = "",
            string Engine = "",
            int? ExitCode = null,
            long? Bytes = null,
            long? DurationMs = null,
            string Group = "",
            string Identity = "");

        /// <summary>这一行的来源上下文（批次 / 轮次 / 样本 / 阶段 / 任务 / 层号）。</summary>
        public sealed record LogRunContext
        {
            public string Batch { get; init; } = string.Empty;

            /// <summary>第几轮（0 = 还没进轮次循环）。</summary>
            public int Round { get; init; }

            /// <summary>大测试的第几份原包（`1111` / `2222` …）。</summary>
            public string Sample { get; init; } = string.Empty;

            /// <summary>阶段：第一大步 / 第二大步 / 接片 / 收卷 / 落点 / 删除 / 引擎调用。</summary>
            public string Stage { get; init; } = string.Empty;

            public string Task { get; init; } = string.Empty;

            /// <summary>层号（-1 = 不适用）。</summary>
            public int Layer { get; init; } = -1;
        }

        /// <summary>
        /// 把一行日志拼成**机器读**的 JSON 对象（⛔ 零依赖：自己转义，不引第三方）。
        /// 字段固定：<c>ts/level/batch/round/sample/stage/task/layer/file/identity/criterion/inputs/verdict/action/msg</c>。
        /// </summary>
        internal static string BuildJsonRecord(
            OperationLogItem item,
            LogRunContext context,
            CriterionFields? criterion,
            LogEventFields? evidence = null,
            long sequence = 0,
            string runId = "",
            int processId = 0,
            int threadId = 0)
        {
            var builder = new StringBuilder(256);

            builder.Append('{');
            AppendJsonField(builder, "schema", "archivefixer.log.v2", first: true);
            AppendJsonField(builder, "ts", FormatTimestamp(item.Time));
            AppendJsonField(builder, "level", item.Level);
            AppendJsonNumber(builder, "seq", sequence);
            AppendJsonField(builder, "run", runId);
            AppendJsonNumber(builder, "pid", processId);
            AppendJsonNumber(builder, "thread", threadId);
            AppendJsonField(builder, "batch", context.Batch);
            AppendJsonNumber(builder, "round", context.Round);
            AppendJsonField(builder, "sample", context.Sample);
            AppendJsonField(builder, "stage", context.Stage);
            AppendJsonField(builder, "task", context.Task);
            AppendJsonNumber(builder, "layer", context.Layer);
            AppendJsonField(builder, "file", criterion?.File ?? item.TaskPath);
            AppendJsonField(builder, "identity", criterion?.Identity ?? string.Empty);
            AppendJsonField(builder, "criterion", criterion?.Criterion ?? string.Empty);
            AppendJsonField(builder, "inputs", criterion?.Inputs ?? string.Empty);
            AppendJsonField(builder, "verdict", criterion?.Verdict ?? string.Empty);
            AppendJsonField(builder, "action", criterion?.Action ?? string.Empty);
            AppendJsonField(builder, "msg", item.Message);
            AppendJsonField(builder, "event", evidence?.Event ?? string.Empty);
            AppendJsonField(builder, "source", evidence?.Source ?? string.Empty);
            AppendJsonField(builder, "target", evidence?.Target ?? string.Empty);
            AppendJsonField(builder, "op_action", evidence?.Action ?? string.Empty);
            AppendJsonField(builder, "result", evidence?.Result ?? string.Empty);
            AppendJsonField(builder, "detail", evidence?.Detail ?? string.Empty);
            AppendJsonField(builder, "stack", evidence?.Stack ?? string.Empty);
            AppendJsonField(builder, "engine", evidence?.Engine ?? string.Empty);
            AppendJsonNumber(builder, "exitCode", evidence?.ExitCode);
            AppendJsonNumber(builder, "bytes", evidence?.Bytes);
            AppendJsonNumber(builder, "durationMs", evidence?.DurationMs);
            AppendJsonField(builder, "group", evidence?.Group ?? string.Empty);
            AppendJsonField(builder, "op_identity", evidence?.Identity ?? string.Empty);
            builder.Append('}');

            return builder.ToString();
        }

        private static void AppendJsonField(StringBuilder builder, string name, string? value, bool first = false)
        {
            if (!first)
            {
                builder.Append(',');
            }

            builder.Append('"').Append(name).Append("\":\"").Append(EscapeJson(value)).Append('"');
        }

        private static void AppendJsonNumber(StringBuilder builder, string name, int value)
        {
            builder.Append(",\"").Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendJsonNumber(StringBuilder builder, string name, long value)
        {
            builder.Append(",\"").Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendJsonNumber(StringBuilder builder, string name, int? value)
        {
            if (value.HasValue)
            {
                AppendJsonNumber(builder, name, (long)value.Value);
            }
        }

        private static void AppendJsonNumber(StringBuilder builder, string name, long? value)
        {
            if (value.HasValue)
            {
                AppendJsonNumber(builder, name, value.Value);
            }
        }

        /// <summary>JSON 字符串转义（零依赖；控制字符走 <c>\uXXXX</c>，⛔ 换行绝不许漏进记录里）。</summary>
        internal static string EscapeJson(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length + 16);

            foreach (char ch in value)
            {
                switch (ch)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (ch < 0x20)
                        {
                            builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(ch);
                        }

                        break;
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// 日志脱敏。
        /// 不允许记录明文密码。
        /// </summary>
        public string Sanitize(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            string result = message;

            result = PasswordMasker.Sanitize(result);

            // -p明文密码
            result = Regex.Replace(
                result,
                @"(?i)(^|\s)-p[^\s]+",
                m =>
                {
                    string prefix = m.Value.StartsWith(" ") ? " " : string.Empty;
                    return prefix + "-p******";
                });

            // --password=明文
            result = Regex.Replace(
                result,
                @"(?i)(--password\s*=\s*)([^\s;]+)",
                "$1******");

            // password=明文
            result = Regex.Replace(
                result,
                @"(?i)(password\s*=\s*)([^\s;]+)",
                "$1******");

            // Password: 明文
            result = Regex.Replace(
                result,
                @"(?i)(password\s*:\s*)([^\r\n]+)",
                "$1******");

            // 使用密码 明文
            result = Regex.Replace(
                result,
                @"使用密码\s*[^\r\n]+",
                "使用密码 ******");

            /*
             * 「尝试密码 …」那一条**只在 PasswordMasker 里有一份**（上面第一行就调过它了）——
             * 这里过去又抄了一遍 `尝试密码\s*[^\r\n]+`，于是 2026-10-04 真机那一行
             * 「尝试密码列表第 3 项：******」被这一份二次擦成「尝试密码 ******」，
             * 第几项 / 什么来源全没了。⛔ 同一个脱敏规则只许有一个出口。
             */

            // 统一密码：明文
            result = Regex.Replace(
                result,
                @"统一密码\s*[:：]\s*[^\r\n]+",
                "统一密码：******");

            // 单任务密码：明文
            result = Regex.Replace(
                result,
                @"单任务密码\s*[:：]\s*[^\r\n]+",
                "单任务密码：******");

            // 密码：明文
            result = Regex.Replace(
                result,
                @"密码\s*[:：]\s*[^\r\n]+",
                "密码：******");

            return result;
        }

        /// <summary>
        /// **导出全部日志**（用户 2026-09-25 第 37 条：不是只导这一次的，是**所有**日志）。
        ///
        /// <para>把日志目录里所有 <c>ArchiveFixer_*.log</c> 按文件名排序（= 时间顺序，最旧在前）
        /// 拼成一个文件，每份前面写一行分隔头（文件名 + 大小 + 行数），末尾写一行合计。
        /// 内容一律过 <see cref="PasswordMasker"/>（与"导出日志 / 导出失败清单"同一口径）。</para>
        ///
        /// <para>读不出来的那一份**跳过但记一行**（被别的程序占用 / 权限）：绝不因为一份坏了就什么都不导。</para>
        /// </summary>
        /// <returns>导出的日志份数与行数（写成 <c>(0, 0)</c> 表示日志目录里一份都没有）。</returns>
        public (int FileCount, int LineCount) ExportAllLogs(string targetPath, bool includeDetails = true)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return (0, 0);
            }

            string? directory = Path.GetDirectoryName(targetPath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var builder = new StringBuilder();

            builder.AppendLine("ArchiveFixer 日志 —— 全部日志（本程序写过的每一份都在这里）");
            builder.AppendLine($"导出时间：{FormatTimestamp(DateTime.Now)}");
            builder.AppendLine($"日志目录：{LogDirectory}");
            builder.AppendLine();

            int fileCount = 0;
            int lineCount = 0;
            var skipped = new List<string>();

            foreach (string file in EnumerateLogFilesOldestFirst())
            {
                string selectedFile = includeDetails
                    ? file
                    : Path.ChangeExtension(file, ".user.log");

                if (!File.Exists(selectedFile))
                {
                    selectedFile = file;
                }

                string fileName = Path.GetFileName(selectedFile);

                string[] lines;

                try
                {
                    lines = File.ReadAllLines(selectedFile);
                }
                catch (Exception ex)
                {
                    skipped.Add($"{fileName}（{PasswordMasker.Sanitize(ex.Message)}）");
                    continue;
                }

                builder.AppendLine("================================================================================");
                builder.AppendLine($"# {fileName}（{lines.Length} 行）");
                builder.AppendLine("================================================================================");

                foreach (string line in lines)
                {
                    builder.AppendLine(line);
                }

                if (includeDetails)
                {
                    string tracePath = Path.ChangeExtension(file, ".trace.jsonl");

                    if (File.Exists(tracePath))
                    {
                        try
                        {
                            string[] traceLines = File.ReadAllLines(tracePath);

                            if (traceLines.Length > 0)
                            {
                                builder.AppendLine();
                                builder.AppendLine($"# {Path.GetFileName(tracePath)}（诊断事件 {traceLines.Length} 行）");

                                foreach (string traceLine in traceLines)
                                {
                                    builder.AppendLine(traceLine);
                                }
                            }
                        }
                        catch
                        {
                            // 诊断事件读不出不影响人读日志的导出。
                        }
                    }
                }

                builder.AppendLine();

                fileCount++;
                lineCount += lines.Length;
            }

            if (skipped.Count > 0)
            {
                builder.AppendLine("--------------------------------------------------------------------------------");
                builder.AppendLine("以下日志这次没能读出来（被占用或没有权限）：" + string.Join("、", skipped));
            }

            builder.AppendLine("--------------------------------------------------------------------------------");
            builder.AppendLine($"合计：{fileCount} 份日志 / {lineCount} 行。");

            File.WriteAllText(targetPath, PasswordMasker.Sanitize(builder.ToString()), new UTF8Encoding(false));

            return (fileCount, lineCount);
        }

        /// <summary>日志目录里的日志文件，按文件名排序（= 时间顺序，最旧在前）。</summary>
        private IEnumerable<string> EnumerateLogFilesOldestFirst()
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                {
                    return Array.Empty<string>();
                }

                return Directory.EnumerateFiles(LogDirectory, "ArchiveFixer_*.log", SearchOption.TopDirectoryOnly)
                    .Where(file => !file.EndsWith(".user.log", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// 导出当前日志。
        /// </summary>
        public bool ExportLog(string targetPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    return false;
                }

                string? directory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (!string.IsNullOrWhiteSpace(_currentLogFilePath) &&
                    File.Exists(_currentLogFilePath))
                {
                    File.Copy(_currentLogFilePath, targetPath, overwrite: true);
                    return true;
                }

                var builder = new StringBuilder();

                foreach (OperationLogItem item in Logs)
                {
                    builder.AppendLine(item.DisplayText);
                }

                File.WriteAllText(targetPath, builder.ToString(), Encoding.UTF8);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 打开日志目录。
        /// </summary>
        public bool OpenLogDirectory()
        {
            try
            {
                _pathService.EnsureDirectoryExists(LogDirectory);
                return _pathService.OpenDirectory(LogDirectory);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 清空屏幕日志。
        /// 不删除文件日志。
        /// </summary>
        public void ClearScreenLogs()
        {
            try
            {
                Logs.Clear();
            }
            catch
            {
                // 忽略 UI 线程问题。
            }
        }

        /// <summary>
        /// 获取屏幕日志文本。
        /// </summary>
        public string GetScreenLogText()
        {
            var builder = new StringBuilder();

            foreach (OperationLogItem item in Logs)
            {
                builder.AppendLine(item.DisplayText);
            }

            return builder.ToString();
        }

        /// <summary>
        /// 记录软件启动日志。
        /// </summary>
        public void WriteStartupInfo()
        {
            WriteInfo("软件启动");
            WriteInfo($"程序目录：{AppContext.BaseDirectory}");
            WriteInfo($"日志目录：{LogDirectory}");
        }

        /// <summary>
        /// 记录 7-Zip 检查结果。
        /// </summary>
        public void WriteSevenZipCheck(PathService pathService)
        {
            pathService ??= _pathService;

            if (pathService.SevenZipExeExists())
            {
                WriteInfo($"检测到 7-Zip：{pathService.SevenZipExePath}");
            }
            else
            {
                WriteWarning($"未找到 7-Zip：{pathService.SevenZipExePath}");
            }

            if (pathService.SevenZipDllExists())
            {
                WriteInfo($"检测到 7z.dll：{pathService.SevenZipDllPath}");
            }
            else
            {
                WriteWarning($"未找到 7z.dll：{pathService.SevenZipDllPath}");
            }
        }
    }
}
