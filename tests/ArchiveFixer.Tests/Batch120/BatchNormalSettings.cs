using System;

namespace ArchiveFixer.Tests.Batch120;

/// <summary>
/// Normal 1–40 的**测试操作设置**，不是造包器里的样本形状。
/// 由测试操作代码模拟人选择：位置严格 20/20；源包/最后一层过程物随机；
/// 8 个组合都出现，但不要求每个组合 5 份。
/// </summary>
internal static class BatchNormalSettings
{
    internal readonly record struct Choice(string Placement, string SourceHandling, string RestHandling);

    // 生成规则固定，保证同一 seed 可复现；第 1 份对应 930000000 例子。
    private static readonly Choice[] Table =
    {
        new("Original", "DeleteInPlace", "Keep"),
        new("Specified", "KeepInPlace", "Delete"),
        new("Original", "KeepInPlace", "Delete"),
        new("Specified", "KeepInPlace", "Keep"),
        new("Original", "KeepInPlace", "Keep"),
        new("Original", "DeleteInPlace", "Keep"),
        new("Specified", "KeepInPlace", "Keep"),
        new("Specified", "DeleteInPlace", "Keep"),
        new("Specified", "DeleteInPlace", "Delete"),
        new("Original", "KeepInPlace", "Keep"),
        new("Original", "DeleteInPlace", "Keep"),
        new("Specified", "KeepInPlace", "Keep"),
        new("Specified", "DeleteInPlace", "Keep"),
        new("Specified", "KeepInPlace", "Delete"),
        new("Original", "DeleteInPlace", "Delete"),
        new("Specified", "DeleteInPlace", "Keep"),
        new("Specified", "KeepInPlace", "Delete"),
        new("Specified", "KeepInPlace", "Keep"),
        new("Original", "KeepInPlace", "Delete"),
        new("Specified", "KeepInPlace", "Delete"),
        new("Original", "DeleteInPlace", "Delete"),
        new("Specified", "KeepInPlace", "Keep"),
        new("Specified", "KeepInPlace", "Keep"),
        new("Original", "DeleteInPlace", "Keep"),
        new("Specified", "DeleteInPlace", "Keep"),
        new("Original", "DeleteInPlace", "Keep"),
        new("Specified", "DeleteInPlace", "Delete"),
        new("Specified", "DeleteInPlace", "Delete"),
        new("Specified", "KeepInPlace", "Delete"),
        new("Original", "DeleteInPlace", "Delete"),
        new("Original", "KeepInPlace", "Keep"),
        new("Specified", "DeleteInPlace", "Keep"),
        new("Specified", "DeleteInPlace", "Keep"),
        new("Original", "KeepInPlace", "Delete"),
        new("Original", "DeleteInPlace", "Delete"),
        new("Original", "KeepInPlace", "Delete"),
        new("Original", "KeepInPlace", "Delete"),
        new("Original", "KeepInPlace", "Keep"),
        new("Original", "DeleteInPlace", "Keep"),
        new("Original", "DeleteInPlace", "Keep")
    };

    public static bool TryGet(string sampleId, string seed, out Choice choice)
    {
        choice = default;
        if (!long.TryParse(sampleId, out long id) || !long.TryParse(seed, out long seedBase))
        {
            return false;
        }

        long index = id - seedBase;
        if (index < 1 || index > Table.Length)
        {
            return false;
        }

        choice = Table[(int)index - 1];
        return true;
    }
}
