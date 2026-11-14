using System.Windows;

namespace ArchiveFixer.Services;

/// <summary>
/// 确认类对话框（从 <see cref="DialogService"/> 按职责拆出的协作类）：
/// 是 / 否（可带"不再提示"勾选框）、解压前提醒、是 / 否 / 取消。
///
/// <para>这一档的共同点是**要一个答案**，所以全部走值返回型派发
/// （<see cref="DialogUiThread.RunValueDialog{T}"/>：无界面宿主 / 超时 / 显示失败一律返回
/// <c>false</c> 或 <see cref="MessageBoxResult.Cancel"/>，破坏性分支不会被误执行）。</para>
///
/// <para>按钮文案、图标、默认按钮与勾选框语义逐字保留：这里只搬位置，不重新解释。
/// 唯一的例外是"解压前提醒"——它的降级方向**故意相反**（没人点 = 继续），见该方法的说明。</para>
/// </summary>
internal static class ConfirmationDialogPresenter
{
    /// <summary>
    /// 确认对话框（是 / 否），可带一个"不再提示"类的勾选框。
    /// 无 UI 宿主时返回 false（默认"不确认"，任何破坏性分支都不会被执行）。
    /// </summary>
    /// <param name="message">正文。</param>
    /// <param name="optionText">可选项位文案；空 = 不显示勾选框。</param>
    /// <param name="optionCheckedByDefault">勾选框初始状态。</param>
    /// <param name="detail">附加明细（明文密码清单等）。空 = 与不带明细的那一版完全一致。</param>
    /// <param name="optionChecked">用户最终是否勾选（未确认时恒为 false）。</param>
    internal static bool ShowConfirm(
        string message,
        string optionText,
        bool optionCheckedByDefault,
        string detail,
        out bool optionChecked)
    {
        bool checkedState = optionCheckedByDefault;

        var request = new AppDialogRequest
        {
            Title = "确认",
            Message = message,
            Detail = detail ?? string.Empty,
            Icon = AppDialogIcon.Question,
            Buttons = AppDialogButtons.YesNo,
            YesText = "确定",
            NoText = "取消",
            OptionText = optionText,
            OptionChecked = optionCheckedByDefault
        };

        bool confirmed = DialogUiThread.RunValueDialog(
            request,
            window =>
            {
                checkedState = window.IsOptionChecked;
                return window.Result == MessageBoxResult.Yes;
            },
            "ShowConfirm",
            fallback: false);

        optionChecked = confirmed && checkedState;
        return confirmed;
    }

    /// <summary>
    /// 「解压前的提醒」专用确认框：标题 / 两个按钮 / 勾选项的文案全部由调用方给
    /// （文案的唯一来源是 <c>StatusText</c>），而且**无 UI 宿主时按「继续」处理**
    /// （<c>fallback: true</c>）。
    ///
    /// <para><b>为什么降级方向与 <see cref="ShowConfirm(string, string, bool, string, out bool)"/> 正好相反</b>：那一个的语义是
    /// "要不要执行一个有副作用的动作"，没人点就必须按"不做"处理；而这里只是一条**纯提示**，
    /// 它拦住的是一整批解压。降级成"取消"的后果是所有无界面宿主（单元测试、控制台宿主、
    /// 被别的程序调起的管线）一启动就被这条提醒拦死，而且没人看得到原因 —— 那才是真正的事故。
    /// 所以这里显式定义成"没人点 = 继续"。</para>
    ///
    /// <para>勾选项在无界面宿主下恒为 false："本次运行不再提示"是一个**用户意图**，
    /// 程序不许替用户勾上。</para>
    /// </summary>
    /// <param name="title">标题（空 = 用 <see cref="AppDialogRequest"/> 的默认标题）。</param>
    /// <param name="yesText">主按钮文案（写动作本身："继续处理"）。</param>
    /// <param name="noText">次按钮文案（"先不处理"）。</param>
    /// <param name="optionChecked">用户最终是否勾选（未确认 / 无界面宿主时按复选框状态回传）。</param>
    internal static bool ShowReminderConfirm(
        string title,
        string message,
        string yesText,
        string noText,
        string optionText,
        bool optionCheckedByDefault,
        out bool optionChecked)
    {
        bool checkedState = optionCheckedByDefault;

        var request = new AppDialogRequest
        {
            Message = message,
            Icon = AppDialogIcon.Question,
            Buttons = AppDialogButtons.YesNo,
            YesText = yesText,
            NoText = noText,
            OptionText = optionText ?? string.Empty,
            OptionChecked = optionCheckedByDefault
        };

        if (!string.IsNullOrWhiteSpace(title))
        {
            request.Title = title;
        }

        bool confirmed = DialogUiThread.RunValueDialog(
            request,
            window =>
            {
                checkedState = window.IsOptionChecked;
                return window.Result == MessageBoxResult.Yes;
            },
            "ShowReminderConfirm",
            fallback: true);

        /*
         * ⛔ `optionChecked` 必须是**复选框本身的状态**，与用户按了哪个按钮无关（2026-09-28 真机）。
         *
         * 老写法 `optionChecked = confirmed && checkedState`：本对话框的语义是
         * 「是 = 保留全部（知道了）/ 否 = 从列表里移除这些」，用户**点了"从列表里移除"并勾上
         * "以后不再提醒"** 时 `confirmed == false`，勾选状态被这一句直接丢掉 →
         * `RemindJunkAfterImport` 永远回不到 false，下次导入又弹一遍。
         *
         * 真机证据：日志有 `已按提醒里的选择，把 N 个无用物从任务列表里移除`，
         * 但**没有**那条"已记住不再提醒"的日志；`appsettings.json` 里 `RemindJunkAfterImport` 始终是 true。
         *
         * ⚠ 只改这一处：另外三个（ShowConfirm / ShowDestructiveConfirm…）的 Yes 是"动手那个"，
         * 用户选否时勾选状态本来就没有意义，保持原样。
         */
        optionChecked = checkedState;
        return confirmed;
    }

    /// <summary>
    /// 是 / 否 / 取消对话框。
    /// 无 UI 宿主时返回 <see cref="MessageBoxResult.Cancel"/>（最保守的一档）。
    /// </summary>
    internal static MessageBoxResult ShowYesNoCancel(string message)
    {
        return DialogUiThread.RunValueDialog(
            new AppDialogRequest
            {
                Title = "确认",
                Message = message,
                Icon = AppDialogIcon.Question,
                Buttons = AppDialogButtons.YesNoCancel
            },
            window => window.Result,
            "ShowYesNoCancel",
            MessageBoxResult.Cancel);
    }
}
