using ArchiveFixer.Models;
namespace ArchiveFixer.BindingModels;

/// <summary>
/// 把一次**识别结果**落到任务事实上的唯一策略（含"识别判不出"那一档）。
///
/// <para>从 <see cref="ArchiveTask"/> 抽出的协作类（「识别结果」那一块）：这里只做
/// "识别结果 ⇒ 该写哪几个绑定属性"，⛔ 不读盘、不判格式（判格式在识别服务里）、
/// 不碰源包路径与任何删除/落点判据。</para>
///
/// <para><b>失败模型</b>：<paramref name="result"/> 为 <c>null</c> 时才走"未知格式"那一档
/// （把上一轮的格式 / 后缀建议 / 内嵌偏移 / 改名建议全部作废，⛔ 不沿用上一轮结论）；
/// 其余情况逐字段照抄识别结果，没有异常路径。</para>
///
/// <para>生命周期：每次识别 / 重扫调用一次；它是**覆盖写**，调用前后的旧事实一律作废
/// （<c>VolumeRenameSuggestion</c> / <c>SplitVolumeAssemblyNote</c> 明确清零）。</para>
/// </summary>
internal static class DetectResultApplier
{
    /// <summary>应用识别结果到任务。</summary>
    internal static void Apply(ArchiveTask task, DetectResult? result, string extensionStatus)
    {
        if (result == null)
        {
            task.DetectedFormat = "Unknown";
            task.SuggestedExtension = string.Empty;
            task.IsArchive = false;
            task.IsEncrypted = false;
            task.EmbeddedArchiveOffset = 0;
            task.EmbeddedArchiveEnd = 0;
            task.EmbeddedDirectReadSupported = false;
            task.EmbeddedDirectReadReason = string.Empty;
            task.VolumeRenameSuggestion = string.Empty;
            task.SplitVolumeAssemblyNote = string.Empty;
            task.ExtensionStatus = StatusText.UnknownFormat;
            task.Status = StatusText.UnknownFormat;
            task.Operation = StatusText.OpScan;
            task.ProgressText = StatusText.ProgressCompleted;
            task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;
            return;
        }

        task.DetectedFormat = result.Format;
        task.SuggestedExtension = result.SuggestedExtension;
        task.IsArchive = result.IsArchive;
        task.IsEncrypted = result.IsProbablyEncrypted;

        // 重扫时也要跟着刷新：上一次的偏移对新文件没有意义（识别结果是会被覆盖的）。
        task.EmbeddedArchiveOffset = result.EmbeddedArchiveOffset;
        task.EmbeddedArchiveEnd = result.EmbeddedArchiveEnd;
        task.EmbeddedDirectReadSupported = result.EmbeddedDirectReadSupported;
        task.EmbeddedDirectReadReason = result.EmbeddedDirectReadReason ?? string.Empty;

        // 改名建议同理：它是"上一轮那次解压判决"的产物，识别结果一刷新就必须作废。
        task.VolumeRenameSuggestion = string.Empty;
        task.SplitVolumeAssemblyNote = string.Empty;
        task.ExtensionStatus = extensionStatus;

        if (result.IsArchive)
        {
            task.Status = StatusText.Recognized;
        }
        else if (result.IsKnownFormat)
        {
            task.Status = StatusText.Recognized;
        }
        else
        {
            task.Status = StatusText.UnknownFormat;
        }

        task.Operation = StatusText.OpScan;
        task.ProgressText = StatusText.ProgressCompleted;
        task.ErrorMessage = result.Message ?? string.Empty;
        task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;
    }
}
