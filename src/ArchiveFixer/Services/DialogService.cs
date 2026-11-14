using ArchiveFixer.Packing;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ArchiveFixer.Services;

/// <summary>
/// 对话框服务（公开 API 的**薄转发**）。
///
/// <para>按职责拆成协作类，本类只保留签名、语义与线程契约：</para>
/// <list type="bullet">
/// <item><description>文件 / 文件夹选择 → <see cref="FileDialogPresenter"/>（原生 <c>Microsoft.Win32</c> 对话框）。</description></item>
/// <item><description>信息 / 警告 / 错误 / 异常 / 批末汇总 / 空间提示 → <see cref="InformationDialogPresenter"/>。</description></item>
/// <item><description>确认 / 解压前提醒 / 是-否-取消 → <see cref="ConfirmationDialogPresenter"/>。</description></item>
/// <item><description>危险确认（实心红主按钮） → <see cref="DestructiveConfirmPresenter"/>。</description></item>
/// <item><description>同名冲突六档询问 → <see cref="ConflictDecisionPresenter"/>。</description></item>
/// <item><description>打包确认 / 密码选单 → <see cref="AuxiliaryDialogPresenter"/>。</description></item>
/// <item><description>无 UI 宿主降级（测试替身那一档） → <see cref="DialogFallbackLog"/>；
/// 线程模型 → <see cref="DialogUiThread"/>；窗口创建与显示 → <see cref="AppDialogWindowHost"/>。</description></item>
/// </list>
///
/// <para><b>线程模型（硬约束，历史上"卡死"就出在这里）</b>：</para>
/// <list type="bullet">
/// <item><description><b>无 UI 宿主</b>（单元测试、控制台宿主）：一律降级为"写降级日志 + 返回默认值"，
/// 绝不抛异常、绝不等待 —— 过去直接调 <c>MessageBox.Show</c> 在没有 WPF 宿主的进程里
/// 会抛 <see cref="InvalidOperationException"/>，把一次"提示"变成一次任务失败。</description></item>
/// <item><description><b>UI 线程调用</b>：直接 <c>ShowDialog()</c>。它内部是嵌套消息泵，界面照常刷新、不会死锁。</description></item>
/// <item><description><b>后台线程调用</b>：值返回的方法用 <c>Dispatcher.InvokeAsync</c>（**不是**同步
/// <c>Dispatcher.Invoke</c>）派发，并带**有界等待**；通知类方法用 <c>BeginInvoke</c> 投递即返回，
/// 不占用后台线程。任何一步失败都只写日志，不向上抛。</description></item>
/// </list>
///
/// <para>公开方法签名保持不变（调用方很多）；<c>virtual</c> 的位置也保持不变 ——
/// 测试靠 override 注入"用户点了确定 / 取消""用户选好了哪个文件"，
/// 这一档是无界面宿主里唯一能端到端钉住真实路径的办法。</para>
/// </summary>
public class DialogService
{
    /// <summary>无 UI 宿主时是否把降级记录写进 <c>data\logs\dialog-fallback.log</c>。</summary>
    public static bool FallbackFileLoggingEnabled
    {
        get => DialogFallbackLog.FileLoggingEnabled;
        set => DialogFallbackLog.FileLoggingEnabled = value;
    }

    /// <summary>本进程内发生过的降级记录（按发生顺序，最近的在后）。测试与排障用。</summary>
    public static IReadOnlyList<string> FallbackLog => DialogFallbackLog.Snapshot;

    /// <summary>清空降级记录（测试用）。</summary>
    public static void ClearFallbackLog() => DialogFallbackLog.Clear();

    // ------------------------------------------------------------------ 文件对话框

    /// <summary>
    /// 打开文件选择对话框。
    /// 支持多选。
    /// </summary>
    public List<string> ShowOpenFileDialog() => FileDialogPresenter.ShowOpenFileDialog();

    /// <summary>
    /// 打开单文件选择对话框。
    ///
    /// <para><c>virtual</c> 是为了让测试注入"用户选好了哪个文件"：无界面宿主下本方法一律返回空串
    /// （等于用户没选），「没有配置密码本路径 → 问一次选哪个 txt」这条分支就永远走不下去。</para>
    /// </summary>
    public virtual string ShowOpenSingleFileDialog(
        string title = "选择文件",
        string filter = "所有文件 (*.*)|*.*")
    {
        return FileDialogPresenter.ShowOpenSingleFileDialog(title, filter);
    }

