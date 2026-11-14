using System;
using System.Globalization;
using System.IO;

namespace ArchiveFixer.Detection;

/// <summary>
/// 试开落点：给"硬链接试开"一个**合法且同卷**的工作目录，以及试开时用的假设名。
///
/// <para><b>为什么落点要独立成层</b>（用户 2026-09-30 口径：临时物只准落
/// <c>&lt;目标目录&gt;\.ArchiveFixer.work</c>）：试开靠硬链接伪造 <c>.001/.002</c>，
/// 而硬链接不能跨卷 —— 落点判错一次，要么复制 GB 级文件（几十 GiB / 几分钟），
/// 要么在用户源盘上开目录（不变量禁止"把中间产物写进源目录"）。这两条都是红线，
/// 所以"能不能试开"只在这一处回答。</para>
///
/// <para><b>失败模型</b>：没有工作区根 / 跨卷 / 路径形状非法 ⇒ 返回**空串**
/// （= 不试开），由调用方如实报"无法确认 ⇒ 判不出"；⛔ 不回落源卷根、⛔ 不另找地方、
/// ⛔ 不复制大文件去凑。全程**绝不抛**（只做路径字符串运算）。</para>
///
/// <para>生命周期：纯静态、无状态；每次调用现算一个带随机后缀的目录名（只算名字，
/// **不建目录** —— 建目录是调用方在试开流程里的事）。</para>
/// </summary>
internal static class VolumeProbeWorkspace
{
    /// <summary>试开用的硬链接基名：7-Zip 只认"基名 + .001/.002"这套名，基名叫什么它不在乎（实测）。</summary>
    private const string ProbeBaseName = "volprobe.7z";

    /// <summary>
    /// 试开目录：**只认调用方给的工作区根**（用户 2026-09-30 口径：临时物只准落
    /// <c>&lt;目标目录&gt;\.ArchiveFixer.work</c>）。
    ///
    /// <para>⛔ <b>没有工作区根 = 不试开</b>。这一档以前会退到"第一卷所在卷根下的
    /// <c>.ArchiveFixer.work</c>"，已经**整条删掉**（用户红线原话：「工作区就设在解压的地方，
    /// 这就完全不存在跨盘的操作」；在源盘开目录正是不变量 12 禁止的"把中间产物写进源目录"，
    /// 收工删掉也一样 —— 中途崩掉就在用户盘上留垃圾）。本机实测那两个卷根空壳
    /// （<c>C:\.ArchiveFixer.work</c> / <c>E:\.ArchiveFixer.work</c>）就是这条老路留下的。</para>
    ///
    /// <para>同理：<b>跨卷也不试</b>（硬链接不能跨卷）—— 两档都返回空串，由调用方如实报
    /// "无法确认 ⇒ 判不出"。⛔ 也绝不复制大文件去凑（一卷 2 GiB，复制一组要几十 GiB 和几分钟）。</para>
    /// </summary>
    /// <param name="firstVolumePath">第一卷（决定"必须同卷"的那个卷是哪一个）。</param>
    /// <param name="preferredWorkRoot">
    /// 工作区根（<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>）。空 / 跨卷 / 路径形状不合法 ⇒ 返回空串。
    /// </param>
    public static string BuildProbeRoot(string? firstVolumePath, string? preferredWorkRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(preferredWorkRoot))
            {
                // 没有工作区根 ⇒ 没有合法落点（⛔ 不另找地方开一个）。
                return string.Empty;
            }

            string full = Path.GetFullPath(firstVolumePath ?? string.Empty);

            if (!IsSameVolumeRoot(full, preferredWorkRoot))
            {
                // 跨卷 ⇒ 硬链接做不了；⛔ 不退源卷根、⛔ 不复制。
                return string.Empty;
            }

            return Path.Combine(
                Path.GetFullPath(preferredWorkRoot.Trim()),
                "volprobe-" + Guid.NewGuid().ToString("N"));
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 两个路径是不是在**同一个卷**上（= 硬链接唯一的成立条件）。
    ///
    /// <para>取不到任一侧的卷根 ⇒ <c>false</c>（判不出同一卷就按"跨卷"处理：宁可不试）。
    /// 判据只看盘符/卷根字符串，⛔ 不查文件系统、不问卷 GUID —— 本程序不做设备身份。</para>
    /// </summary>
    public static bool IsSameVolumeRoot(string? pathA, string? pathB)
    {
        try
        {
            string rootA = Path.GetPathRoot(Path.GetFullPath(pathA ?? string.Empty)) ?? string.Empty;
            string rootB = Path.GetPathRoot(Path.GetFullPath(pathB ?? string.Empty)) ?? string.Empty;

            return rootA.Length > 0 && rootB.Length > 0
                && string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>试开时第 <paramref name="index"/>（1 起）卷的假设名。</summary>
    public static string ProbeFileName(int index) =>
        ProbeBaseName + "." + index.ToString("D3", CultureInfo.InvariantCulture);
}
