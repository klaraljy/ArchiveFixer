using ArchiveFixer.Password;
using ArchiveFixer.Views;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 对话框服务。
    ///
    /// 职责：
    /// 1. 封装打开文件 / 文件夹 / 保存文件对话框。
    /// 2. 封装信息、警告、错误、确认提示 —— 一律走自绘的
    ///    <see cref="AppDialogWindow"/>，不再用系统 MessageBox（灰底、字挤、正文无法复制）。
    ///
    /// 线程模型（这一条是硬约束，历史上"卡死"就出在这里）：
    /// · **无 UI 宿主**（单元测试、控制台宿主）：一律降级为"写降级日志 + 返回默认值"，
    ///   绝不抛异常、绝不等待 —— 过去直接调 <c>MessageBox.Show</c> 在没有 WPF 宿主的进程里
    ///   会抛 <see cref="InvalidOperationException"/>，把一次"提示"变成一次任务失败。
    /// · **UI 线程调用**：直接 <c>ShowDialog()</c>。它内部是嵌套消息泵，界面照常刷新、不会死锁。
    /// · **后台线程调用**：值返回的方法用 <c>Dispatcher.InvokeAsync</c>（**不是**同步
    ///   <c>Dispatcher.Invoke</c>）派发，并带**有界等待**；通知类方法用 <c>BeginInvoke</c> 投递即返回，
    ///   不占用后台线程。任何一步失败都只写日志，不向上抛。
    ///
    /// 公开方法签名保持不变（调用方很多）；只换实现，并额外提供一个带"不再提示"选项的重载。
    ///
    /// 另有一个专项询问：<see cref="ShowConflictDecisionAsync"/> —— 同名冲突的六档询问
    /// （覆盖 / 跳过 / 自动重命名 × 这一个 / 整批，✕ = 取消本批）。它同样遵守上面这套线程规则。
    /// </summary>
    public class DialogService
    {
        /// <summary>
        /// 后台线程等待用户点击对话框的上限。
        ///
        /// 为什么有上限：这个 API 是同步的（要拿用户的答案），后台线程必须等；
        /// 万一 UI 线程已经忙死或已开始关闭，**不能无限等**（那就是"死等"）。
        /// 超时后按"用户没有确认"处理并写日志 —— 与 ShowConfirm 返回 false 的语义一致。
        /// </summary>
        private static readonly TimeSpan BackgroundWaitTimeout = TimeSpan.FromMinutes(5);

        private static readonly object FallbackLock = new();
        private static readonly List<string> FallbackEntries = new();

        /// <summary>无 UI 宿主时是否把降级记录写进 <c>data\logs\dialog-fallback.log</c>。</summary>
        public static bool FallbackFileLoggingEnabled { get; set; } = true;

        /// <summary>本进程内发生过的降级记录（最近的在前）。测试与排障用。</summary>
        public static IReadOnlyList<string> FallbackLog
        {
            get
            {
                lock (FallbackLock)
                {
                    return FallbackEntries.ToArray();
                }
            }
        }

        /// <summary>清空降级记录（测试用）。</summary>
        public static void ClearFallbackLog()
        {
            lock (FallbackLock)
            {
                FallbackEntries.Clear();
            }
        }

        // ------------------------------------------------------------------ 文件对话框

        /// <summary>
        /// 打开文件选择对话框。
        /// 支持多选。
        /// </summary>
        public List<string> ShowOpenFileDialog()
        {
            return ShowFileDialog(
                () =>
                {
                    var dialog = new OpenFileDialog
                    {
                        Title = "选择文件",
                        Multiselect = true,
                        CheckFileExists = true,
                        CheckPathExists = true,
                        Filter =
                            "所有文件 (*.*)|*.*|" +
                            "压缩包 (*.zip;*.rar;*.7z;*.gz;*.bz2;*.xz;*.tar)|*.zip;*.rar;*.7z;*.gz;*.bz2;*.xz;*.tar|" +
                            "伪装常见文件 (*.jpg;*.png;*.pdf;*.mp4;*.txt)|*.jpg;*.jpeg;*.png;*.gif;*.pdf;*.mp4;*.txt"
                    };

                    return dialog.ShowDialog() == true
                        ? dialog.FileNames.ToList()
                        : new List<string>();
                },
                "ShowOpenFileDialog",
                new List<string>());
        }

        /// <summary>
        /// 打开单文件选择对话框。
        /// </summary>
        public string ShowOpenSingleFileDialog(
            string title = "选择文件",
            string filter = "所有文件 (*.*)|*.*")
        {
            return ShowFileDialog(
                () =>
                {
                    var dialog = new OpenFileDialog
                    {
                        Title = title,
                        Multiselect = false,
                        CheckFileExists = true,
                        CheckPathExists = true,
                        Filter = filter
                    };

                    return dialog.ShowDialog() == true ? dialog.FileName : string.Empty;
                },
                "ShowOpenSingleFileDialog",
                string.Empty);
        }

        /// <summary>
        /// 选择文件夹。
        /// .NET 8 WPF 可使用 Microsoft.Win32.OpenFolderDialog。
        /// </summary>
        public string ShowFolderBrowserDialog()
        {
            return ShowFileDialog(
                () =>
                {
                    var dialog = new OpenFolderDialog
                    {
                        Title = "选择文件夹",
                        Multiselect = false
                    };

                    return dialog.ShowDialog() == true ? dialog.FolderName : string.Empty;
                },
                "ShowFolderBrowserDialog",
                string.Empty);
        }

        /// <summary>
        /// 选择多个文件夹。
        /// </summary>
        public List<string> ShowMultiFolderBrowserDialog()
        {
            return ShowFileDialog(
                () =>
                {
                    var dialog = new OpenFolderDialog
                    {
                        Title = "选择文件夹",
                        Multiselect = true
                    };

                    return dialog.ShowDialog() == true
                        ? dialog.FolderNames.ToList()
                        : new List<string>();
                },
                "ShowMultiFolderBrowserDialog",
                new List<string>());
        }

        /// <summary>
        /// 选择保存文件路径。
        /// </summary>
        public string ShowSaveFileDialog(
            string title = "保存文件",
            string filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            string defaultFileName = "")
        {
            return ShowFileDialog(
                () =>
                {
                    var dialog = new SaveFileDialog
                    {
                        Title = title,
                        Filter = filter,
                        FileName = defaultFileName,
                        AddExtension = true,
                        OverwritePrompt = true
                    };

                    return dialog.ShowDialog() == true ? dialog.FileName : string.Empty;
                },
                "ShowSaveFileDialog",
                string.Empty);
        }

        // ------------------------------------------------------------------ 提示类对话框

        /// <summary>
        /// 确认对话框（是 / 否）。
        /// 无 UI 宿主时返回 false（默认"不确认"，任何破坏性分支都不会被执行）。
        /// </summary>
        public bool ShowConfirm(string message)
        {
            return ShowConfirm(message, string.Empty, false, out _);
        }

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
            bool checkedState = optionCheckedByDefault;

            bool confirmed = ShowValueDialog(
                new AppDialogRequest
                {
                    Title = "确认",
                    Message = message,
                    Icon = AppDialogIcon.Question,
                    Buttons = AppDialogButtons.YesNo,
                    YesText = "确定",
                    NoText = "取消",
                    OptionText = optionText,
                    OptionChecked = optionCheckedByDefault
                },
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
        /// <para><b>为什么降级方向与上面那个 <see cref="ShowConfirm(string, string, bool, out bool)"/>
        /// 正好相反</b>：那一个的语义是"要不要执行一个**有副作用**的动作"，没人点就必须按"不做"处理；
        /// 而这里只是一条**纯提示**，它拦住的是一整批解压。降级成"取消"的后果是所有无界面宿主
        /// （单元测试、控制台宿主、被别的程序调起的管线）一启动就被这条提醒拦死，而且没人看得到原因 ——
        /// 那才是真正的事故。所以这里显式定义成"没人点 = 继续"。</para>
        ///
        /// <para>勾选项在无界面宿主下恒为 false（<c>confirmed &amp;&amp; checkedState</c>，而 checkedState
        /// 就是传进来的默认值）："本次运行不再提示"是一个**用户意图**，程序不许替用户勾上。</para>
        /// </summary>
        /// <param name="title">标题（空 = 用 <see cref="AppDialogRequest"/> 的默认标题）。</param>
        /// <param name="yesText">主按钮文案（写动作本身："继续处理"）。</param>
        /// <param name="noText">次按钮文案（"先不处理"）。</param>
        /// <param name="optionChecked">用户最终是否勾选（未确认 / 无界面宿主时恒为 false）。</param>
        public bool ShowReminderConfirm(
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

            bool confirmed = ShowValueDialog(
                request,
                window =>
                {
                    checkedState = window.IsOptionChecked;
                    return window.Result == MessageBoxResult.Yes;
                },
                "ShowReminderConfirm",
                fallback: true);

            optionChecked = confirmed && checkedState;
            return confirmed;
        }

        /// <summary>
        /// 是 / 否 / 取消对话框。
        /// 无 UI 宿主时返回 <see cref="MessageBoxResult.Cancel"/>（最保守的一档）。
        /// </summary>
        public MessageBoxResult ShowYesNoCancel(string message)
        {
            return ShowValueDialog(
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

        /// <summary>
        /// 危险操作的二次确认：主按钮用实心红，且文案写清动作本身（"清空""删除"），
        /// 而不是含糊的"是"。默认按不确认处理。
        /// </summary>
        /// <param name="message">正文，建议写清"会发生什么、能不能撤销"。</param>
        /// <param name="confirmText">主按钮文案。</param>
        public bool ShowDestructiveConfirm(string message, string confirmText = "确定")
        {
            return ShowValueDialog(
                new AppDialogRequest
                {
                    Title = "危险操作确认",
                    Message = message,
                    Icon = AppDialogIcon.Warning,
                    Buttons = AppDialogButtons.YesNo,
                    Destructive = true,
                    YesText = string.IsNullOrWhiteSpace(confirmText) ? "确定" : confirmText,
                    NoText = "取消"
                },
                window => window.Result == MessageBoxResult.Yes,
                "ShowDestructiveConfirm",
                fallback: false);
        }

        /// <summary>
        /// 危险操作的**二次确认**（红色 + 勾选框）。
        ///
        /// 为什么需要它：规格 §3.2 的"彻底删除"档要求同时满足"界面红色标识"与"二次确认（勾选/输入）"，
        /// 而这两件事在原来的两个 API 里各占一半（<see cref="ShowDestructiveConfirm"/> 只有红色，
        /// <see cref="ShowConfirm(string, string, bool, out bool)"/> 只有勾选框）。
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
            bool checkedState = false;

            bool confirmed = ShowValueDialog(
                new AppDialogRequest
                {
                    Title = "危险操作确认",
                    Message = message,
                    Icon = AppDialogIcon.Warning,
                    Buttons = AppDialogButtons.YesNo,
                    Destructive = true,
                    YesText = string.IsNullOrWhiteSpace(confirmText) ? "确定" : confirmText,
                    NoText = "取消",
                    OptionText = optionText ?? string.Empty,
                    OptionChecked = false
                },
                window =>
                {
                    checkedState = window.IsOptionChecked;
                    return window.Result == MessageBoxResult.Yes;
                },
                "ShowDestructiveConfirm",
                fallback: false);

            optionChecked = confirmed && checkedState;
            return confirmed;
        }

        /// <summary>
        /// 危险操作确认 + **可选的**危险档勾选框（用户 2026-09-22 拍板的"其余物删除确认用勾选"）。
        ///
        /// <para>
        /// 与 <see cref="ShowDestructiveConfirm(string, string, string, out bool)"/> 的区别只有一处，
        /// 但很关键：<b>不勾选也能确认</b>。语义是"默认走安全档（移入回收站），勾上才切到激进档"——
        /// 勾选框在这里是**档位选择**，不是"必须承认才能继续"的确认位。
        /// 上一版把两者混用，导致用户不勾就没法走默认的回收站档。
        /// </para>
        /// <para>
        /// 激进档自己的二次确认（红色 + "我知道不可恢复"必勾）由调用方在拿到
        /// <paramref name="optionChecked"/> == true 之后再弹一次，本方法不代劳。
        /// </para>
        /// </summary>
        /// <param name="optionChecked">用户最终是否勾选（未确认时恒为 false）。</param>
        public bool ShowDestructiveConfirmWithOption(
            string message,
            string confirmText,
            string optionText,
            bool optionCheckedByDefault,
            out bool optionChecked)
        {
            bool checkedState = optionCheckedByDefault;

            bool confirmed = ShowValueDialog(
                new AppDialogRequest
                {
                    Title = "危险操作确认",
                    Message = message,
                    Icon = AppDialogIcon.Warning,
                    Buttons = AppDialogButtons.YesNo,
                    Destructive = true,
                    YesText = string.IsNullOrWhiteSpace(confirmText) ? "确定" : confirmText,
                    NoText = "取消",
                    OptionText = optionText ?? string.Empty,
                    OptionChecked = optionCheckedByDefault
                },
                window =>
                {
                    checkedState = window.IsOptionChecked;
                    return window.Result == MessageBoxResult.Yes;
                },
                "ShowDestructiveConfirmWithOption",
                fallback: false);

            optionChecked = confirmed && checkedState;
            return confirmed;
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

        /// <summary>勾上它 = 对后续所有同名冲突都照此办理（本批不再询问）。决策 D-4 的前提就是这一勾。</summary>
        private const string ConflictApplyToAllText = "对后面所有同名冲突都照此办理（本批不再询问）";

        /// <summary>当前正显示着的冲突询问窗口（取消时要能把它关掉，否则用户会对着一个已经作废的框点）。</summary>
        private static readonly object ConflictDialogLock = new();

        private static AppDialogWindow? _activeConflictDialog;

        /// <summary>
        /// 同名冲突询问：**一次问清"这一个 / 整批"怎么处理**（照 WinRAR 六档的精神，按批量场景裁剪）。
        ///
        /// <para>
        /// 六个选项 = 三个按钮 × 勾选框两态：
        /// 「覆盖 / 跳过 / 自动重命名」与「覆盖全部 / 跳过全部 / 全部自动重命名」；
        /// 直接关掉窗口（标题栏 ✕）= **取消本批**（等价于既有取消语义）。
        /// 为什么是 3 个按钮：自绘对话框最多同时放三个按钮（<c>Views/AppDialogWindow</c> 不归本轮改动管），
        /// 用"勾选框 = 全部"正好覆盖 WinRAR 六档的语义，而且比六个按钮更好读。
        /// </para>
        /// <para>
        /// <b>线程与降级</b>（与 <see cref="ShowValueDialog{T}"/> 同一口径，历史卡死就出在这一段）：
        /// </para>
        /// <list type="bullet">
        /// <item><description><b>无 UI 宿主</b>（<c>Application.Current == null</c>，单元测试 / 控制台宿主）：
        /// 写一条降级日志并返回 <c>null</c> —— **不弹窗、不死等**，调用方按保守档（自动重命名，绝不覆盖）继续。</description></item>
        /// <item><description><b>UI 线程</b>：直接 <c>ShowDialog()</c>（内部是嵌套消息泵，界面照常刷新）。</description></item>
        /// <item><description><b>后台线程</b>：<c>Dispatcher.InvokeAsync</c>（**不是**同步 <c>Invoke</c>，那会阻塞调用线程并放大死锁）
        /// + 有界等待；等待期间**响应取消令牌**（用户点了取消就把窗口关掉并返回 null）。</description></item>
        /// </list>
        /// <para>
        /// 返回 <c>null</c> 的语义是"**没问到答案**"（无界面宿主 / 被取消 / 超时）：
        /// 调用方**必须**按保守档处理，绝不允许把它当成"用户同意覆盖"。
        /// </para>
        /// </summary>
        public virtual Task<ConflictDecision?> ShowConflictDecisionAsync(
            ConflictPrompt prompt,
            CancellationToken cancellationToken = default)
        {
            prompt ??= new ConflictPrompt();

            Dispatcher? dispatcher = TryGetUiDispatcher();

            if (dispatcher == null)
            {
                LogFallback(
                    "ShowConflictDecision",
                    prompt.Message,
                    "当前宿主没有 WPF 界面，同名冲突询问无法进行，已按保守档（自动重命名，绝不覆盖）处理");

                return Task.FromResult<ConflictDecision?>(null);
            }

            try
            {
                if (dispatcher.CheckAccess())
                {
                    return Task.FromResult(ShowConflictDecisionModal(prompt));
                }

                DispatcherOperation<ConflictDecision?> operation =
                    dispatcher.InvokeAsync(() => ShowConflictDecisionModal(prompt));

                return AwaitConflictDialogAsync(operation, prompt, cancellationToken);
            }
            catch (Exception ex)
            {
                LogFallback("ShowConflictDecision", prompt.Message, "显示同名冲突询问失败：" + ex.Message);
                return Task.FromResult<ConflictDecision?>(null);
            }
        }

        /// <summary>
        /// 后台线程那条路：拿答案，但**绝不无限等**，也绝不无视取消令牌。
        /// </summary>
        private static async Task<ConflictDecision?> AwaitConflictDialogAsync(
            DispatcherOperation<ConflictDecision?> operation,
            ConflictPrompt prompt,
            CancellationToken cancellationToken)
        {
            Task<ConflictDecision?> dialog = operation.Task;

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using CancellationTokenRegistration registration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(() => cancelled.TrySetResult(true))
                : default;

            // 有界等待：对话框先有结果就把这个计时器取消掉，别让一个 5 分钟的定时器白挂着。
            using var timeoutCts = new CancellationTokenSource(BackgroundWaitTimeout);
            Task timeout = Task.Delay(BackgroundWaitTimeout, timeoutCts.Token);

            Task finished = await Task.WhenAny(dialog, cancelled.Task, timeout).ConfigureAwait(false);

            if (finished == dialog)
            {
                return await dialog.ConfigureAwait(false);
            }

            // 取消 / 超时：把窗口关掉（它已经作废了），返回 null 让调用方走保守档。
            CloseActiveConflictDialog(operation.Dispatcher);

            LogFallback(
                "ShowConflictDecision",
                prompt.Message,
                finished == cancelled.Task
                    ? "同名冲突询问期间被取消，已按保守档处理（绝不覆盖）"
                    : $"等待用户响应超过 {BackgroundWaitTimeout.TotalMinutes:0} 分钟，已按保守档处理（绝不覆盖）");

            return null;
        }

        /// <summary>
        /// 在 UI 线程上显示冲突询问并读回答案。
        ///
        /// 六个选项的映射：Yes=覆盖、No=跳过、Cancel=自动重命名，勾选框 = 全部；
        /// ✕ 关窗时 <see cref="AppDialogWindow.Result"/> 停在默认的 Cancel 而
        /// <c>DialogResult</c> **始终是 null**（按钮那条路会显式把它置成 false）——
        /// 这就是"取消本批"与"自动重命名"的区分点。
        /// </summary>
        private static ConflictDecision? ShowConflictDecisionModal(ConflictPrompt prompt)
        {
            var request = new AppDialogRequest
            {
                Title = "同名冲突",
                Message = prompt.Message,
                Subtitle = prompt.Subtitle,
                Detail = prompt.Detail,
                Icon = AppDialogIcon.Question,
                Buttons = AppDialogButtons.YesNoCancel,
                Destructive = prompt.Destructive,
                YesText = "覆盖",
                NoText = "跳过",
                CancelText = "自动重命名",
                OptionText = ConflictApplyToAllText,
                OptionChecked = false
            };

            var window = new AppDialogWindow(request);

            Window? owner = ResolveOwner();

            if (owner != null && owner.IsVisible)
            {
                window.Owner = owner;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            /*
             * 默认按钮（回车）不能是「覆盖」：AppDialogWindow 对 YesNoCancel 一律把 Yes 设为默认，
             * 而这里的 Yes 正是危险动作「覆盖」。模态框可能在用户打字时弹出来，回车即覆盖
             * 等于把"默认不得覆盖"（不变量 3）做反了。所以显式把默认按钮换成「跳过」。
             */
            window.YesButton.IsDefault = false;
            window.NoButton.IsDefault = true;

            lock (ConflictDialogLock)
            {
                _activeConflictDialog = window;
            }

            try
            {
                window.ShowDialog();
            }
            finally
            {
                lock (ConflictDialogLock)
                {
                    _activeConflictDialog = null;
                }
            }

            bool applyToAll = window.IsOptionChecked;

            return MapConflictAnswer(window.Result, window.DialogResult, applyToAll);
        }

        /// <summary>
        /// 六档询问的**答案映射**（唯一实现处；抽成静态方法是为了能脱离窗口单测）。
        ///
        /// 六个选项 = 三个按钮 × 勾选框两态：
        /// Yes=覆盖、No=跳过、Cancel=自动重命名，勾选框 = 对整批都照此办理；
        /// ✕ 关窗（<paramref name="dialogResult"/> 为 null —— 两条按钮路径都会显式写 DialogResult）
        /// = **取消本批**，勾选框不改变这个结论。
        /// </summary>
        internal static ConflictDecision MapConflictAnswer(
            MessageBoxResult result,
            bool? dialogResult,
            bool applyToAll)
        {
            switch (result)
            {
                case MessageBoxResult.Yes:
                    return ScopeConflictChoice(ConflictChoice.Overwrite, applyToAll);

                case MessageBoxResult.No:
                    return ScopeConflictChoice(ConflictChoice.Skip, applyToAll);

                case MessageBoxResult.Cancel when dialogResult is null:
                    return ConflictDecision.Once(ConflictChoice.CancelBatch);

                case MessageBoxResult.Cancel:
                    return ScopeConflictChoice(ConflictChoice.AutoRename, applyToAll);

                default:
                    // 未知结果（理论上到不了）：按最保守的"取消本批"处理，绝不落到"覆盖"。
                    return ConflictDecision.Once(ConflictChoice.CancelBatch);
            }
        }

        private static ConflictDecision ScopeConflictChoice(ConflictChoice choice, bool applyToAll) =>
            applyToAll ? ConflictDecision.ForAll(choice) : ConflictDecision.Once(choice);

        /// <summary>把已经作废的冲突询问窗口关掉（必须在 UI 线程上做）。</summary>
        private static void CloseActiveConflictDialog(Dispatcher dispatcher)
        {
            try
            {
                dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        AppDialogWindow? window;

                        lock (ConflictDialogLock)
                        {
                            window = _activeConflictDialog;
                        }

                        try
                        {
                            window?.Close();
                        }
                        catch
                        {
                            // 关不掉也只是留一个框，绝不允许把降级路径变成新的失败。
                        }
                    }));
            }
            catch
            {
                // 投递失败同上：降级路径不允许再制造失败。
            }
        }

        /// <summary>
        /// 信息提示。
        /// </summary>
        public void ShowInfo(string message)
        {
            ShowNotification(
                new AppDialogRequest
                {
                    Title = "提示",
                    Message = message,
                    Icon = AppDialogIcon.Info,
                    Buttons = AppDialogButtons.Ok
                },
                "ShowInfo");
        }

        /// <summary>
        /// 警告提示。
        /// </summary>
        public void ShowWarning(string message)
        {
            ShowNotification(
                new AppDialogRequest
                {
                    Title = "警告",
                    Message = message,
                    Icon = AppDialogIcon.Warning,
                    Buttons = AppDialogButtons.Ok
                },
                "ShowWarning");
        }

        /// <summary>
        /// 错误提示。
        /// </summary>
        public void ShowError(string message)
        {
            ShowNotification(
                new AppDialogRequest
                {
                    Title = "错误",
                    Message = message,
                    Icon = AppDialogIcon.Error,
                    Buttons = AppDialogButtons.Ok
                },
                "ShowError");
        }

        /// <summary>
        /// 异常提示。
        ///
        /// ⚠ 正文过 <see cref="PasswordMasker.Sanitize"/>：异常文本可能夹带命令行片段
        /// （7z 的 <c>-p&lt;明文密码&gt;</c> 就写在参数里，异常消息经常把整条命令带出来），
        /// 弹窗与日志同一口径 —— 密码只存内存，任何出口都不得出现明文（AGENTS.md §6 第 5 条）。
        /// </summary>
        public void ShowException(Exception ex, string prefix = "发生异常")
        {
            string message = ex == null
                ? prefix
                : $"{prefix}：{ex.Message}";

            // 类型名单独一行：用户回报问题时"是什么异常"比"异常说了什么"更管用，
            // 而且不夹带堆栈（堆栈里有路径，太长也不该塞进对话框正文）。
            string detail = ex == null
                ? string.Empty
                : ex.GetType().FullName ?? ex.GetType().Name;

            ShowNotification(
                new AppDialogRequest
                {
                    Title = "错误",
                    Message = PasswordMasker.Sanitize(message),
                    Detail = PasswordMasker.Sanitize(detail),
                    Icon = AppDialogIcon.Error,
                    Buttons = AppDialogButtons.Ok
                },
                "ShowException");
        }

        // ------------------------------------------------------------------ 内部：线程与降级

        /// <summary>
        /// 值返回型对话框：拿用户的答案。
        /// </summary>
        private static T ShowValueDialog<T>(
            AppDialogRequest request,
            Func<AppDialogWindow, T> read,
            string context,
            T fallback)
        {
            Dispatcher? dispatcher = TryGetUiDispatcher();

            if (dispatcher == null)
            {
                LogFallback(context, request.Message, "当前宿主没有 WPF 界面，已按默认值处理");
                return fallback;
            }

            try
            {
                if (dispatcher.CheckAccess())
                {
                    // UI 线程：ShowDialog 自己跑嵌套消息泵，界面不会卡住。
                    return ShowModal(request, read);
                }

                // 后台线程：InvokeAsync（不是同步 Invoke，那会阻塞调用线程并放大死锁）+ 有界等待。
                DispatcherOperation<T> operation = dispatcher.InvokeAsync(() => ShowModal(request, read));

                if (operation.Task.Wait(BackgroundWaitTimeout))
                {
                    return operation.Task.GetAwaiter().GetResult();
                }

                LogFallback(
                    context,
                    request.Message,
                    $"等待用户响应超过 {BackgroundWaitTimeout.TotalMinutes:0} 分钟，已按默认值处理");
                return fallback;
            }
            catch (Exception ex)
            {
                LogFallback(context, request.Message, "显示对话框失败：" + ex.Message);
                return fallback;
            }
        }

        /// <summary>
        /// 通知型对话框（没有返回值）：后台线程投递即返回，不阻塞调用方。
        /// </summary>
        private static void ShowNotification(AppDialogRequest request, string context)
        {
            Dispatcher? dispatcher = TryGetUiDispatcher();

            if (dispatcher == null)
            {
                LogFallback(context, request.Message, "当前宿主没有 WPF 界面，仅记录日志");
                return;
            }

            if (dispatcher.CheckAccess())
            {
                try
                {
                    ShowModal(request, window => window.Result);
                }
                catch (Exception ex)
                {
                    LogFallback(context, request.Message, "显示对话框失败：" + ex.Message);
                }

                return;
            }

            try
            {
                dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        try
                        {
                            ShowModal(request, window => window.Result);
                        }
                        catch (Exception ex)
                        {
                            LogFallback(context, request.Message, "显示对话框失败：" + ex.Message);
                        }
                    }));
            }
            catch (Exception ex)
            {
                LogFallback(context, request.Message, "投递对话框失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 文件对话框同样要能在无 UI 宿主下安全降级（测试会构造本服务）。
        /// </summary>
        private static T ShowFileDialog<T>(Func<T> show, string context, T fallback)
        {
            Dispatcher? dispatcher = TryGetUiDispatcher();

            if (dispatcher == null)
            {
                LogFallback(context, "(文件对话框)", "当前宿主没有 WPF 界面，返回空结果");
                return fallback;
            }

            try
            {
                if (dispatcher.CheckAccess())
                {
                    return show();
                }

                DispatcherOperation<T> operation = dispatcher.InvokeAsync(show);

                return operation.Task.Wait(BackgroundWaitTimeout)
                    ? operation.Task.GetAwaiter().GetResult()
                    : fallback;
            }
            catch (Exception ex)
            {
                LogFallback(context, "(文件对话框)", "显示失败：" + ex.Message);
                return fallback;
            }
        }

        /// <summary>
        /// 在 UI 线程上创建并模态显示统一对话框。
        /// </summary>
        private static T ShowModal<T>(AppDialogRequest request, Func<AppDialogWindow, T> read)
        {
            var window = new AppDialogWindow(request);

            Window? owner = ResolveOwner();

            if (owner != null && owner.IsVisible)
            {
                window.Owner = owner;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            window.ShowDialog();

            return read(window);
        }

        /// <summary>
        /// 选宿主窗口：先当前激活的那个（可能正是密码列表/设置窗口），再退回主窗口。
        /// 不这样做的话，从模态子窗口里弹出的提示会跑到主窗口后面去。
        /// </summary>
        private static Window? ResolveOwner()
        {
            Application? app = Application.Current;

            if (app == null)
            {
                return null;
            }

            Window? active = app.Windows
                .OfType<Window>()
                .FirstOrDefault(window => window.IsActive && window.IsVisible);

            if (active != null)
            {
                return active;
            }

            Window? main = app.MainWindow;

            return main != null && main.IsVisible ? main : null;
        }

        private static Dispatcher? TryGetUiDispatcher()
        {
            Application? app = Application.Current;

            if (app == null)
            {
                return null;
            }

            Dispatcher? dispatcher = app.Dispatcher;

            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return null;
            }

            return dispatcher;
        }

        /// <summary>
        /// 无 UI 宿主 / 超时 / 显示失败时的唯一出口：记一条降级记录，然后返回默认值。
        ///
        /// 记录前过一遍 <see cref="PasswordMasker.Sanitize"/> —— 提示文案里可能夹带
        /// "密码：xxx" 这种内容，日志里不得出现明文（AGENTS.md §6 第 5 条）。
        /// 写日志本身也必须吞掉所有异常：降级路径不允许再制造新的失败。
        /// </summary>
        private static void LogFallback(string context, string message, string reason)
        {
            string line =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{context}] {reason}｜" +
                PasswordMasker.Sanitize(message);

            lock (FallbackLock)
            {
                FallbackEntries.Add(line);

                if (FallbackEntries.Count > 200)
                {
                    FallbackEntries.RemoveAt(0);
                }
            }

            if (!FallbackFileLoggingEnabled)
            {
                return;
            }

            try
            {
                string directory = Path.Combine(AppContext.BaseDirectory, "data", "logs");

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(
                    Path.Combine(directory, "dialog-fallback.log"),
                    line + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch
            {
                // 降级日志写不进去也不允许影响调用方。
            }
        }
    }
}