    /// <summary>
    /// 选择文件夹。
    ///
    /// <para><c>virtual</c> 与 <see cref="ShowOpenSingleFileDialog"/> 同理（2026-09-25 第 31 条）：
    /// 无界面宿主下本方法一律返回空串，测试注入不了"用户挑好了哪个目录"，
    /// 于是①页「选择…」那条**真实路径**没法被端到端钉住 —— 而"两处落点不同步"的缺陷正是从这条路进来的。</para>
    /// </summary>
    public virtual string ShowFolderBrowserDialog() => ShowFolderBrowserDialog("选择文件夹", string.Empty);

    /// <summary>
    /// 带标题 / 初始目录的那一版（⑤打包页「选择…」用：标题要说清是"最终压缩包放在哪"）。
    ///
    /// <para>⚠ 这两版**必须是同一条路**（①页与⑤页各调一版）：无界面宿主下都返回空串，
    /// 测试只要 override 这一版就能钉住两条"选择…"的真实路径。</para>
    /// </summary>
    public virtual string ShowFolderBrowserDialog(string title, string initialDirectory)
        => FileDialogPresenter.ShowFolderBrowserDialog(title, initialDirectory);

    /// <summary>
    /// **打包的小确认弹窗**（用户 2026-09-26 第 46 条）：只显示"最终压缩包放在哪 + 原包操作 + 其余物操作"。
    ///
    /// <para>返回用户确认后的那一套选择；用户取消时返回 <c>null</c>（⛔ 一个字节都不许提前动）。
    /// <c>virtual</c> 与 <see cref="ShowFolderBrowserDialog()"/> 同理：无界面宿主下返回 <c>null</c>，
    /// 测试注入不了"他点了哪几档"——而"选了什么就必须照着做什么"这件事必须能被端到端钉住。</para>
    /// </summary>
    public virtual PackingRunOptions? ShowPackingConfirm(PackingConfirmRequest request)
        => AuxiliaryDialogPresenter.ShowPackingConfirm(request);

    /// <summary>
    /// 「从密码列表里选」的小窗口（用户 2026-09-26 第 46 条）。
    /// 返回选中那条；取消 / 列表为空时返回空串。⛔ 不做任何记账、不写日志。
    /// </summary>
    public virtual string ShowPasswordPicker(string title, IReadOnlyList<string> passwords)
        => AuxiliaryDialogPresenter.ShowPasswordPicker(title, passwords);

    /// <summary>
    /// 选择多个文件夹。
    /// </summary>
    public List<string> ShowMultiFolderBrowserDialog() => FileDialogPresenter.ShowMultiFolderBrowserDialog();

    /// <summary>
    /// 选择保存文件路径。
    /// </summary>
    /// <param name="initialDirectory">
    /// 起始目录（可空 = 系统默认的"上次用过的那个文件夹"）。用户 2026-10-01 真机 `giu910`：
    /// 上次那个文件夹正好是**其余物**（源包被收进去过），日志就落在了那个"程序自己会整份删掉"的地方 ——
    /// 所以调用方现在会传"上次成功导出到哪儿"，并保证它不在其余物 / 工作区里。
    /// </param>
    public string ShowSaveFileDialog(
        string title = "保存文件",
        string filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
        string defaultFileName = "",
        string? initialDirectory = null)
    {
        return FileDialogPresenter.ShowSaveFileDialog(title, filter, defaultFileName, initialDirectory);
    }

    // ------------------------------------------------------------------ 提示类对话框

    /// <summary>
    /// 确认对话框（是 / 否）。
    /// 无 UI 宿主时返回 false（默认"不确认"，任何破坏性分支都不会被执行）。
    /// </summary>
    public bool ShowConfirm(string message) => ShowConfirm(message, string.Empty, false, out _);

    /// <summary>
    /// 带"不再提示"一类的可选项位的确认对话框。
    /// </summary>
    /// <param name="message">正文。</param>
    /// <param name="optionText">可选项位文案；空 = 不显示勾选框。</param>
    /// <param name="optionCheckedByDefault">勾选框初始状态。</param>
    /// <param name="optionChecked">用户最终是否勾选。</param>
    public bool ShowConfirm(
        string message,
        string optionText,
        bool optionCheckedByDefault,
        out bool optionChecked)
    {
        return ShowConfirm(
            message,
            optionText,
            optionCheckedByDefault,
            detail: string.Empty,
            out optionChecked);
    }

