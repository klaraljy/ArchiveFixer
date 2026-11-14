using System;

namespace ArchiveFixer.Models;

/// <summary>
/// 「其余物」在**解压成功 + 输出校验通过 + 未取消**之后怎么处理（设置项
/// <see cref="AppSettings.RestHandlingAfterVerify"/>）的**唯一词表**。
///
/// <para><b>用户 2026-10-15 定的两档</b>（原话：「我们这里现在只有留下和彻底删除操作，已经不存在放入回收站了」
/// ＋「这个删除操作有两个选项，留下和彻底删除……默认留下」）：</para>
/// <list type="number">
/// <item><description><see cref="Keep"/>：留下（**默认**）。</description></item>
/// <item><description><see cref="Delete"/>：彻底删除、直接省空间 —— **不可恢复**，界面上整条标红。</description></item>
/// </list>
///
/// <para>⚠ 原来的第三档「移入回收站」2026-10-15 **整条下线**（用户口径逐字：已经不存在放入回收站了）。
/// 老配置里存过的 <c>"RecycleBin"</c> 由 <see cref="Normalize"/> 落进 <see cref="Keep"/>（保守方向）。</para>
///
/// <para>⚠ 它同时取代了 2026-09-22 那套「危险模式 + 自测凭证」（用户 2026-09-25 明确要求整块删掉）：
/// 不再有自测、风险四条与红横幅 —— **红字就是警告**。而"失败 / 部分完成 / 取消一个字节都不删"这条红线
/// 由 <c>Storage/RestItemPurger</c> 的五道门槛继续钉着（与哪一档无关）。</para>
/// </summary>
public static class RestHandlingModes
{
    /// <summary>留下（默认）。</summary>
    public const string Keep = "Keep";

    /// <summary>彻底删除（不可恢复，直接省空间）。</summary>
    public const string Delete = "Delete";

    /// <summary>两档的唯一判据（认不出的值一律回落 <see cref="Keep"/>，绝不回落成"删"）。</summary>
    public static bool IsKnown(string? value) =>
        string.Equals(value, Keep, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, Delete, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 归一化：认不出的值 → <see cref="Keep"/>（保守方向：宁可不动，绝不误删）。
    ///
    /// <para>⚠ 老配置里存过的 <c>"RecycleBin"</c> 也落进 <see cref="Keep"/> —— 那一档 2026-10-15 已整条下线。
    /// ⛔ 绝不许把它当成 <see cref="Delete"/>：那是把"可还原"悄悄升级成"不可恢复"，方向正好反了。</para>
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.Equals(value, Delete, StringComparison.OrdinalIgnoreCase))
        {
            return Delete;
        }

        return Keep;
    }
}
