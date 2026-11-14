using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Detection;
/// <summary>一次判定的输入。</summary>
public sealed class VolumeGroupQuery
{
    /// <summary>围着哪个文件问（组里的任意一个，包括"改了名的那一个"）。</summary>
    public string AnchorPath { get; init; } = string.Empty;

    /// <summary>
    /// 同目录的事实清单。
    ///
    /// <para>为 null 时判定器**自己列目录**；给了就只用给的那一份（单测靠它摆脱真实磁盘的随机性）。
    /// 给了清单就不再碰文件系统（除了试开那一步）。</para>
    /// </summary>
    public IReadOnlyList<VolumeGroupEntry>? DirectoryEntries { get; init; }

    /// <summary>
    /// 临时物落点：<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>（用户 2026-09-30 口径）。
    ///
    /// <para>⛔ <b>空 = 不试开</b>（不是"另找个地方试"）：工作区只准设在解压的地方 ——
    /// 拿不到目标目录（例如改名那条路）或者它与第一卷不在同一个卷上（硬链接不能跨卷）时，
    /// 试开这一档**一次都不做**，结论如实降成"判不出"。</para>
    /// </summary>
    public string WorkRootDirectory { get; init; } = string.Empty;

    /// <summary>允不允许试开（导入期这种"只想保护、不想调引擎"的场合传 false）。</summary>
    public bool AllowTrialOpen { get; init; } = true;
}
