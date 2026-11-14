using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 发布结果的**失败模型**：失败结果怎么造、搬了几个 / 改了名 / 没搬成的数字怎么讲清楚。
///
/// <para>为什么单独一层：失败与计数的话术必须和搬运实际做的事**同一口径**，
/// 而"就地替换那一档搬动的数大于落点里最终的文件数"这类差别只在这一处解释。
/// ⛔ 别处不许自己拼一句"已发布 N 个文件"（2026-10-02 真机就是两行互相打脸）。</para>
/// </summary>
internal static class WorkspacePublishOutcome
{
    /// <summary>发布失败的结果（数字一律 0，原因写在 <c>Message</c> 里）。</summary>
    internal static WorkspacePublishResult Failure(string destination, string message)
    {
        return new WorkspacePublishResult
        {
            Success = false,
            DestinationPath = destination,
            MovedFileCount = 0,
            Message = message
        };
    }

    /// <summary>发布结果那句话（数字口径与实际搬运一致，见下面那个开关）。</summary>
    /// <param name="countsMovedNotLanded">
    /// 就地替换那一档（<c>inPlaceInnerPackages</c>）传 true：搬动的文件里**包含**那些
    /// "落位之后位置让给同名目录、随后被拿掉"的内层包，所以那个数**大于**落点里最终的文件数。
    ///
    /// <para>2026-10-02 真机现场：日志写「已发布 146 个文件到 …\stage」，紧接着下一行就是
    /// 「结果校验 —— 校验通过：预期 145 个文件 / 实际 145 个」—— 两行互相打脸。
    /// 数字本身没错（146 = 145 个内容物 + 1 个被替换掉的内层包），错的是那句话让人读成
    /// "落点里有 146 个"。</para>
    /// </param>
    internal static string Describe(
        string destination,
        int movedCount,
        List<string> renamed,
        List<string> errors,
        bool countsMovedNotLanded = false,
        bool intakeStaging = false)
    {
        var parts = new List<string>();

        if (movedCount <= 0)
        {
            parts.Add("没有可发布的产物文件");
        }
        else if (intakeStaging)
        {
            /*
             * 2026-10-07 真机 EEEE：目标是入仓目录（不是落点）⇒ ⛔ 不许写"已发布…到 <工作区路径>"，
             * 那句话会被读成"成品留在了工作区"。数字照旧（含被替换掉的内层包那一档也不影响本句）。
             */
            parts.Add($"已入仓 {movedCount} 个文件（中间产物，定稿时才搬进落点）");
        }
        else if (countsMovedNotLanded)
        {
            parts.Add(
                $"搬运 {movedCount} 个文件到 {destination}"
                + "（含已被同名目录替换掉的内层包，所以比落点里最终的文件数多 —— 落点里几个看校验那一行）");
        }
        else
        {
            parts.Add($"已发布 {movedCount} 个文件到 {destination}");
        }

        if (renamed.Count > 0)
        {
            parts.Add($"其中 {renamed.Count} 个同名文件已改名，未覆盖原有文件（{string.Join("、", renamed)}）");
        }

        if (errors.Count > 0)
        {
            parts.Add($"{errors.Count} 个文件没能移动（{string.Join("；", errors)}）");
        }

        return string.Join("；", parts);
    }
}