    /// <summary>
    /// 与上一个重载同一件事，但可以多带一段 <b>Detail</b>（对话框里那块等宽、可滚动、可复制的区域）。
    ///
    /// <para><b>为什么需要它</b>：「写回密码本…」的确认框必须**逐条列出将要写入的密码明文**
    /// （用户 2026-09-24 明确要求，界面上给他看是应该的），但正文 <c>Message</c> 会被无界面宿主的
    /// 降级日志原样记下来；明文密码只能走 Detail —— 它只显示、不记日志。
    /// 顺带的好处是长清单不撑高对话框（Detail 区有自己的高度上限 + 滚动条）。</para>
    ///
    /// <para><c>virtual</c> 是为了让测试能注入"用户点了确定 / 点了取消"的假对话框：
    /// 无界面宿主下本方法一律返回 false（没人点过 = 不执行），写回那条路就永远走不到真正写入，
    /// 端到端也就无从验证。</para>
    /// </summary>
    /// <param name="detail">附加明细（明文密码清单等）。空 = 与上一个重载完全一致。</param>
    /// <param name="optionChecked">用户最终是否勾选。</param>
    public virtual bool ShowConfirm(
        string message,
        string optionText,
        bool optionCheckedByDefault,
        string detail,
        out bool optionChecked)
    {
        return ConfirmationDialogPresenter.ShowConfirm(
            message,
            optionText,
            optionCheckedByDefault,
            detail,
            out optionChecked);
    }

    /// <summary>
    /// 「解压前的提醒」专用确认框：标题 / 两个按钮 / 勾选项的文案全部由调用方给
    /// （文案的唯一来源是 <c>StatusText</c>），而且**无 UI 宿主时按「继续」处理**。
    ///
    /// <para>降级方向与 <see cref="ShowConfirm(string, string, bool, out bool)"/> 故意相反：
    /// 那一个的语义是"要不要执行一个有副作用的动作"，没人点就必须按"不做"处理；
    /// 而这里只是一条**纯提示**，它拦住的是一整批解压 —— 降级成"取消"会让所有无界面宿主一启动就被拦死，
    /// 而且没人看得到原因。所以这里显式定义成"没人点 = 继续"。</para>
    /// </summary>
    /// <param name="title">标题（空 = 用 <see cref="AppDialogRequest"/> 的默认标题）。</param>
    /// <param name="yesText">主按钮文案（写动作本身："继续处理"）。</param>
    /// <param name="noText">次按钮文案（"先不处理"）。</param>
    /// <param name="optionChecked">用户最终是否勾选（按复选框本身的状态回传）。</param>
    public virtual bool ShowReminderConfirm(
        string title,
        string message,
        string yesText,
        string noText,
        string optionText,
        bool optionCheckedByDefault,
        out bool optionChecked)
    {
        return ConfirmationDialogPresenter.ShowReminderConfirm(
            title,
            message,
            yesText,
            noText,
            optionText,
            optionCheckedByDefault,
            out optionChecked);
    }

    /// <summary>
    /// 是 / 否 / 取消对话框。
    /// 无 UI 宿主时返回 <see cref="MessageBoxResult.Cancel"/>（最保守的一档）。
    /// </summary>
    public MessageBoxResult ShowYesNoCancel(string message)
        => ConfirmationDialogPresenter.ShowYesNoCancel(message);

    /// <summary>
    /// 危险操作的二次确认：主按钮用实心红，且文案写清动作本身（"清空""删除"），
    /// 而不是含糊的"是"。默认按不确认处理。
    /// </summary>
    /// <param name="message">正文，建议写清"会发生什么、能不能撤销"。</param>
    /// <param name="confirmText">主按钮文案。</param>
    public bool ShowDestructiveConfirm(string message, string confirmText = "确定")
        => DestructiveConfirmPresenter.ShowDestructiveConfirm(message, confirmText);

