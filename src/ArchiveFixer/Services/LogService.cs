using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace ArchiveFixer.Services;

/// <summary>
/// 日志服务的**门面**：公开 API 一个都没变，调用方（界面、协调器、导出、诊断包）不知道里面分了几层。
///
/// <para>职责分工（本类只剩公开 API、两个静态格式常量、以及"谁来装配谁"）：</para>
/// <list type="bullet">
/// <item><description><see cref="LogOperationContext"/>：本次运行/本次操作的**可变状态**（运行 ID、
/// 目录与操作身份、四份文件路径、两个序号、文件日志开关、各行的来源上下文）与**唯一那把锁**；</description></item>
/// <item><description><see cref="LogFileWriter"/>：**写入核心** —— 四份文件的唯一物理追加口
/// （人读行与机器读行一比一就靠它同一处写入）；</description></item>
/// <item><description><see cref="LogScreenBuffer"/>：屏幕日志集合（封顶 3000 行、清空、整段取文本）；</description></item>
/// <item><description><see cref="LogDiagnosticWriter"/>：**诊断** —— 错误码/组件/判据/结构化事件/异常，
/// 以及"屏幕 + 人读 + 机器读"的唯一收口；</description></item>
/// <item><description><see cref="LogJsonRecord"/>：**JSONL** —— 机器读那两份的字段顺序与转义；</description></item>
/// <item><description><see cref="LogExportWriter"/>：**导出** —— 本次操作/全部/当前这一份，以及操作起点标记；</description></item>
/// <item><description><see cref="LogLifecycle"/>：**生命周期** —— 运行目录、操作目录、manifest、环境头、保留策略。</description></item>
/// </list>
///
/// <para>⛔ 拆分不改任何**可见行为**：日志格式、文件与目录名、级别、脱敏口径、以及线程安全语义
/// （四份文件 + 屏幕集合 + 两个序号仍在 <see cref="LogOperationContext.SyncRoot"/> **同一把锁**下串行）
/// 全部与拆分前一致；守门用例见 <c>LogSchemaTests</c> / <c>LogDetailSplitTests</c> /
/// <c>LogV3Tests</c> / <c>LogVolumePolicyTests</c> / <c>LogTimestampFormatTests</c>。</para>
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
    public const string TimestampFormat = LogTimestampFormat.Timestamp;

    /// <summary>日志文件名里的紧凑时间戳格式（同样固定文化，避免区域日历换出别的年份）。</summary>
    public const string FileNameTimestampFormat = "yyyyMMdd_HHmmss";

    /// <summary>
    /// 把时间按 <see cref="TimestampFormat"/> 格式化。全仓的日志/报告时间戳都走这里，
    /// **不要**再写 <c>DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")</c>（那样会各自跟随区域日历）。
    /// </summary>
    public static string FormatTimestamp(DateTime value)
    {
        return LogTimestampFormat.Format(value);
    }

    /// <summary>把时间按 <see cref="FileNameTimestampFormat"/> 格式化（日志文件名用）。</summary>
    public static string FormatFileTimestamp(DateTime value)
    {
        return value.ToString(FileNameTimestampFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 日志文件里一行的完整文本：<c>[时间] [级别] 消息</c>（唯一的行格式；
    /// 形态与屏幕日志 <c>OperationLogItem.DisplayText</c> 一致，时间戳固定文化）。
    /// </summary>
    public static string FormatLogLine(DateTime time, string? level, string? message)
    {
        return LogFileWriter.FormatLogLine(time, level, message);
    }

    /// <summary>
    /// 把一行日志拼成**机器读**的 JSON 对象（字段顺序与转义由 <see cref="LogJsonRecord"/> 负责）。
    /// 保留这个转发只为既有调用方/守门用例的口径不变。
    /// </summary>
    internal static string BuildJsonRecord(
        OperationLogItem item,
        LogRunContext context,
        CriterionFields? criterion,
        LogEventFields? evidence = null,
        long sequence = 0,
        string runId = "",
        int processId = 0,
        int threadId = 0,
        string operationId = "")
    {
        return LogJsonRecord.Build(item, context, criterion, evidence, sequence, runId, processId, threadId, operationId);
    }

    /// <summary>JSON 字符串转义（零依赖；控制字符走 <c>\uXXXX</c>，⛔ 换行绝不许漏进记录里）。</summary>
    internal static string EscapeJson(string? value)
    {
        return LogJsonRecord.EscapeJson(value);
    }

    /// <summary>日志保留天数（第 44 条追加；固定策略，界面上没有开关 —— 要改告诉我）。</summary>
    internal const int LogRetentionDays = LogLifecycle.RetentionDays;

    /// <summary>日志目录总量上限（超过就从最旧的开始删）。</summary>
    internal const long LogRetentionTotalBytes = LogLifecycle.RetentionTotalBytes;

    /// <summary>无论如何至少留最近这么多份（用户随时可能要看"昨天那一次"）。</summary>
    internal const int LogRetentionMinFiles = LogLifecycle.RetentionMinFiles;

    private readonly PathService _pathService;
    private readonly LogOperationContext _context;
    private readonly LogScreenBuffer _screen;
    private readonly LogFileWriter _files;
    private readonly LogDiagnosticWriter _diagnostics;
    private readonly LogExportWriter _exports;
    private readonly LogLifecycle _lifecycle;

    /// <summary>本进程启动时间（环境头用）。</summary>
    private readonly DateTime _processStarted = Process.GetCurrentProcess().StartTime;

    /// <summary>当前进程的日志入口；全局未处理异常用它写入当前操作 trace。</summary>
    public static LogService? Current { get; private set; }

    public LogService()
        : this(new PathService())
    {
    }

    public LogService(PathService pathService)
    {
        _pathService = pathService;
        _context = new LogOperationContext();
        _screen = new LogScreenBuffer();
        _files = new LogFileWriter(_context);
        _diagnostics = new LogDiagnosticWriter(_context, _screen, _files);
        _diagnostics.Published = item => LogAdded?.Invoke(this, item);
        _exports = new LogExportWriter(pathService, _context, _screen);
        _lifecycle = new LogLifecycle(pathService, _context, _files, _diagnostics);
        Current = this;
    }

    /// <summary>
    /// 屏幕日志集合。
    /// MainViewModel 可以直接绑定该集合，或者订阅 LogAdded 事件后自己维护集合。
    /// </summary>
    public ObservableCollection<OperationLogItem> Logs => _screen.Items;

    /// <summary>
    /// 日志新增事件。
    /// </summary>
    public event EventHandler<OperationLogItem>? LogAdded;

    /// <summary>
    /// 当前日志文件路径。
    /// </summary>
    public string CurrentLogFilePath => _context.MainLogPath;

    /// <summary>本次运行 ID（整个进程一套）。</summary>
    public string CurrentRunId => _context.RunId;

    /// <summary>本次运行目录（logs 根下唯一文件夹）。</summary>
    public string CurrentRunDirectory => _context.RunDirectory;

    /// <summary>当前操作目录（每次操作一个子文件夹）。</summary>
    public string CurrentOperationDirectory => _context.OperationDirectory;

    public string CurrentOperationId => _context.OperationId;

    public string CurrentOperationName => _context.OperationName;

    /// <summary>
    /// 当前**机器读**的那一份（`.jsonl`：一行一个 JSON 对象）—— 与人读的 `.log` **一比一**（同一处写入）。
    ///
    /// <para>为什么要有它：用户出问题时**只会发日志**，而人读行里的判据输入是中文散文
    /// ⇒ 逐条筛/汇总/比对都没有稳定形状。JSONL 给人读行配一份**字段固定**的孪生记录
    /// （时间/级别/批次/轮次/样本/阶段/任务/层/文件/身份/判据名/判据输入/结论/动作/耗时），
    /// 于是"只看日志"也能把每一处判据的输入与结论摆出来。</para>
    /// </summary>
    public string CurrentJsonlFilePath => _context.JsonlPath;

    /// <summary>
    /// 当前**诊断事件**那一份（`.trace.jsonl`：schema v2，一批一条 JSON，不占人读行）。
    /// 它与 `.log`/`.jsonl` 分离：成功任务的屏幕日志仍只留少量行，详细的批次/任务/文件/引擎事实全在这里。
    /// </summary>
    public string CurrentTraceFilePath => _context.TracePath;

    /// <summary>
    /// 是否往文件里写日志（对应设置项「启用日志文件」，默认开）。
    ///
    /// 关掉只影响**写盘**：屏幕日志照旧（用户还得看见程序在干什么），
    /// 而且 <see cref="Initialize"/> 仍然把当前日志文件路径算好，重新打开就能接着写同一个文件。
    /// </summary>
    public bool EnableFileLog
    {
        get => _context.EnableFileLog;
        set => _context.EnableFileLog = value;
    }

    /// <summary>
    /// 日志目录。
    /// </summary>
    public string LogDirectory => _pathService.LogsDirectory;

    /// <summary>
    /// **这一行的来源上下文**（批次 / 轮次 / 样本 / 阶段 / 任务 / 层号）—— 大测试的"只看日志排查"靠它。
    ///
    /// <para>写点：调用方开工时 <see cref="UpdateRunContext"/>；读点：JSONL 拼装把它抄进每一行。
    /// ⛔ 只影响**日志记录**，⛔ 不参与任何判据、⛔ 不改任何行为。</para>
    /// </summary>
    public LogRunContext RunContext => _context.RunContext;

    /// <summary>换一次上下文（只覆盖**显式给了**的那几格；判据一个字都不受影响）。</summary>
    public void UpdateRunContext(
        string? batch = null,
        int? round = null,
        string? sample = null,
        string? stage = null,
        string? task = null,
        int? layer = null)
    {
        _context.UpdateRunContext(batch, round, sample, stage, task, layer);
    }

    /// <summary>
    /// 标记"一次新的操作开始了"（用户点了一次处理 / 一次扫描）。
    ///
    /// <para>开一个新的操作文件夹、写一行醒目的分隔线进日志，并记下当前位置 —— 这样**导出的日志**与
    /// 用户自己打开日志文件看到的分界完全一致（"这次操作从哪儿开始"一眼可见）。</para>
    ///
    /// <para>⛔ 三步的顺序不能换：先建文件夹（换掉四份文件的路径），再写分隔行（它自己也是这次操作的第一行），
    /// 最后才取位置 —— 屏幕下标与两份文件的行号取的都是**分隔行**那一处。</para>
    /// </summary>
    public void MarkOperationStart(string operationName)
    {
        _lifecycle.BeginOperationFolder(operationName);
        AppendOperationSeparator(operationName);
        _exports.CaptureOperationStart(_screen.Items.Count);
    }

    /// <summary>把"本次操作开始"写进日志（屏幕 + 文件；关掉文件日志时只进屏幕日志）。</summary>
    private void AppendOperationSeparator(string operationName)
    {
        string message = $"======== 本次操作开始：{operationName} ========";

        try
        {
            _diagnostics.Write("INFO", message);
        }
        catch
        {
            // 写日志失败绝不能影响操作本身（与既有 Write 的口径一致）。
        }
    }

    /// <summary>
    /// 初始化日志系统（目录、本次运行/操作的六件套、环境头、旧运行目录回收）。
    /// ⛔ 失败语义不变：初始化失败就关掉文件日志并清空当前路径，绝不让程序起不来。
    /// </summary>
    public void Initialize(bool enableFileLog = true)
    {
        _lifecycle.Initialize(enableFileLog);
    }

    /// <summary>
    /// **只导出"本次操作"的日志**（最近一次标记之后的部分；没有标记就导出当前这一份）。
    /// 详细档/普通档各按自己那份文件的起点行，`includeDetails=false` 还会剔掉排障专用行。
    /// </summary>
    /// <returns>导出的行数，以及是不是"本次操作"的范围（false = 退回了整份当前日志）。</returns>
    public (int Lines, bool FromOperationMarker) ExportOperationLog(
        string targetPath,
        IReadOnlyList<string>? headerLines = null,
        bool includeDetails = true,
        IReadOnlyList<string>? extraSections = null)
    {
        return _exports.ExportOperationLog(targetPath, headerLines, includeDetails, extraSections);
    }

    /// <summary>
    /// **导出全部日志**（用户 2026-09-25 第 37 条：不是只导这一次的，是**所有**日志）。
    /// 读不出来的那一份跳过但记一行，绝不因为一份坏了就什么都不导。
    /// </summary>
    /// <returns>导出的日志份数与行数（写成 <c>(0, 0)</c> 表示日志目录里一份都没有）。</returns>
    public (int FileCount, int LineCount) ExportAllLogs(string targetPath, bool includeDetails = true)
    {
        return _exports.ExportAllLogs(targetPath, includeDetails);
    }

    /// <summary>
    /// 导出当前日志（有当前文件就整份复制，没有就退回屏幕日志）。
    /// </summary>
    public bool ExportLog(string targetPath)
    {
        return _exports.ExportLog(targetPath);
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
        _screen.Clear();
    }

    /// <summary>
    /// 获取屏幕日志文本。
    /// </summary>
    public string GetScreenLogText()
    {
        return _screen.ToText();
    }

    /// <summary>
    /// 写入 INFO 日志。
    /// </summary>
    public void WriteInfo(string message)
    {
        _diagnostics.Write("INFO", message);
    }

    /// <summary>带稳定错误码/组件的日志行（失败、异常、判据否定必须走这里或 Exception）。</summary>
    public void WriteWithCode(
        string level,
        string message,
        string taskPath = "",
        string errorCode = DiagnosticCodes.AppUnknown,
        string component = "",
        string eventId = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string caller = "")
    {
        _diagnostics.WriteWithCode(
            level,
            message,
            taskPath,
            errorCode,
            component,
            eventId,
            sourceFile,
            sourceLine,
            caller);
    }

    /// <summary>
    /// 写入 WARN 日志。
    /// </summary>
    public void WriteWarning(
        string message,
        string errorCode = "",
        string component = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string caller = "")
    {
        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            WriteWithCode("WARN", message, errorCode: errorCode, component: component,
                sourceFile: sourceFile, sourceLine: sourceLine, caller: caller);
            return;
        }

        _diagnostics.Write("WARN", message, sourceFile: sourceFile, sourceLine: sourceLine, caller: caller);
    }

    /// <summary>
    /// 写入 ERROR 日志。
    /// </summary>
    public void WriteError(
        string message,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string caller = "")
    {
        WriteWithCode("ERROR", message, errorCode: DiagnosticCodes.AppUnknown, component: string.Empty,
            sourceFile: sourceFile, sourceLine: sourceLine, caller: caller);
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
    public void WriteTaskWarning(
        ArchiveTask task,
        string message,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string caller = "")
    {
        if (task == null)
        {
            WriteWarning(message, sourceFile: sourceFile, sourceLine: sourceLine, caller: caller);
            return;
        }

        string fileName = string.IsNullOrWhiteSpace(task.FileName)
            ? Path.GetFileName(task.CurrentPath)
            : task.FileName;

        string text = $"任务[{fileName}] {message}";
        Write("WARN", text, task.CurrentPath, sourceFile, sourceLine, caller);
    }

    /// <summary>
    /// 写入任务错误日志。
    /// </summary>
    public void WriteTaskError(
        ArchiveTask task,
        string message,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string caller = "")
    {
        if (task == null)
        {
            WriteError(message, sourceFile, sourceLine, caller);
            return;
        }

        string fileName = string.IsNullOrWhiteSpace(task.FileName)
            ? Path.GetFileName(task.CurrentPath)
            : task.FileName;

        string text = $"任务[{fileName}] {message}";
        Write("ERROR", text, task.CurrentPath, sourceFile, sourceLine, caller);
    }

    /// <summary>
    /// 详细日志：**只写内部 `.log` / `.jsonl`，不进屏幕、也不进普通 `.user.log`**。
    /// 供任务级细节在用户选择普通档时仍然留下排查证据。
    /// </summary>
    public void WriteFileOnly(string level, string message)
    {
        _files.WriteFileOnly(level, message);
    }

    /// <summary>
    /// 写入日志。
    /// </summary>
    public void Write(
        string level,
        string message,
        string taskPath = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string caller = "")
    {
        _diagnostics.Write(level, message, taskPath, sourceFile, sourceLine, caller);
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
        string identity = "",
        string errorCode = "",
        string component = "",
        string eventId = "",
        string spanId = "",
        string taskId = "",
        string stage = "",
        string sourceFile = "",
        int sourceLine = 0,
        string caller = "")
    {
        _diagnostics.Event(
            eventName: eventName,
            message: message,
            detail: detail,
            level: level,
            file: file,
            source: source,
            target: target,
            action: action,
            result: result,
            stack: stack,
            engine: engine,
            exitCode: exitCode,
            bytes: bytes,
            durationMs: durationMs,
            group: group,
            identity: identity,
            errorCode: errorCode,
            component: component,
            eventId: eventId,
            spanId: spanId,
            taskId: taskId,
            stage: stage,
            sourceFile: sourceFile,
            sourceLine: sourceLine,
            caller: caller);
    }

    /// <summary>写一条异常事件（异常类型/消息进人读行，完整 ToString 进机器读的 stack）。</summary>
    public void Exception(
        string context,
        Exception exception,
        string taskPath = "",
        string errorCode = DiagnosticCodes.Unhandled,
        string component = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string caller = "")
    {
        _diagnostics.Exception(
            context: context,
            exception: exception,
            taskPath: taskPath,
            errorCode: errorCode,
            component: component,
            sourceFile: sourceFile,
            sourceLine: sourceLine,
            caller: caller);
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
        string level = "INFO",
        string errorCode = "",
        string component = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string caller = "")
    {
        _diagnostics.Criterion(
            criterion: criterion,
            inputs: inputs,
            verdict: verdict,
            action: action,
            file: file,
            identity: identity,
            level: level,
            errorCode: errorCode,
            component: component,
            sourceFile: sourceFile,
            sourceLine: sourceLine,
            caller: caller);
    }

    /// <summary>
    /// 日志脱敏。
    /// 不允许记录明文密码。
    /// </summary>
    public string Sanitize(string message) => LogSanitizer.Sanitize(message);

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
