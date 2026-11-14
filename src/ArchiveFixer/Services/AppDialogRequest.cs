using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;


namespace ArchiveFixer.Services;

/// <summary>
/// 一次对话框请求。给 <see cref="AppDialogWindow"/> 用，同时也是
/// <see cref="ArchiveFixer.Services.DialogService"/> 与窗口之间的唯一契约。
/// </summary>
public sealed class AppDialogRequest
{
    public string Title { get; set; } = "提示";

    public string Message { get; set; } = string.Empty;

    /// <summary>标题下面那行小字（例如"此操作不可撤销"）。</summary>
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>附加细节（异常类型等），等宽小字 + 警示底色，默认不显示。</summary>
    public string Detail { get; set; } = string.Empty;

    public AppDialogIcon Icon { get; set; } = AppDialogIcon.Info;

    public AppDialogButtons Buttons { get; set; } = AppDialogButtons.Ok;

    /// <summary>
    /// 主操作（是/确定）是不是危险操作。
    /// true 时主按钮用实心红 —— 例如"清空密码列表""删除源压缩包"。
    /// </summary>
    public bool Destructive { get; set; }

    /// <summary>可选项位的文案（如"本次运行内不再询问"）。空 = 不显示该勾选框。</summary>
    public string OptionText { get; set; } = string.Empty;

    /// <summary>可选项位的初始勾选状态。</summary>
    public bool OptionChecked { get; set; }

    public string OkText { get; set; } = "确定";

    public string YesText { get; set; } = "是";

    public string NoText { get; set; } = "否";

    public string CancelText { get; set; } = "取消";

    /// <summary>
    /// 批末汇总的**严重度**（null = 普通对话框，一条色带都不显示）。
    ///
    /// <para>非 null 时窗口顶部放出一条约 30% 高的色带（蓝 / 橙 / 红），正文照旧白底黑字 ——
    /// 用户 2026-09-29 原话："我记得你是上30%的位置是蓝色，然后下面是白底，黑字"。
    /// 判定本身**不在这里**（⛔ 窗口不许按文案或状态自己猜），它来自
    /// <see cref="ArchiveFixer.Models.BatchSummarySeverityRules"/> 那一个出口。</para>
    /// </summary>
    public ArchiveFixer.Models.BatchSummarySeverity? SummarySeverity { get; set; }
}

