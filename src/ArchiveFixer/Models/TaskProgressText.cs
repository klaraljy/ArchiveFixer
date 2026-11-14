using System;

namespace ArchiveFixer.Models;

/// <summary>
/// 进度那一块的**纯计算**：剩余时间估算、详情行拼接、续解层号那一句的措辞。
///
/// <para>从 <see cref="ArchiveTask"/> 抽出的协作类（「进度」那一块）：这里只做"给定这几个数，
/// 该显示什么文字"，⛔ 不持有任务状态、不发变更通知、不判成败。是否还有实时进度
/// （<see cref="ArchiveTask.HasLiveProgress"/>）由任务按 <see cref="ArchiveTask.EndTime"/> 给出，
/// 本类只把它当输入。</para>
///
/// <para><b>失败模型</b>：任何"算不出 / 样本不足 / 数值荒唐"都返回空串（⛔ 不显示 00:00 或天文数字），
/// 绝无异常路径（只做算术与字符串拼接）。</para>
///
/// <para>生命周期：纯静态、无状态；同一组输入永远得到同一句文字。</para>
/// </summary>
internal static class TaskProgressText
{
    /// <summary>
    /// 按当前这一轮解压的已用时间与百分比估算剩余时间（空串 = 还算不出）。
    ///
    /// <para>刚开始的一两秒样本太少，估出来只会是 <c>00:00</c> / 荒唐值 ⇒ 先不显示；
    /// 超过 24 小时的估算一律当"算不出"（那不是用户能用的数字）。</para>
    /// </summary>
    internal static string EstimateRemaining(bool hasLiveProgress, int progressPercent, DateTime? etaStartUtc, DateTime nowUtc)
    {
        if (!hasLiveProgress || progressPercent <= 0 || progressPercent >= 100 || etaStartUtc == null)
        {
            return string.Empty;
        }

        TimeSpan elapsed = nowUtc - etaStartUtc.Value;

        if (elapsed < TimeSpan.FromSeconds(2))
        {
            return string.Empty;
        }

        double remainingSeconds = elapsed.TotalSeconds * (100.0 - progressPercent) / progressPercent;

        return remainingSeconds is > 0 and < 86400
            ? TimeSpan.FromSeconds(remainingSeconds).ToString(remainingSeconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss")
            : string.Empty;
    }

    /// <summary>
    /// 任务详情窗口「进度」那一行：**百分比 + 剩余时间 + 当前条目 + 无响应提示**；
    /// 没有实时进度时退回既有的进度文案。
    /// </summary>
    internal static string DescribeDetail(
        bool hasLiveProgress,
        string fallbackProgressText,
        int progressPercent,
        string? estimatedRemainingText,
        string progressEntry,
        bool hasResponsivenessHint,
        string responsivenessHint)
    {
        if (!hasLiveProgress)
        {
            return fallbackProgressText;
        }

        string percent = $"{progressPercent}%";
        string eta = string.IsNullOrWhiteSpace(estimatedRemainingText)
            ? string.Empty
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.ProgressRemainingSuffixFormat,
                estimatedRemainingText);

        if (string.IsNullOrWhiteSpace(progressEntry))
        {
            return hasResponsivenessHint
                ? $"{percent}{eta} · {responsivenessHint}"
                : $"{percent}{eta}";
        }

        return hasResponsivenessHint
            ? $"{percent}{eta} · {progressEntry} · {responsivenessHint}"
            : $"{percent}{eta} · {progressEntry}";
    }

    /// <summary>
    /// 续解第 <paramref name="layer"/> 层这一步叫什么：**只记程序自己那一帧说的**
    /// （<c>第 N 层：&lt;那一层的包&gt;</c>），⛔ 不推算、不猜名字；那一帧没给条目时只写层号。
    /// </summary>
    internal static string DescribeLayerStep(int layer, string? entry) =>
        string.IsNullOrWhiteSpace(entry)
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.LiveLayerSuffixFormat,
                layer)
            : entry.Trim();
}
