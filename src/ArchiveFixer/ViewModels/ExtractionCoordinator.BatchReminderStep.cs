using ArchiveFixer.BindingModels;
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
/// 批前提醒/确认框/并行与输出目录（第 11 块）——实现已按职责搬到 <see cref="BatchReminderHandler"/>
/// （<c>ViewModels/Extraction/Steps/BatchReminderHandler.cs</c>），这里只留同签名薄转发。
///
/// <para>⛔ 本分片不再放任何判据：搬走的是**实现**，调用点（<c>ExtractionBatchEntry</c> /
/// <c>OneClickConfirmFlow</c> / <c>OneClickBatchDriver</c> / <c>ExtractionCoordinator.VolumeRepairStep</c> /
/// <c>ExtractionCoordinator.PostProcessSuccessStep</c> / <c>ExtractionCoordinator.ExtractEntryStep</c>）
/// 一个字都没改。状态（本次运行不再提醒、批记账、预扫结论、输出目录只开一次）随之归
/// <see cref="BatchReminderHandler"/> 持有：本类只有**一个**宿主实例 ⇒ 生命周期与原来完全一致。</para>
///
/// <para>嵌套类型 <c>ReminderAnswer</c> / <c>BatchReminderFacts</c> 与 <c>ReminderAnswerOverride</c>
/// 留在宿主这一侧：测试与调用方按 <c>ExtractionCoordinator.*</c> 引用它们，搬走会改公开表面。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    /// <summary>搬出去的那一份提醒/确认实现（懒建一次；宿主就是本类，全类共用同一个实例）。</summary>
    private BatchReminderHandler? _batchReminderHandler;

    private BatchReminderHandler BatchReminders => _batchReminderHandler ??= new BatchReminderHandler(this);

    private Task<bool> DispatcherSafeConfirmAsync(string message) =>
        BatchReminders.DispatcherSafeConfirmAsync(message);

    private Task<string> ShowFolderBrowserOnUiThreadAsync() =>
        BatchReminders.ShowFolderBrowserOnUiThreadAsync();

    /// <summary>
    /// 排队等空位**超过这个秒数**才单独写一行（否则只进批末汇总）。
    ///
    /// <para>用户 2026-09-26 第 45 条：原来每个被节流挡住的包都写两行同义反复的话，真机上 76 个任务
    /// 出了 74 条 —— 而绝大多数等待只有一两秒，根本不值得占一行。</para>
    ///
    /// <para>⚠ 它的读者在 <c>ExtractionCoordinator.ExtractEntryStep</c>（同一 partial 类），
    /// 所以常量留在宿主这一侧，⛔ 不跟着实现搬走。</para>
    /// </summary>
    private const int ThrottleNoticeThresholdSeconds = 30;

    internal int ResolveMaxParallel(IReadOnlyList<ArchiveTask> tasks, out bool fullSpeed) =>
        BatchReminders.ResolveMaxParallel(tasks, out fullSpeed);

    internal int ResolveManualConcurrencyLimit() => BatchReminders.ResolveManualConcurrencyLimit();

    private int ResolveSpaceTightParallelForBatch(ExtractionSchedulePlan plan) =>
        BatchReminders.ResolveSpaceTightParallelForBatch(plan);

    internal void ResetBatchOutputFolderState() => BatchReminders.ResetBatchOutputFolderState();

    private bool TryOpenOutputFolderOnce(ArchiveTask task) => BatchReminders.TryOpenOutputFolderOnce(task);

    /// <summary>
    /// 提醒处理器要复用"需要密码但没有可用候选"那套判据（唯一实现在 <c>PasswordCandidatesStep</c>；
    /// 本类里的转发是私有的）——**只**给搬出去的 <see cref="BatchReminderHandler"/> 留这一个内部入口，
    /// ⛔ 不另算一套判据。
    /// </summary>
    internal List<ArchiveTask> FindReminderTasksWithoutUsablePassword(IReadOnlyList<ArchiveTask> tasks) =>
        FindTasksWithoutUsablePassword(tasks);

    /// <summary>
    /// 解压前提醒的"用户答案"（可注入；单测用它模拟"点了继续 / 点了先不处理 / 勾了不再提示"）。
    ///
    /// <para>为什么需要它：无界面宿主里那个框根本不会显示，而"勾了不再提示之后到底还会不会再弹"
    /// 只有让调用方答一次才测得出来。正式路径永远是 null（走真弹窗）。</para>
    /// </summary>
    internal Func<string, ReminderAnswer>? ReminderAnswerOverride { get; set; }

    /// <summary>一次"解压前的提醒"的用户答案。</summary>
    internal sealed class ReminderAnswer
    {
        /// <summary>true = 继续处理；false = 先不处理（这一批不开始，一个字节都不动）。</summary>
        public bool Confirmed { get; init; }

        /// <summary>是否勾了「本次运行不再提示这类提醒」。</summary>
        public bool OptionChecked { get; init; }
    }

    /// <summary>
    /// 一次「解压前的提醒」扫描的结论（A 段无用物 + B 段无可用密码）。
    ///
    /// <para>为什么把"扫出事实"和"弹框问人"拆开（用户 2026-09-24 第 17 条）：一键处理那条路
    /// 只允许有**一个**弹窗，所以它得在**动手之前**先拿到这两段事实、写进自己的确认框；
    /// 手动「只解压」那条路仍然用同一个扫描结果去弹原来那个提醒框。两段判据因此只有一份实现
    /// （<see cref="SourceJunkScanner"/> + <see cref="FindTasksWithoutUsablePassword"/>），
    /// 不会出现"确认框里说没有、解压时又提醒有"这种自相矛盾。</para>
    /// </summary>
    internal sealed class BatchReminderFacts
    {
        /// <summary>A 段：源目录里疑似打包者附带的文件。</summary>
        public SourceJunkScanResult Junk { get; init; } = new();

        /// <summary>B 段：需要密码但当前一个可用候选都没有的包。</summary>
        public IReadOnlyList<ArchiveTask> NoUsablePassword { get; init; } = Array.Empty<ArchiveTask>();

        /// <summary>两段都空 = 没什么可提醒的（既不弹框、也不写日志）。</summary>
        public bool HasAnything => Junk.HasAnything || NoUsablePassword.Count > 0;
    }

    internal Task<BatchReminderFacts> PrepareBatchReminderForCallerAsync(IReadOnlyList<ArchiveTask> selectedTasks) =>
        BatchReminders.PrepareBatchReminderForCallerAsync(selectedTasks);

    internal void SuppressBatchReminderDialogForThisRun() =>
        BatchReminders.SuppressBatchReminderDialogForThisRun();

    internal void EndCallerHandledReminderBatch() => BatchReminders.EndCallerHandledReminderBatch();

    internal Task<BatchReminderFacts> ScanBatchRemindersAsync(IReadOnlyList<ArchiveTask> selectedTasks) =>
        BatchReminders.ScanBatchRemindersAsync(selectedTasks);

    internal Task<OneClickConfirmFacts> BuildConfirmFactsAsync(
        IReadOnlyList<ArchiveTask> targets,
        OneClickRunOptions? pendingOptions,
        BatchReminderFacts? reminders,
        RunSnapshot? snapshot = null,
        CancellationToken cancellationToken = default) =>
        BatchReminders.BuildConfirmFactsAsync(targets, pendingOptions, reminders, snapshot, cancellationToken);

    internal Task<string> DescribePlannedDestinationAsync(
        IReadOnlyList<ArchiveTask> targets,
        OneClickRunOptions? pendingOptions,
        CancellationToken cancellationToken = default) =>
        BatchReminders.DescribePlannedDestinationAsync(targets, pendingOptions, cancellationToken);

    internal ExtractOptions BuildExtractOptions(bool tryExtractUnknownFormat) =>
        BatchReminders.BuildExtractOptions(tryExtractUnknownFormat);

    internal Task<bool> ConfirmBatchRemindersAsync(IReadOnlyList<ArchiveTask> selectedTasks) =>
        BatchReminders.ConfirmBatchRemindersAsync(selectedTasks);

    internal static string DisplayNameOf(ArchiveTask task) => BatchReminderHandler.DisplayNameOf(task);
}
