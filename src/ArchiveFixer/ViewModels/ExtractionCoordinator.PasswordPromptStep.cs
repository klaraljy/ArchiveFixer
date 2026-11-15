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
/// 手动密码/密码提示/密码失败汇总/危险条目提示（第 11 块）——实现已按职责搬到
/// <see cref="PasswordPromptHandler"/>（<c>ViewModels/Extraction/Steps/PasswordPromptHandler.cs</c>），
/// 这里只留同签名薄转发与最小 internal 访问器。
///
/// <para>⛔ 本分片不再放任何判据：搬走的是**实现**，调用点（<c>ExtractionCoordinator.ExtractEntryStep</c> /
/// <c>ExtractionBatchEntry</c> / <c>ExtractionPreflight</c>）一个字都没改。状态（手动密码是否问过、
/// 本批手动密码、密码失败清单）仍在 <c>PasswordFailureRegistry</c>，宿主只有**一个**实例 ⇒
/// 生命周期与原来完全一致。</para>
///
/// <para>两个常量（<see cref="DangerousEntriesHintPrefix"/> / <see cref="MaxPasswordFailureNamesInDialog"/>）
/// 留在宿主这一侧：单测与 <c>ExtractionBatchReminderMessages</c> 按 <c>ExtractionCoordinator.*</c>
/// 直接引用它们，搬走会改公开表面。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    /// <summary>搬出去的那一份密码提示/失败汇总/危险条目实现（懒建一次；宿主就是本类，全类共用同一个实例）。</summary>
    private PasswordPromptHandler? _passwordPromptHandler;

    private PasswordPromptHandler PasswordPrompts => _passwordPromptHandler ??= new PasswordPromptHandler(this);

    private Task PromptForManualBatchPasswordAsync(IReadOnlyList<ArchiveTask> tasks) =>
        PasswordPrompts.PromptForManualBatchPasswordAsync(tasks);

    private void LogOneClickPasswordHint(IReadOnlyList<ArchiveTask> selectedTasks) =>
        PasswordPrompts.LogOneClickPasswordHint(selectedTasks);

    /// <summary>
    /// 「危险条目提示」的展示前缀。它**不是**任务状态（不进 StatusText / 配色 / 统计），
    /// 只是一句提示文本的前缀，所以按 §7 的要求用常量收在这里，不散落字面量。
    /// </summary>
    internal const string DangerousEntriesHintPrefix = "可疑条目提示：";

    internal static string AnalyzeDangerousEntries(IEnumerable<ArchiveEntry>? entries, bool enabled) =>
        PasswordPromptHandler.AnalyzeDangerousEntries(entries, enabled);

    internal void PublishDangerousEntriesHint(ArchiveTask task, string hint, string level = "WARN") =>
        PasswordPrompts.PublishDangerousEntriesHint(task, hint, level);

    private Task ShowPasswordFailuresSummaryAsync(bool showDialog = true) =>
        PasswordPrompts.ShowPasswordFailuresSummaryAsync(showDialog);

    internal void SuppressDecisionPromptsForOneClickRun() =>
        PasswordPrompts.SuppressDecisionPromptsForOneClickRun();

    /// <summary>合并提示里最多列几个文件名（再多就让用户去看失败清单），别弹一个占满屏幕的框。</summary>
    internal const int MaxPasswordFailureNamesInDialog = 10;

    // ===== 搬出去的那份实现要用的私有成员（只加最小 internal 访问器；⛔ 字段语义一个字不改）=====

    /// <summary>唯一一份密码类失败/手动密码状态（定义在 <c>ExtractionCoordinator.cs</c>，字段本身保持 private）。</summary>
    internal PasswordFailureRegistry PasswordFailuresForPasswordPrompt => _passwordFailureRegistry;

    /// <summary>"需要密码但一个可用候选都没有"的唯一判据（实现在 <c>PasswordCandidatesStep</c>；本类里的转发是私有的）。</summary>
    internal List<ArchiveTask> FindSuspectsWithoutUsablePasswordForPasswordPrompt(IReadOnlyList<ArchiveTask> tasks) =>
        FindSuspectsWithoutUsablePassword(tasks);

    /// <summary>每层密码尝试上限（唯一来源；本类里的转发是私有的）。</summary>
    internal int MaxPasswordAttemptsPerLayerForPasswordPrompt => MaxPasswordAttemptsPerLayer;

    /// <summary>UI 线程上的警告框（唯一实现在 <c>UiThreadStep</c>；本类里的转发是私有的）。</summary>
    internal Task ShowWarningOnUiThreadForPasswordPrompt(string message) => ShowWarningOnUiThreadAsync(message);

    /// <summary>一键档把同名冲突询问标成"问不到答案"（唯一实现在 <c>ConflictStep</c>；本类里的转发是私有的）。</summary>
    internal void SuppressConflictPromptsForRun() => _conflictStep.SuppressPromptsForRun();
}