    /// <summary>
    /// 危险操作的**二次确认**（红色 + 勾选框）。
    ///
    /// 为什么需要它：规格 §3.2 的"彻底删除"档要求同时满足"界面红色标识"与"二次确认（勾选/输入）"，
    /// 而这两件事在原来的两个 API 里各占一半：只带红色的确认只有红，只带勾选框的确认只有勾选框。
    /// 少任何一半都不满足规格：只有红色 = 一次点掉就永久删除；只有勾选框 = 危险动作看起来像普通询问。
    /// </summary>
    /// <param name="message">正文，写清"会发生什么、能不能撤销"。</param>
    /// <param name="confirmText">主按钮文案（写动作本身："彻底删除"）。</param>
    /// <param name="optionText">勾选框文案（"我知道不可恢复"）。</param>
    /// <param name="optionChecked">用户最终是否勾选（返回 false 时它恒为 false）。</param>
    /// <returns>用户是否确认（返回值与 <see cref="ShowConfirm(string, string, bool, out bool)"/> 同一口径）。</returns>
    public bool ShowDestructiveConfirm(
        string message,
        string confirmText,
        string optionText,
        out bool optionChecked)
    {
        return DestructiveConfirmPresenter.ShowDestructiveConfirm(
            message,
            confirmText,
            optionText,
            out optionChecked);
    }

    /// <summary>
    /// 危险操作确认 + **可选的**危险档勾选框（用户 2026-09-22 拍板的"其余物删除确认用勾选"）。
    ///
    /// <para>与 <see cref="ShowDestructiveConfirm(string, string, string, out bool)"/> 的区别只有一处，
    /// 但很关键：<b>不勾选也能确认</b>。语义是"默认走安全档（移入回收站），勾上才切到激进档"——
    /// 勾选框在这里是**档位选择**，不是"必须承认才能继续"的确认位。</para>
    /// <para>激进档自己的二次确认（红色 + "我知道不可恢复"必勾）由调用方在拿到
    /// <paramref name="optionChecked"/> == true 之后再弹一次，本方法不代劳。</para>
    /// </summary>
    /// <param name="optionChecked">用户最终是否勾选（未确认时恒为 false）。</param>
    public bool ShowDestructiveConfirmWithOption(
        string message,
        string confirmText,
        string optionText,
        bool optionCheckedByDefault,
        out bool optionChecked)
    {
        return DestructiveConfirmPresenter.ShowDestructiveConfirmWithOption(
            message,
            confirmText,
            optionText,
            optionCheckedByDefault,
            out optionChecked);
    }

    // ------------------------------------------------------------------ 同名冲突询问（Ask 档）

    /// <summary>
    /// 同名冲突询问的内容。由调用方（解压管线）按现场拼好，本类只负责"怎么显示、怎么拿答案"。
    /// </summary>
    public sealed class ConflictPrompt
    {
        /// <summary>正文：说清"有几个冲突、你要选什么"。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>标题下面那行小字（"本批只会问你这一次"）。</summary>
        public string Subtitle { get; init; } = string.Empty;

        /// <summary>冲突清单（等宽显示，可复制）—— 用户得先看清撞上的是什么。</summary>
        public string Detail { get; init; } = string.Empty;

        /// <summary>主按钮「覆盖」是不是危险动作（true → 实心红）。</summary>
        public bool Destructive { get; init; } = true;
    }

    /// <summary>
    /// 同名冲突询问：**一次问清"这一个 / 整批"怎么处理**（照 WinRAR 六档的精神，按批量场景裁剪）。
    ///
    /// <para>六个选项 = 三个按钮（覆盖 / 跳过 / 自动重命名）× 勾选框两态（这一个 / 全部）；
    /// 直接关掉窗口（标题栏 ✕）= **取消本批**。</para>
    /// <para>返回 <c>null</c> 的语义是"没问到答案"（无界面宿主 / 被取消 / 超时）：
    /// 调用方**必须**按保守档处理，绝不允许把它当成"用户同意覆盖"。</para>
    /// </summary>
    public virtual Task<ConflictDecision?> ShowConflictDecisionAsync(
        ConflictPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        return ConflictDecisionPresenter.ShowAsync(prompt, cancellationToken);
    }

    /// <summary>
    /// 六档询问的**答案映射**（唯一实现在 <see cref="ConflictDecisionPresenter"/>；抽成纯函数是为了能脱离窗口单测）。
    ///
    /// 六个选项 = 三个按钮 × 勾选框两态：
    /// Yes=覆盖、No=跳过、Cancel=自动重命名，勾选框 = 对整批都照此办理；
    /// ✕ 关窗（<paramref name="dialogResult"/> 为 null）= **取消本批**，勾选框不改变这个结论。
    /// </summary>
    internal static ConflictDecision MapConflictAnswer(
        MessageBoxResult result,
        bool? dialogResult,
        bool applyToAll)
    {
        return ConflictDecisionPresenter.MapAnswer(result, dialogResult, applyToAll);
    }

