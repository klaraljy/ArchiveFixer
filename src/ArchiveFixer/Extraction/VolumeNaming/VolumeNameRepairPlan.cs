using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 「按建议改名并重试」的计划（用户 2026-09-25 第 41 条）。
///
/// <para><b>为什么要有它</b>：第一卷第名字被改坏（<c>X.7z(删掉.001</c>）时，程序**只能**报「分卷缺失」
/// 并给一个改名建议 —— 源文件一个字节都不能动（不变量 1）。可用户手上那一卷是真需要改名的，
/// 让他自己去资源管理器里对着日志手打一遍名字，既慢又容易打错（那名字本来就不规则）。
/// 于是给一个**显式**的按钮：「按建议改名并重试」——用户点了才改，只改**名字**、内容一个字节不动。</para>
///
/// <para><b>判据全在事实里</b>（不看中文状态、不看调用方怎么想）：</para>
/// <list type="number">
/// <item><description>源文件还在；</description></item>
/// <item><description>它的名字里**有卷号、而且是第 1 卷**（不是第一卷时改名毫无意义）；</description></item>
/// <item><description>同目录里找得到"像后续卷"的文件（一个都没有就凑不齐一组）；</description></item>
/// <item><description>从后续卷的名字**推得出**标准名（推不出来宁可不做 —— 改错名字比不改更糟）；</description></item>
/// <item><description>它现在的名字**不是**标准名（已经是了就别乱动）；</description></item>
/// <item><description>目标名**没被占用**（⛔ 绝不覆盖，不变量 3）。</description></item>
/// </list>
///
/// <para>任一不成立 → <see cref="VolumeNameRepairPlan.CanRepair"/> = false，并把**为什么不能改**
/// 写在 <see cref="VolumeNameRepairPlan.Reason"/> 里（直接给用户看，不许含糊）。</para>
/// </summary>
public sealed class VolumeNameRepairPlan
{
    /// <summary>
    /// <see cref="Describe"/> 里最多列几条 `旧名 → 新名`（用户 2026-10-02：整组改名必须点名）。
    /// 超过它才折成「等 N 卷」—— 一行日志读得完，又不至于把名字全藏起来。
    /// </summary>
    private const int MaxNamesInDescribe = 3;

    /// <summary>能不能改（false 时看 <see cref="Reason"/>）。</summary>
    public bool CanRepair { get; init; }

    /// <summary>不能改的原因 / 能改时的补充说明（面向用户，可为空）。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>现在的完整路径。</summary>
    public string CurrentPath { get; init; } = string.Empty;

    /// <summary>现在的文件名。</summary>
    public string CurrentFileName { get; init; } = string.Empty;

    /// <summary>建议的文件名（例如 <c>Code Complete-BZ.7z.001</c>）。</summary>
    public string SuggestedFileName { get; init; } = string.Empty;

    /// <summary>改名后的完整路径。</summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>同目录里"像这一组后续卷"的文件名（只用来把话说清）。</summary>
    public IReadOnlyList<string> Siblings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 这一份计划**真的跑过试开**（硬链接 + 引擎列一次目录）。
    ///
    /// <para>为什么要标出来：内容级这条路以前"不成立就静默返回"，用户在日志里看不到任何痕迹 ——
    /// 2026-09-29 真机就是因此查不出断在哪（<c>ExtractionCoordinator.NormalizeDisguisedVolumeNamesAsync</c>）。
    /// 标了它，调用方才能只对"真跑过试开"的那些写一行结论，
    /// 而不会给每个普通压缩包都添一行（第 45 条的日志纪律：成功的任务只留一行）。</para>
    /// </summary>
    public bool TrialAttempted { get; init; }

    /// <summary>试开成立、但引擎读不出清单：这一组是"文件名也加密"的归档（7z <c>-mhe</c> / RAR <c>-hp</c>）。</summary>
    public bool ProbeNeedsPassword { get; init; }

    /// <summary>这一份计划里**有几卷是从别的目录收过来的**（0 = 全在同一层，与老行为一样）。</summary>
    public int GatheredVolumes { get; init; }

