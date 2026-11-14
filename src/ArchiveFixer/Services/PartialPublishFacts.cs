using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using System;

namespace ArchiveFixer.Services;

/// <summary>
/// 「部分完成」这一档的两个**唯一事实出口**。两个事实各自都有两个消费方，所以只在这里判一次：
/// <list type="number">
/// <item><description>这一单算不算「损坏压缩包」—— <see cref="IsDamagedArchive"/>。
/// 消费方 = 候选登记（盖 <see cref="PartialPublishStep.PartialPublishCandidate.Damaged"/> 那一位）
/// ＋ 对外门面 <see cref="PartialPublishStep.IsDamagedArchive"/>。</description></item>
/// <item><description>递归链停在哪一层 —— <see cref="ResolveStoppedLayer"/>。
/// 消费方 = 候选登记（哪一层的产物才是"解了一半的现场"）＋ 过程物保留（哪一份才是"最后一层过程物"）。</description></item>
/// </list>
///
/// <para>⛔ 判据只读**结构化事实**（引擎的错误类型 / 各层的 <c>Success</c> 位），⛔ 不比中文文案、
/// 不看体积与名字。同一件事两个消费方各判一遍迟早会分叉 —— 这就是把它们收在这里的原因。</para>
/// </summary>
internal static class PartialPublishFacts
{
    /// <summary>
    /// **「这一单算不算『损坏压缩包』」的唯一出口**（用户 2026-10-14 口径：部分完成发布从"用户开关"
    /// 降到底层 —— 「就是『损坏压缩包的解压情况』，这种情况下会自动开启，**而且用户不能操作**」）。
    ///
    /// <para>判据只看**结构化事实**：引擎明说 <see cref="EngineErrorTypes.CorruptedArchive"/>
    /// （"归档损坏 / 校验和不符 / 数据不全"）。⛔ 不比中文文案、⛔ 不看体积/名字一类推断。</para>
    ///
    /// <para><b>为什么不含"两义"那一档</b>（<see cref="EngineErrorTypes.PasswordOrCorrupted"/>）：
    /// 引擎在那里同一句话里既说密码不对又说数据坏了，**它自己都判不出**；而这一类最常见的成因是
    /// 密码不对（旧式 RAR 加密包）⇒ 按项目铁律「判不出 ⇒ 什么都不做」，这一档**不发布**
    /// （要发布得先让用户看到那两种可能、由他决定，⛔ 不是我们替他挑一个）。</para>
    /// </summary>
    internal static bool IsDamagedArchive(string? detectedErrorType) =>
        string.Equals(detectedErrorType, EngineErrorTypes.CorruptedArchive, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// **「链停在哪一层」的唯一出口**：报告里**最后一个没跑成的层**（都在跑成 / 没跑过任何一层 ⇒ null）。
    ///
    /// <para>为什么要抽出来：这一件事现在有**两个**消费方 —— ① 部分完成发布的登记（那一层的产物就是
    /// "解了一半的现场"）；② 「最后一层过程物」按「删除操作」那一档留 / 删（那一层**要解的那个包**
    /// 就是口径里说的「最后一层过程物」）。⛔ 两处各判一遍迟早会分叉。</para>
    /// </summary>
    internal static RecursionLayerReport? ResolveStoppedLayer(RecursionResult? result)
    {
        if (result == null)
        {
            return null;
        }

        RecursionLayerReport? stopped = null;

        foreach (RecursionLayerReport layer in result.Layers)
        {
            if (!layer.Success)
            {
                stopped = layer;
            }
        }

        return stopped;
    }
}
