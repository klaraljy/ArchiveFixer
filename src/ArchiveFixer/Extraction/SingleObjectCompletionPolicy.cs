using ArchiveFixer.BindingModels;
using ArchiveFixer.Models;
using System;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 单一物（一个完整源包，或一个完整分卷组）完成后的共同裁决。
///
/// <para>普通档和特定解压都必须从这一处读；⛔ 不再由特定解压自己判
/// <c>spaceTight || ...</c>，也不再看批次汇总。</para>
/// </summary>
public static class SingleObjectCompletionPolicy
{
    /// <summary>
    /// 是否允许删除这一份源包。
    ///
    /// <para>空间不足：成功后删；普通档：只有选 DeleteInPlace 才删。失败/部分完成/取消一律不删。
    /// 无论哪一档，都必须有真实输出校验证据。</para>
    /// </summary>
    public static bool ShouldDeleteSource(
        SourceHandlingMode sourceHandling,
        bool spaceTight,
        TaskOutcome outcome,
        OutputVerificationResult? verification,
        bool cancelled)
    {
        if (cancelled || outcome != TaskOutcome.Succeeded)
        {
            return false;
        }

        if (!spaceTight && sourceHandling != SourceHandlingMode.DeleteInPlace)
        {
            return false;
        }

        return verification?.AllowsSourceRemoval == true;
    }

    /// <summary>
    /// 失败/部分完成/取消时，最后一层过程物是否保留。
    /// 空间不足固定保留；普通档按「删除操作」档，默认保留。
    /// </summary>
    public static bool ShouldKeepLastProcess(string? restHandling, bool spaceTight)
    {
        if (spaceTight)
        {
            return true;
        }

        return !string.Equals(restHandling, RestHandlingModes.Delete, StringComparison.OrdinalIgnoreCase);
    }
}