    /// <summary>
    /// 这一份"不能改"是**有结论的**：同目录里那些片全是满片、其中一片开头就是跨盘标记
    /// ⇒ 这是一组跨盘 zip，缺的是**末片**（用户要的"说清缺的是第几片"）。
    ///
    /// <para>为什么单独立一位：调用方只对"值得说一句"的结论写日志（第 45 条：成功的任务只留一行），
    /// 而这个结论与"试开跑过"是两件事 —— 它一次引擎都没调，但用户必须看到
    /// "缺的是末片"而不是含糊的「分卷缺失」。</para>
    /// </summary>
    public bool SpannedTailMissing { get; init; }

    /// <summary>
    /// 这一份"不能改"是**字节数那条硬证据**给的结论：7z 起始头自述的整包字节数与手上这几卷对不上
    /// （缺多少 / 多出多少都如实写在 <see cref="Reason"/> 里），所以**一次引擎都没调**。
    ///
    /// <para>为什么单独立一位（2026-10-03 阶段 B）：调用方按"值不值得说一句"决定写不写日志
    /// （第 45 条），而这一档的结论是用户最需要看见的那句"还差多少字节" —— 它既不是
    /// <see cref="TrialAttempted"/>（⛔ 一次引擎都没调，不许说成"试过"），也不是
    /// <see cref="SpannedTailMissing"/>（那是跨盘 zip 的事）。⛔ 它只影响**怎么写这句话**，
    /// 不参与任何删除 / 搬运的判据（方案 §4 阶段 B 的 (d)）。</para>
    /// </summary>
    public bool ByteBudgetMismatch { get; init; }

    /// <summary>
    /// 这次要改的**每一卷**（用户 2026-09-28 追加：网盘给整组的名字都缀了「删除」，
    /// 只改第一卷没用 —— 7-Zip 找 `.002` 时名字对不上，照样报缺卷）。
    ///
    /// <para>老调用方读上面那四个单文件属性（= 第一项），行为与以前逐字一样；
    /// 「修复分卷名并重试」按本列表一次把整组改干净。</para>
    /// </summary>
    public IReadOnlyList<VolumeRepairItem> Items { get; init; } = Array.Empty<VolumeRepairItem>();

    /// <summary>
    /// 一行给人看：**每一卷都点名**（`旧名 → 新名`）—— 列全 / 折起来都写出这一组共几卷。
    ///
    /// <para><b>为什么必须逐条点名</b>（用户 2026-10-02 真机）：老写法只报第一项、其余折成
    /// 「等 N 卷」，于是真机日志里那一行是
    /// 「风景01.7z：分卷名不标准，已按标准名改好（2 卷……）：风景01.7z → 风景01.7z.001 等 2 卷」
    /// —— 另一卷原本叫 **`风景02.mp4`**（被改成 `风景01.7z.002`），日志里**一个字都查不到**，
    /// 而它随后被彻底删除了。用户对不上账："我的风景02.mp4 去哪了"。</para>
    ///
    /// <para>规则：≤ <see cref="MaxNamesInDescribe"/> 项**全列**（末尾写「（共 N 卷）」）；
    /// 更多时只列前 <see cref="MaxNamesInDescribe"/> 条 + 「等 N 卷」（N 仍是**总卷数**，
    /// 与老口径一致 —— 绝不静默截断、也不假称只有列出来的那几卷）。</para>
    /// </summary>
    public string Describe()
    {
        if (Items.Count <= 1)
        {
            return $"{CurrentFileName} → {SuggestedFileName}";
        }

        IEnumerable<VolumeRepairItem> listed = Items.Count > MaxNamesInDescribe
            ? Items.Take(MaxNamesInDescribe)
            : Items;

        string body = string.Join(
            "；",
            listed.Select(item => $"{item.CurrentFileName} → {item.SuggestedFileName}"));

        return Items.Count > MaxNamesInDescribe
            ? $"{body}；等 {Items.Count} 卷"
            : $"{body}（共 {Items.Count} 卷）";
    }
}