    // ------------------------------------------------------------------ 提示类对话框（续）

    /// <summary>
    /// **批末汇总**（用户 2026-09-29：这个框要按结果分颜色）。
    ///
    /// <para>与 <see cref="ShowInfo"/> 的唯一区别是那个 <paramref name="severity"/>：
    /// 窗口按它在顶部放一条色带（蓝 = 全成功 / 橙 = 有部分完成或跳过 / 红 = 有失败），
    /// 正文照旧白底黑字。判定**不在这里**（⛔ 对话框不许读中文状态去猜），
    /// 它来自 <see cref="ArchiveFixer.Models.BatchSummarySeverityRules"/> 那一个出口。</para>
    ///
    /// <para><c>virtual</c> 与其他可注入对话框同理：测试要能直接断言"弹的是哪一档"，
    /// 而不是只能读降级日志。</para>
    /// </summary>
    public virtual void ShowBatchSummary(string message, ArchiveFixer.Models.BatchSummarySeverity severity)
        => InformationDialogPresenter.ShowBatchSummary(message, severity);

    /// <summary>
    /// **中途因空间不足被拦下**的那一次提示（用户 2026-09-29 第 2 条）。
    ///
    /// <para>它是**纯提示、一个按钮、不阻塞后续任务**：一键处理的红线是"批中间不许要用户点一下
    /// 才能继续"，所以走**非模态**窗口（<c>Show()</c>）——窗口浮在那儿让他看得见，批一秒都不停。
    /// 调用方负责"同一批只弹一次"（合并计数）：这里只管弹，不判断该不该弹。</para>
    /// </summary>
    public void ShowSpaceBlockedNotice(string message)
        => InformationDialogPresenter.ShowSpaceBlockedNotice(message);

    /// <summary>
    /// 信息提示。
    ///
    /// <para><c>virtual</c> 与其他可注入对话框同理：测试要能直接断言"弹的是哪一档"，
    /// 而不是只能读降级日志（模态框还会把用例卡死）。</para>
    /// </summary>
    public virtual void ShowInfo(string message) => InformationDialogPresenter.ShowInfo(message);

    /// <summary>
    /// 警告提示。
    /// </summary>
    public void ShowWarning(string message) => InformationDialogPresenter.ShowWarning(message);

    /// <summary>
    /// **空间体检**那一个警告框（用户 2026-09-27："还可以弄一个弹窗，因为这个比较危险"）。
    ///
    /// <para>与 <see cref="ShowWarning(string)"/> 的区别只有一个，但很关键：正文之外还给一块
    /// <b>Detail</b>（等宽、可滚动、可复制）逐条列出"哪些包放不下、各差多少" ——
    /// 那些名字与数字塞进正文会把框撑高到看不完，而它们正是用户要照着清理的清单。</para>
    ///
    /// <para><c>virtual</c> 与其他可注入对话框同理：让测试能看见"到底弹了什么"，
    /// 无界面宿主（单元测试 / 控制台宿主）由降级出口记一条 —— 不弹窗、不死等。</para>
    /// </summary>
    public virtual void ShowSpaceShortageWarning(string message, string detail)
        => InformationDialogPresenter.ShowSpaceShortageWarning(message, detail);

    /// <summary>
    /// 错误提示。
    ///
    /// <para><c>virtual</c> 与其他可注入对话框同理：让测试能看见"到底报了哪一条错"，
    /// 而不是只能看降级日志。</para>
    /// </summary>
    public virtual void ShowError(string message) => InformationDialogPresenter.ShowError(message);

    /// <summary>
    /// 异常提示。
    ///
    /// ⚠ 正文与明细都过 <see cref="ArchiveFixer.Password.PasswordMasker.Sanitize"/>：异常文本可能夹带命令行片段
    /// （7z 的 <c>-p&lt;明文密码&gt;</c> 就写在参数里，异常消息经常把整条命令带出来），
    /// 弹窗与日志同一口径 —— 密码只存内存，任何出口都不得出现明文（AGENTS.md §6 第 5 条）。
    /// </summary>
    public void ShowException(Exception ex, string prefix = "发生异常")
        => InformationDialogPresenter.ShowException(ex, prefix);
}
