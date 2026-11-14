using System;

namespace ArchiveFixer.Models;

/// <summary>
/// 解压前资源预算上限（不变量 8）**四条夹取规则的唯一实现处**。
///
/// <para>为什么单独成类：同一批规则被三处读 —— 设置层（<see cref="AppSettings.Normalize"/>）、
/// 界面层（<c>SettingsViewModel</c> 保存时夹回并当场告知）、真正判包的
/// <c>Security/ResourceBudgetOptions.FromSettings</c>。口径只许一处，
/// ⛔ 不许在别处再写一套 <c>Math.Clamp</c>；默认值与合法区间仍以
/// <see cref="AppSettings"/> 上的公开常量为唯一来源（这里不复制值）。</para>
///
/// <para>⚠ 顺序有意义：总大小那一档永远不小于单文件那一档（否则它形同虚设），
/// 所以 <see cref="NormalizeTotalGiB"/> 必须先拿到已经夹好的单文件值。</para>
/// </summary>
internal static class ExtractionResourceCapRules
{
    /// <summary>文件数上限的兜底上界（5000 万）：再大就不是「帮用户省事」，而是把时间烧在一个可能失败的包上。</summary>
    private const int MaxExtractedFileCountLimit = 50_000_000;

    /// <summary>展开比上限的兜底上界（100 万倍）。</summary>
    private const double MaxExtractionRatioLimit = 1_000_000d;

    /// <summary>把「单文件上限」夹进合法区间（≤0 视为没配 → 用默认值）。</summary>
    internal static int NormalizeSingleFileGiB(int value)
    {
        if (value <= 0)
        {
            return AppSettings.DefaultMaxSingleExtractedFileGiB;
        }

        return Math.Clamp(value, AppSettings.MinExtractionCapGiB, AppSettings.MaxExtractionCapGiB);
    }

    /// <summary>
    /// 把「总大小上限」夹进合法区间，并保证它**不小于**单文件上限 ——
    /// 否则单文件那一档永远先命中，用户以为调大了总量却什么都没变。
    /// </summary>
    internal static int NormalizeTotalGiB(int value, int singleFileGiB)
    {
        int single = NormalizeSingleFileGiB(singleFileGiB);
        int total = value <= 0
            ? AppSettings.DefaultMaxExtractedTotalGiB
            : Math.Clamp(value, AppSettings.MinExtractionCapGiB, AppSettings.MaxExtractionCapGiB);

        return Math.Max(single, total);
    }

    /// <summary>把「文件数上限」夹进合法区间（≤0 视为没配 → 用默认值）。</summary>
    internal static int NormalizeFileCount(int value)
    {
        if (value <= 0)
        {
            return AppSettings.DefaultMaxExtractedFileCount;
        }

        return Math.Clamp(value, 1, MaxExtractedFileCountLimit);
    }

    /// <summary>把「展开比上限」夹进合法区间（NaN / 无穷 / &lt;1 视为没配 → 用默认值）。</summary>
    internal static double NormalizeRatio(double value)
    {
        if (!double.IsFinite(value) || value < 1d)
        {
            return AppSettings.DefaultMaxExtractionRatio;
        }

        return Math.Min(value, MaxExtractionRatioLimit);
    }
}
