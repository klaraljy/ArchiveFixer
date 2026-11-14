using System;
using System.Collections.Generic;

namespace ArchiveFixer.Detection;

/// <summary>
/// **首卷判定**：整组里"哪一份自述是第 1 卷"与内容卷号互相印证，以及入口那一卷是不是第 1 卷。
///
/// <para><b>为什么单独成层</b>：第 1 卷有两条互相独立的证据 —— ① 归档自己写在头里的标记
/// （RAR5 的"没有卷号字段"、RAR 1.5–4.x 的 <c>MHD_FIRSTVOLUME</c>），② 由内容算出来的卷号 = 1。
/// 只有**恰好一份**带着第 1 卷标记、且它算出来的卷号也是 1，才认这一组锚在第 1 卷上；
/// 对不上（例如"第 1 卷"标记长在第 3 份上的残组）一律不认 —— 改错名字比不改更糟。</para>
///
/// <para>⛔ 这里只读读数上的两条内容事实（<see cref="VolumeNumberReading.FirstVolumeFlag"/> 与算好的卷号），
/// 不看名字、不按格式另加分支。入口那一档只回答"用户手上这一卷是不是第 1 卷"；
/// 改名计划那一档用 <c>requireCurrentFirstVolume: false</c> 跳过它（见主类
/// <c>VolumeNumberFromContent.ResolveGroup</c> 的参数说明），卷号证据一个字没放宽。</para>
///
/// <para>生命周期：纯静态、无状态；只读调用方给的事实清单。</para>
/// </summary>
internal static class VolumeFirstVolumeJudge
{
    /// <summary>
    /// RAR 新编号族：**恰好一份**带第 1 卷标记，且它算出来的卷号 = 1。
    ///
    /// <para>原判据（逐字）：<c>flagged.Count != 1 || numbers[members.IndexOf(flagged[0])] != 1</c> ⇒ 拒绝。
    /// 少了这一步，"0 起的整组里恰好缺了第 1 卷"会被读成"1 起的一组"——
    /// 那就会把第 2 卷改名叫 part1.rar（改错名字比不改更糟）。</para>
    /// </summary>
    public static bool RarHeaderFlagsMatchNumbers(
        IReadOnlyList<VolumeNumberReading> members,
        IReadOnlyList<int> numbers)
    {
        int flaggedIndex = -1;

        for (int index = 0; index < members.Count; index++)
        {
            if (!members[index].FirstVolumeFlag)
            {
                continue;
            }

            if (flaggedIndex >= 0)
            {
                // 两份都自称"第 1 卷"：说不清哪一份是，判不出。
                return false;
            }

            flaggedIndex = index;
        }

        return flaggedIndex >= 0 && numbers[flaggedIndex] == 1;
    }

    /// <summary>整组的第一槽必须是卷号 1（定序结论里"第 1 卷"这一格）。</summary>
    public static bool GroupStartsAtFirstVolume(IReadOnlyList<VolumeGroupSlot> slots) =>
        slots.Count > 0 && slots[0].Number == 1;

    /// <summary>
    /// 入口那一卷是不是这一组的第 1 卷；<paramref name="requireCurrentFirstVolume"/> = <c>false</c>
    /// 时不做这一档判断（改名计划那一档：用户手上可能是 <c>.part2.rar</c>）。
    /// </summary>
    public static bool IsEntryPointFirstVolume(
        string currentPath,
        string firstVolumePath,
        bool requireCurrentFirstVolume) =>
        !requireCurrentFirstVolume
        || string.Equals(currentPath, firstVolumePath, StringComparison.OrdinalIgnoreCase);
}
