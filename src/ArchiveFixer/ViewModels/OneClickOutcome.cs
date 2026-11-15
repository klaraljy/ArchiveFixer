using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 一键处理的结果。
///
/// 为什么要一个对象而不是只返回那行汇总：测试要能直接断言"跑了几轮、停在哪"，
/// 拿中文汇总去 <c>Contains</c> 只是把文案当接口，文案一改测试就假红/假绿。
/// </summary>
internal sealed class OneClickOutcome
{
    /// <summary>实际跑了几轮（1 = 只有第一层，也就是最常见的情况）。</summary>
    public int Rounds { get; init; }

    /// <summary>自动续解了几层（正常跑完时 = 轮数 - 1）。</summary>
    public int ContinuationLayers { get; init; }

    /// <summary>是不是被「停止后续」打断的（被打断就不许显示成成功）。</summary>
    public bool Stopped { get; init; }

    /// <summary>是不是撞到轮数硬上限、还有更深的内层包没解。</summary>
    public bool HitRoundLimit { get; init; }

    /// <summary>
    /// 撞上限时**还剩多少个内层包没解**（已经加进任务列表并勾好，等用户点「继续解」）。
    ///
    /// <para>用户 2026-09-24 第 16 条追加："你为什么只弄了两层，我要的一键解压时多重解压……都到最后一步了还没成功"。
    /// 到顶**必须提示还剩多少 + 给一键继续**，不许静默停下 —— 这个数字就是那句提示与那个按钮的依据。</para>
    /// </summary>
    public int PendingContinuationCount { get; init; }

    /// <summary>一行汇总（已经写进日志，GUI 用它弹提示）。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// 这一批的**汇总严重度**（用户 2026-09-29 第 3 条：批末那个框要按结果分颜色）。
    ///
    /// <para>判定只读机器终态（<see cref="ArchiveTask.Outcome"/> / 校验枚举），
    /// 唯一出口是 <see cref="BatchSummarySeverityRules.FromTasks"/>；
    /// 这里只是把它**带出来**，供 GUI 选蓝 / 橙 / 红那条色带 ——
    /// ⛔ 别在窗口里按 <see cref="Summary"/> 这段中文再判断一次。</para>
    /// </summary>
    public BatchSummarySeverity Severity { get; init; } = BatchSummarySeverity.Success;

    /// <summary>
    /// **出错在哪**那份清单（用户 2026-09-30 第 1 条）：按机器终态 / 错误分类分组，
    /// 逐组点名具体包名；空字符串 = 这一批没有任何"没做成"的事。
    ///
    /// <para>它与 <see cref="Severity"/> 出自**同一次**调用
    /// （<see cref="BatchSummaryDiagnosticsRules.Build"/>，见 <see cref="RunPipelineAsync"/> 里那一行）：
    /// 严重度管颜色、诊断管文字，两者是同一份任务终态算出来的，⛔ 不是各算一遍。</para>
    /// </summary>
    public string Diagnostics { get; init; } = string.Empty;

    /// <summary>
    /// 上面那份清单的**逐行**形态（组行 + 「下一步」那一行，不含标题）：日志一行一条写它。
    ///
    /// <para>与 <see cref="Diagnostics"/> 出自同一个 <see cref="BatchSummaryReport"/> ——
    /// 框里摆的那份文字与日志里写的那几行逐字一致（用户 2026-09-30 第 1 条的排障前提）。</para>
    /// </summary>
    public IReadOnlyList<string> DiagnosticLines { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 批末那个框的**正文** = 一行汇总 + （有问题时）诊断清单。
    ///
    /// <para>日志仍用 <see cref="Summary"/> 那一行（一条一行好搜）；摆给用户看的用这一份 ——
    /// 他原话："你和我这总会仔细看日志，但是用户不会，他们只想看看错误出在哪里"。</para>
    /// </summary>
    public string DialogMessage => string.IsNullOrWhiteSpace(Diagnostics)
        ? Summary
        : Summary + Environment.NewLine + Environment.NewLine + Diagnostics;
}

