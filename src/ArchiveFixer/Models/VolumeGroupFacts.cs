using System.Collections.Generic;

namespace ArchiveFixer.Models;

/// <summary>
/// 这一单作为**一组分卷**的事实（AGENTS.md §9.3）：是不是组、归组键、整组卷序、缺卷、未解组、说明文字。
///
/// <para>从 <see cref="ArchiveTask"/> 抽出的协作类（「卷组」那一块）：一组分卷 = 一个任务，
/// 只从第一卷启动解压，其余卷不作为独立任务出现；<see cref="Paths"/> 的排序与内容由归组服务写，
/// 本类只回答"几卷 / 缺不缺 / 那句话该怎么显示"。</para>
///
/// <para><b>失败模型</b>：没有归组 / 判不出 ⇒ 保持空表与 <c>false</c>（删除闸门不得 fail-open），
/// 没有 IO、没有异常路径。</para>
///
/// <para>生命周期：随任务创建；识别之后由归组服务写入，收场之后由删除闸门 / 第二大步读取。
/// <see cref="Paths"/> / <see cref="MissingNames"/> 是**同一个列表实例**（调用方在拆分前就拿着它加/清），
/// ⛔ 这里不复制、不排序、不改写。</para>
/// </summary>
internal sealed class VolumeGroupFacts
{
    /// <summary>本任务是否代表"一组分卷"（AGENTS.md §9.3）。</summary>
    internal bool IsGroup { get; set; }

    /// <summary>分卷组的归组键（目录 + 基名）。失败重试与清理源包时靠它找回同一组。</summary>
    internal string GroupKey { get; set; } = string.Empty;

    /// <summary>这一组已找到的分卷文件（含第一卷），按卷序排列。</summary>
    internal List<string> Paths { get; } = new();

    /// <summary>卷序是否从 1 连续、无缺口（未知/未归组 = false，删除闸门不得 fail-open）。</summary>
    internal bool IsComplete { get; set; }

    /// <summary>缺失的卷文件名（总数无法确定时为空）。</summary>
    internal List<string> MissingNames { get; } = new();

    /// <summary>这一单的产物里还有未完成的分卷组，最终成功前不许删源。</summary>
    internal bool HasUnresolvedGroup { get; set; }

    /// <summary>分卷情况的一句话说明，直接显示给用户。</summary>
    internal string InfoText { get; set; } = string.Empty;

    /// <summary>已找到的卷数。</summary>
    internal int Count => Paths.Count;

    /// <summary>
    /// 「分卷」那一列**显示**什么（用户 2026-10-07：三列过期里点名的「分卷」—— 已经解开整组的行
    /// 上还留着扫描那一刻的「共 1 卷，缺 111.zip、111.z01…」）。
    ///
    /// <para>判据只用**结构化事实**：这一片被整组接手过（<c>settledWithGroupUnitName</c> 非空，
    /// 收场那一刻记下的）或它本身就是跟班卷、且已按"完成"收场 ⇒ 那一组已经不缺卷了，如实说一声。
    /// ⛔ 不重跑归组、⛔ 不动 <see cref="Paths"/>（归组结果是收场判据的输入）、⛔ 不比中文文案。</para>
    /// </summary>
    internal string DisplayInfoText(string settledWithGroupUnitName, bool isVolumeGroupFollower, TaskOutcome outcome) =>
        settledWithGroupUnitName.Length > 0
        || (isVolumeGroupFollower && outcome == TaskOutcome.Succeeded)
            ? StatusText.VolumeInfoSettledWithGroupText
            : InfoText;
}
