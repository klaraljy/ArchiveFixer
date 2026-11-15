using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 任务级日志捕获/摘要行/批清账（第 11 块）——实现已按职责搬到 <see cref="LogCaptureHandler"/>
/// （<c>ViewModels/Extraction/Steps/LogCaptureHandler.cs</c>），这里只留同签名薄转发、
/// 四个 <c>internal</c> 记账字段、以及两个**给测试/调用方直接读写的状态**。
///
/// <para>⛔ 搬走的是**实现**：判据、文案、日志级别与落盘顺序一格没改。调用点
/// （<c>ExtractionTaskRunner</c> / <c>OneClickCoordinator.SpecificExtractionBatch</c> /
/// <c>ExtractEntryHandler</c> / <c>RestPurgeStep</c> / <c>SourceHandler</c> / 各 partial）
/// 一个字都没改，转发签名（含 <c>CallerFilePath</c> 那一组可选参数）与原来逐字相同 ——
/// 所以外部调用点报进诊断日志的文件/行号/成员名与搬家前完全一致。</para>
///
/// <para>⚠ 只有 <c>CriterionLog</c> / <c>UpdateSummary</c> 两个**一行适配器**留在本类：
/// 它们的实现就是"转给 <c>MainViewModel</c> 那一个唯一收口"，搬走只会多一跳、一行判据都不会少，
/// 所以不搬（ADR 0003 的口径：不给没有实现可搬的方法留空转发）。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    /// <summary>
    /// 搬出去的那一份日志实现（懒建一次；宿主就是本类，全类共用同一个实例）。
    ///
    /// <para>⚠ 这里用锁而不是 <c>??=</c>：搬走的实现里有两个**实例状态** —— 任务级 <c>AsyncLocal</c>
    /// 捕获缓冲，和批记账的那把锁。任务线程本来就会并发调 <c>BeginTaskLogCapture</c> / <c>AppendLog</c>，
    /// 无锁懒建一旦造出第二个实例，"开始捕获"与"写日志"就落到两个不同的缓冲上
    /// （后果：普通档下成功任务的 INFO 细节会漏到屏幕上，而那是既有红线）。
    /// 锁只护第一次创建，之后就是一次空判。</para>
    /// </summary>
    private LogCaptureHandler? _logCaptureHandler;

    private readonly object _logCaptureHandlerGate = new();

    private LogCaptureHandler LogCapture
    {
        get
        {
            LogCaptureHandler? handler = _logCaptureHandler;

            if (handler != null)
            {
                return handler;
            }

            lock (_logCaptureHandlerGate)
            {
                return _logCaptureHandler ??= new LogCaptureHandler(this);
            }
        }
    }

    /// <summary>薄转发：实现见 <see cref="LogCaptureHandler.AppendTaskRowSnapshot"/>。</summary>
    private void AppendTaskRowSnapshot() => LogCapture.AppendTaskRowSnapshot();

    /// <summary>
    /// 写一处**判据**（人读一行 + 机器读一条，字段固定；唯一收口 <see cref="MainViewModel.CriterionLog"/>）。
    ///
    /// <para>⚠ 刻意**不走任务级捕获缓冲**（那条缓冲会把成功任务的 INFO 丢掉）：判据是大测试"只看日志排查"的
    /// 唯一线索 ⇒ 它每一处都必须落进日志文件。⛔ 只是记录：判据与行为一个字都不改。</para>
    /// </summary>
    internal void CriterionLog(
        string criterion,
        string inputs,
        string verdict,
        string action = "",
        string file = "",
        string identity = "",
        string level = "INFO",
        string errorCode = "",
        string component = "",
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLine = 0,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        => _vm.CriterionLog(
            criterion, inputs, verdict, action, file, identity, level, errorCode, component,
            sourceFile, sourceLine, caller);

    /// <summary>
    /// **测试用**的显式口子：true = 成功时也把全部细节写进日志
    /// （默认 <c>false</c> = 产品行为：成功的任务在日志里只留一行摘要）。
    ///
    /// <para>为什么留这个口子：一大批既有用例把"日志里那一行"当作**行为证据**（跨盘搬运走了复制、
    /// 工作区被清理、空间依据写了"同卷只算一份"……），而第 44 条的瘦身改的是**默认呈现**、
    /// 不是行为本身。让那些用例显式声明"我这个用例要看细节"，比把它们一条条改成断言摘要更诚实
    /// —— 也让"默认现在是简洁的"这件事只由 <c>LogVolumePolicyTests</c> 一处钉住。</para>
    ///
    /// <para>⚠ 用户侧的入口是 ⑥设置 →「详细日志（排查用）」（<see cref="AppSettings.VerboseLog"/>，
    /// 默认关），二者在 <see cref="LogCaptureHandler"/> 的详细档判据里合成同一个行为；
    /// ⛔ 这里这个口子只给测试用，界面不绑它。</para>
    /// </summary>
    internal bool KeepTaskDetailInLog { get; set; }

    /// <summary>
    /// **测试缝**：收尾（定稿/归集/搬源）**已经开始、第一道取消闸门之后**回调一次。
    ///
    /// <para>为什么需要它（用户「残余缺口：收尾内部按取消无用例钉住」）：收尾内部有三处
    /// `ThrowIfCancellationRequested()`（`ExtractionCoordinator.cs:2015` / `:2227` / `:2260` 附近），
    /// 而**没有任何用例能看见它们** —— 现有那两个钩子（`Engine.OnList/OnExtract`）都发生在收尾之前，
    /// 更早的闸门就先看见了取消（`ExtractionPipelineFixTests` 那条用例的注释自己写明了这一点）。
    /// ⇒ 只有在这里回调，测试才能把"取消"按在**收尾已经开始之后**。</para>
    ///
    /// <para>⛔ 默认 `null` = 一个字都不做（生产路径行为与改动前逐字相同）。</para>
    /// </summary>
    internal Action? PostProcessStartedForTests { get; set; }

    /// <summary>薄转发：实现见 <see cref="LogCaptureHandler.BeginTaskLogCapture"/>。</summary>
    internal void BeginTaskLogCapture(ArchiveTask task) => LogCapture.BeginTaskLogCapture(task);

    /// <summary>薄转发：实现见 <see cref="LogCaptureHandler.EndTaskLogCapture"/>。</summary>
    internal void EndTaskLogCapture(ArchiveTask task) => LogCapture.EndTaskLogCapture(task);

    /// <summary>
    /// 本批"其余物已处理"的记账（逐任务只写一行，批末一条汇总 —— 第 44 条）。
    ///
    /// <para>⚠ <c>internal</c>（不是 <c>private</c>）：计数由搬出去的 <see cref="LogCaptureHandler"/> 在锁里累加，
    /// 而批首清零仍在 <c>ExtractionCoordinator.ExtractEntryStep.cs</c>（另一个 partial）里直接赋 0 ——
    /// 两边都得能写，这是搬实现时留的**最小**访问面（没有新增属性，也没搬走清零那一处）。</para>
    /// </summary>
    internal int _batchPurgedTaskCount;

    internal long _batchPurgedBytes;

    internal bool _batchPurgedPermanently;

    /// <summary>薄转发：实现见 <see cref="LogCaptureHandler.NoteBatchPurge"/>。</summary>
    private void NoteBatchPurge(long freedBytes, bool permanent) =>
        LogCapture.NoteBatchPurge(freedBytes, permanent);

    /// <summary>
    /// 「停止后续」的那条日志**本批只写一次**（2026-09-27 真机：他连点两次按钮，
    /// 日志里"已请求停止后续任务"出现两遍，跑完收尾又是第三句不同措辞）。
    /// 批开始处（<c>_operationCts = new CancellationTokenSource()</c> 那一段）重置。
    ///
    /// <para>⚠ <c>internal</c> 的理由同上：置位与判重都在 <see cref="LogCaptureHandler"/>，
    /// 复位仍在 <c>ExtractionCoordinator.ExtractEntryStep.cs</c>。</para>
    /// </summary>
    internal bool _stopNoticeLogged;

    /// <summary>薄转发：实现见 <see cref="LogCaptureHandler.LogStopRequestOnce"/>。</summary>
    internal void LogStopRequestOnce() => LogCapture.LogStopRequestOnce();

    /// <summary>薄转发：实现见 <see cref="LogCaptureHandler.ReadBatchPurge"/>。</summary>
    private (int Tasks, long Bytes, bool Permanent) ReadBatchPurge() => LogCapture.ReadBatchPurge();

    internal void UpdateSummary() => _vm.UpdateSummary();


    internal void AppendLog(string message) => LogCapture.AppendLog(message);

    /// <summary>任务级日志：统一带任务名前缀，并走与普通解压相同的成功/失败捕获规则。</summary>
    internal void AppendTaskLog(ArchiveTask task, string message) =>
        LogCapture.AppendTaskLog(task, message);

    internal void AppendTaskLog(ArchiveTask task, string level, string message) =>
        LogCapture.AppendTaskLog(task, level, message);

    /// <summary>
    /// 适配既有 <see cref="Action{T1,T2}"/> 注入点的两参数入口（引擎原话/失败清单写入器）。
    /// 它只是转到带 caller 的唯一入口；⛔ 不另建一套日志逻辑。
    /// </summary>
    internal void AppendLogPair(string level, string message) => LogCapture.AppendLogPair(level, message);

    /// <summary>适配 <see cref="Action{T1,T2,T3,T4}"/> 注入点（失败清单写入器）。</summary>
    private void AppendLogWithCode(string level, string message, string errorCode, string component) =>
        LogCapture.AppendLogWithCode(level, message, errorCode, component);

    /*
     * ⚠ 下面两个 AppendLog 重载**必须**保留 CallerFilePath / CallerLineNumber / CallerMemberName：
     * 取值点在调用点，转发时把取到的值原样传下去 ⇒ 别的 partial / Handler 报进诊断日志的
     * 文件、行号、成员名与搬家前完全一致（实现见 LogCaptureHandler 的同签名重载）。
     */
    internal void AppendLog(
        string level,
        string message,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLine = 0,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        => LogCapture.AppendLog(level, message, sourceFile, sourceLine, caller);

    internal void AppendLog(
        string level,
        string message,
        string errorCode,
        string component,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLine = 0,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        => LogCapture.AppendLog(level, message, errorCode, component, sourceFile, sourceLine, caller);

}
