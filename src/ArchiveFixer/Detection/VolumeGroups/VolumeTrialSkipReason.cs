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
/// <summary>
/// **没试开**的结构化原因。⛔ 消费方只许读这个枚举，不许去比 <see cref="VolumeTrialOutcome.Reason"/> 的中文。
///
/// <para>为什么要分档：只有 <see cref="CrossVolume"/> 会**改变结论**（跨盘 ⇒ 判不出）；
/// 其余几档都是"这一次没取到证据"，结论照旧由前五条证据给（"没试 ≠ 不成立"）。</para>
/// </summary>
public enum VolumeTrialSkipReason
{
    /// <summary>试过了（或者根本没走到试开这条证据）。</summary>
    None = 0,

    /// <summary>没有可用的解压引擎。</summary>
    NoEngine = 1,

    /// <summary>凑不出可试的顺序（卷号上有洞 —— 缺一卷 7-Zip 必然报 Missing volume）。</summary>
    NoOrderings = 2,

    /// <summary>拿不到合法的试开目录 / 建不出试开目录。</summary>
    NoProbeRoot = 3,

    /// <summary>
    /// **跨盘**：源文件与工作区根不在同一个卷上 —— 而硬链接不能跨卷。
    ///
    /// <para>这一档**必须如实降成"判不出"**：⛔ 不许退到源卷根偷偷开工作区去试
    /// （工作区只准设在解压的地方，在源盘开目录正是不变量 12 禁止的形态；收工删掉也一样 ——
    /// 中途崩掉就在用户盘上留垃圾）。判不出 ⇒ 不删源、不移动源，安全结论一个字不变。</para>
    /// </summary>
    CrossVolume = 4,

    /// <summary>别的意外（第一卷不在、试开请求为空等）。</summary>
    Other = 5,
}
