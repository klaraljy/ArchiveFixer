using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ArchiveFixer.Services;

/// <summary>
/// **导出**：三种用户要的东西——"本次操作的日志"（人读 txt）、"全部日志"、"当前这一份"——
/// 以及它们共用的**本次操作起点标记**。
///
/// <para>为什么单独一个类：导出的判据不是"写日志"，而是"从哪儿开始算这一次操作"。
/// 那个起点以前和文件写入、屏幕集合住在同一个类型里，导出还要顺手读 trace 拼关键事件区；
/// 抽出来后，导出改文案/改区块只影响这一处，写日志那条路看不到导出逻辑。</para>
///
/// <para>⛔ 口径一个字都不许变：普通档与详细档各按自己那份文件的起点行、正文去重、诊断专用行
/// 只在详细档、关键事件最多 200 条、写出前一律过 <see cref="PasswordMasker"/>、
/// 文件用 <c>UTF8Encoding(false)</c>（不写 BOM）。这些是用户已经拿到的 txt 的形状。</para>
/// </summary>
internal sealed class LogExportWriter
{
    private readonly PathService _pathService;
    private readonly LogOperationContext _context;
    private readonly LogScreenBuffer _screen;

    /// <summary>本次操作开始处的**屏幕日志下标**（-1 = 还没有标记）。</summary>
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

    internal LogExportWriter(
        PathService pathService,
        LogOperationContext context,
        LogScreenBuffer screen)
    {
        _pathService = pathService;
        _context = context;
        _screen = screen;
    }

    /// <summary>
    /// 记下"一次新的操作开始了"：屏幕下标、两份文件各自的起始行号、开始时间。
    ///
    /// <para>⛔ 调用点必须与拆分前完全同序：先建操作文件夹、再写分隔行、最后才取位置
    /// （屏幕下标取的是**分隔行自己**，不是操作里的第一行）。</para>
    /// </summary>
    internal void CaptureOperationStart(int screenCount)
    {
        _operationStartIndex = Math.Max(0, screenCount - 1);
        _operationStartFile = _context.MainLogPath;
        _operationStartFileLine = TryCountLines(_context.MainLogPath) - 1;
        _operationStartUserFile = _context.UserLogPath;
        _operationStartUserFileLine = TryCountLines(_context.UserLogPath) - 1;
        _operationStartTimestamp = LogService.FormatTimestamp(ArchiveFixer.Helpers.Clock.Now);
    }

    /// <summary>
    /// **只导出"本次操作"的日志**（最近一次标记之后的部分）。
    ///
    /// <para>优先按日志文件导出（与用户手工打开看到的一致）；没有文件（关掉了文件日志）就退回屏幕日志。
    /// 没有任何标记时导出**当前这一份日志文件**（= 本次运行）—— 那已经是他要的范围，绝不拼历史文件。</para>
    /// </summary>
    /// <returns>导出的行数，以及是不是"本次操作"的范围（false = 退回了整份当前日志）。</returns>
    internal (int Lines, bool FromOperationMarker) ExportOperationLog(
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
            ? _context.MainLogPath
            : _context.UserLogPath;
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

            for (int i = start; i < _screen.Items.Count; i++)
            {
                body.Add(_screen.Items[i].DisplayText);
            }
        }

        builder.AppendLine("================ 以下是日志正文（已去重；任务行快照已移到上面的任务明细） ================");

        foreach (string line in DeduplicateReportBody(body))
        {
            /*
             * ⛔ 2026-11-01：普通档（includeDetails=false）不把**排障专用**内容给用户：
             * UI 线程停顿/性能探针/内部排障标记只在完整 `.log` 里。
             * 详细档（用户主动勾选）原样保留。
             */
            if (!includeDetails && IsDiagnosticOnlyLine(line))
            {
                continue;
            }

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
    /// 普通档导出要排除的**排障专用**行（只该留在完整 `.log` 里给排查用）。
    /// </summary>
    private static bool IsDiagnosticOnlyLine(string line)
    {
        return line.Contains("[性能]", StringComparison.Ordinal)
            || line.Contains("UI 线程停顿", StringComparison.Ordinal)
            || line.Contains("[排障", StringComparison.Ordinal)
            || line.Contains("[诊断", StringComparison.Ordinal);
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
        if (string.IsNullOrWhiteSpace(_context.TracePath) ||
            !File.Exists(_context.TracePath))
        {
            return;
        }

        string[] traceLines;

        try
        {
            traceLines = File.ReadAllLines(_context.TracePath);
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
    /// **导出全部日志**（用户 2026-09-25 第 37 条：不是只导这一次的，是**所有**日志）。
    ///
    /// <para>把日志目录里所有 <c>ArchiveFixer_*.log</c> 按文件名排序（= 时间顺序，最旧在前）
    /// 拼成一个文件，每份前面写一行分隔头（文件名 + 大小 + 行数），末尾写一行合计。
    /// 内容一律过 <see cref="PasswordMasker"/>（与"导出日志 / 导出失败清单"同一口径）。</para>
    ///
    /// <para>读不出来的那一份**跳过但记一行**（被别的程序占用 / 权限）：绝不因为一份坏了就什么都不导。</para>
    /// </summary>
    /// <returns>导出的日志份数与行数（写成 <c>(0, 0)</c> 表示日志目录里一份都没有）。</returns>
    internal (int FileCount, int LineCount) ExportAllLogs(string targetPath, bool includeDetails = true)
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
        builder.AppendLine($"导出时间：{LogService.FormatTimestamp(ArchiveFixer.Helpers.Clock.Now)}");
        builder.AppendLine($"日志目录：{_pathService.LogsDirectory}");
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
            if (!Directory.Exists(_pathService.LogsDirectory))
            {
                return Array.Empty<string>();
            }

            return Directory.EnumerateFiles(_pathService.LogsDirectory, "operation.log", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(_pathService.LogsDirectory, "ArchiveFixer_*.log", SearchOption.TopDirectoryOnly)
                    .Where(file => !file.EndsWith(".user.log", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(file => File.GetLastWriteTimeUtc(file))
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
    internal bool ExportLog(string targetPath)
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

            if (!string.IsNullOrWhiteSpace(_context.MainLogPath) &&
                File.Exists(_context.MainLogPath))
            {
                File.Copy(_context.MainLogPath, targetPath, overwrite: true);
                return true;
            }

            var builder = new StringBuilder();

            foreach (OperationLogItem item in _screen.Items)
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
}
