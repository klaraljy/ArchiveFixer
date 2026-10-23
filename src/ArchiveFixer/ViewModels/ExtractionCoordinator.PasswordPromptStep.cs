using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;


namespace ArchiveFixer.ViewModels
{
    /// <summary>手动密码/密码提示/密码失败汇总/危险条目提示（第 11 块，从 Password 大杂烩搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// 整批**一次性**地问"要不要手动给一个密码"（WinRAR 参考 §2 H 组 / §3 附注采纳项）。
        ///
        /// <para>
        /// 什么时候问：本批里有任何任务"可能带密码"时才问 —— 判据是既有的两个任务字段
        /// （<see cref="ArchiveTask.IsEncrypted"/> 或 <see cref="ArchiveTask.PasswordStatus"/> 为"需要密码"）。
        /// 都没有的批次一个字都不问，免得每次解一堆无密码的包都被挡一下。
        /// </para>
        /// <para>
        /// <b>无 UI 宿主（单测 / 控制台宿主）：不弹窗、不死等</b>，直接返回并写一条降级日志 ——
        /// 与 <c>DialogService.ShowConflictDecisionAsync</c> 同一口径。
        /// </para>
        /// <para>
        /// 用户答"不用"也一样记 <see cref="_manualPasswordPrompted"/>：一次性就是一次性，
        /// 不能因为"没填"就每次遇到觉得需要密码的包再问一遍。
        /// </para>
        /// </summary>
        private async Task PromptForManualBatchPasswordAsync(IReadOnlyList<ArchiveTask> tasks)
        {
            if (_manualPasswordPrompted)
            {
                return;
            }

            /*
             * 只问"**真有包连一个可用候选都没有**"的时候（2026-09-29 用户提问后的改动）。
             *
             * 老判据是"本批有包看起来要密码就弹" —— 哪怕密码本里明明有它的密码也弹，
             * 用户的原话就是"我的密码本里面明明有这个密码，为什么还是会出现"。现在复用
             * 与解压**同一套参数**算候选（GetPasswordCandidates：统一密码 / 密码本 / 空密码 / 旁路说明文件），
             * 只要有一个包一个候选都拿不出来才问；有候选就一个字都不打扰。
             */
            List<ArchiveTask> suspects = FindSuspectsWithoutUsablePassword(tasks);

            if (suspects.Count == 0)
            {
                return;
            }

            _manualPasswordPrompted = true;

            List<ArchiveTask> named = suspects.Take(MaxPasswordFailureNamesInDialog).ToList();

            string message =
                $"本批有 {suspects.Count} 个包可能带密码，而当前一个可用候选都没有。" +
                Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, named.Select(t => t.FileName)) +
                Environment.NewLine + Environment.NewLine +
                "可以现在手动给一个：本批所有任务都会把它当候选试一遍。" +
                Environment.NewLine +
                "⚠ 只对本次运行有效，不会写进任何文件、也不会进密码列表（关掉程序就没了）。";

            string? entered = await ShowPasswordPromptOnUiThreadAsync(message);

            if (string.IsNullOrEmpty(entered))
            {
                AppendLog("INFO", "没有手动输入密码（或当前宿主没有界面），本批按密码本与统一密码继续。");
                return;
            }

            _manualBatchPasswords.Add(entered);

            /*
             * 日志只说"收到一个手动密码"，**绝不记录内容**（不变量 5）。
             * 措辞上不给"密码"配冒号：PasswordMasker 的兜底规则是"密码 + 冒号 → 本行剩余全部打码"，
             * 那样这一行的后半句会被吃掉（旧版实测踩过这个坑）。
             */
            AppendLog(
                "INFO",
                $"已收到一个手动密码（{entered.Length} 个字符），只对本次运行有效，不落盘；" +
                "本批所有任务都会试它。");

            if (entered.Length > MaxSupportedPasswordLength)
            {
                /*
                 * 抄 WinRAR 6.10 才补上的那个坑：超长密码会被**静默截断**，
                 * 用户输对了却看到"密码错误"是最难查的一类现象，所以当场说清。
                 */
                AppendLog(
                    "WARN",
                    $"⚠ 你输入的手动密码有 {entered.Length} 个字符，超过 7-Zip 支持的 {MaxSupportedPasswordLength} 个，" +
                    "更长的部分会被截断 —— 如果解不开，先确认密码本身有没有这么长。");
            }
        }

        /// <summary>
        /// 一键档的密码提示（用户 2026-09-29 第二次改口径）：**不在批中间弹任何框、也不在面板里放输入框**
        /// （他原话："这个一键处理点击后出现一个输入框非常的奇怪，这个给他移除，可以弄一个提醒字样，
        /// 本次的解压可能需要密码，请在密码页一键导入"）。
        ///
        /// <para>所以这里只做两件事：① 把"要补密码就去「密码」页一键导入"写进日志；
        /// ② 标记本轮不再走"动手前问一次"那条路（续解的第 2/3 轮同一条口径）。</para>
        /// </summary>
        private void LogOneClickPasswordHint(IReadOnlyList<ArchiveTask> selectedTasks)
        {
            _manualPasswordPrompted = true;

            bool mayNeedPassword = selectedTasks.Any(task =>
                task != null &&
                (task.IsEncrypted ||
                 string.Equals(task.PasswordStatus, StatusText.PasswordNeed, StringComparison.Ordinal)));

            if (mayNeedPassword && _manualBatchPasswords.Count == 0)
            {
                AppendLog("INFO", StatusText.OneClickConfirmManualPasswordSkippedLog);
            }
        }

        /// <summary>手动密码候选的来源标记（只在这里出现一次，别在别处再写字面量）。</summary>
        internal const string ManualPasswordSource = "ManualBatch";

        /// <summary>
        /// 7-Zip 支持的密码长度上限（超过会被截断）。抄 WinRAR 参考里"6.10 才补上截断警告"那条教训：
        /// 我们**当场提示**，不静默截断。
        /// </summary>
        private const int MaxSupportedPasswordLength = 127;

        /// <summary>
        /// 在 UI 线程上要一个密码。**无 UI 宿主返回 null（不弹窗、不死等）**。
        ///
        /// <para>
        /// 为什么自己建一个小窗口而不是复用 <c>DialogService</c>：它的自绘对话框
        /// （<c>Views/AppDialogWindow</c>）没有输入框，而"改一个通用对话框控件"不在本次授权范围内
        /// （任务书明确要求"需要什么就写进报告"）。这里用 <see cref="PasswordBox"/> 而不是 TextBox：
        /// 手动密码同样不许明文显示在屏幕上（不变量 5 的同一精神），而且 <c>PasswordBox.Password</c>
        /// 不会进 WPF 的绑定/日志路径。
        /// </para>
        /// <para>
        /// 返回 null 的两种情况（都不算失败）：当前宿主没有 WPF 界面；用户点了"跳过"或直接关窗口。
        /// </para>
        /// </summary>
        private Task<string?> ShowPasswordPromptOnUiThreadAsync(string message)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null)
            {
                // 无界面宿主（单测 / 控制台）：只写日志，绝不弹模态框 —— 那会阻塞调用方且没人点得掉。
                AppendLog("INFO", "当前宿主没有 WPF 界面，跳过「手动输入密码」这一步（不影响其它流程）。");
                return Task.FromResult<string?>(null);
            }

            if (dispatcher.CheckAccess())
            {
                return Task.FromResult(ShowPasswordPromptModal(message));
            }

            return dispatcher.InvokeAsync(() => ShowPasswordPromptModal(message)).Task;
        }

        /// <summary>
        /// 密码输入小窗口本体（**只允许在 UI 线程上调用**）。
        ///
        /// 刻意**不设** <c>WindowStartupLocation</c> 以外的任何窗口行为：不置前、不最大化、不改尺寸。
        /// 默认按钮是「跳过」而不是「确定」——回车不该把可能错的密码当真（同 Ask 档"默认不能是危险动作"）。
        /// </summary>
        private static string? ShowPasswordPromptModal(string message)
        {
            var window = new System.Windows.Window
            {
                Title = "手动输入密码（只对本次运行有效）",
                Width = 520,
                Height = 300,
                MinWidth = 460,
                MinHeight = 280,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                Owner = System.Windows.Application.Current?.MainWindow,
                Background = System.Windows.Media.Brushes.White
            };

            /*
             * ⛔ 模态子窗一律不许最小化（用户 2026-10-05 拍板 A2）—— 这个框也是**代码里内联建的**，
             * 不在 XAML 名单里。它现在是 `NoResize`（压根点不动最小化），接上策略是为了①系统菜单那一项
             * 也置灰、②万一被 Win+D 这类系统级操作最小化，只要主窗口没跟着最小化就立刻还原。
             */
            ArchiveFixer.Views.WindowMinimizePolicy.Apply(window);

            var root = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(16) };

            for (int i = 0; i < 3; i++)
            {
                root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
                {
                    Height = i == 1 ? new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
                                    : System.Windows.GridLength.Auto
                });
            }

            var messageBlock = new System.Windows.Controls.TextBlock
            {
                Text = message,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 12)
            };

            System.Windows.Controls.Grid.SetRow(messageBlock, 0);
            root.Children.Add(messageBlock);

            var passwordBox = new System.Windows.Controls.PasswordBox
            {
                Height = 30,
                VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new System.Windows.Thickness(0, 0, 0, 12)
            };

            System.Windows.Controls.Grid.SetRow(passwordBox, 1);
            root.Children.Add(passwordBox);

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };

            var okButton = new System.Windows.Controls.Button
            {
                Content = "用这个密码",
                MinWidth = 110,
                Height = 30,
                Margin = new System.Windows.Thickness(0, 0, 8, 0)
            };

            var skipButton = new System.Windows.Controls.Button
            {
                Content = "跳过",
                MinWidth = 86,
                Height = 30,
                IsDefault = true,
                IsCancel = true
            };

            string? result = null;

            okButton.Click += (_, _) =>
            {
                result = passwordBox.Password;
                window.DialogResult = true;
                window.Close();
            };

            skipButton.Click += (_, _) =>
            {
                result = null;
                window.DialogResult = false;
                window.Close();
            };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(skipButton);

            System.Windows.Controls.Grid.SetRow(buttonPanel, 2);
            root.Children.Add(buttonPanel);

            window.Content = root;

            /*
             * 提醒（用户 2026-09-22 反馈的原话：「密码本那个窗口弹出来之后躲到主窗口后面了，
             * 只有声音、没有闪烁，声音还很轻」）。
             *
             * ⚠ 这一条**修正**了本方法早先那句"不置前、不 Topmost"的注释：那个口径来自 AGENTS.md §13，
             * 而 §13 管的是**代理脚本不许抢用户前台**（免得用户没法干别的事）。这里是**程序自己**
             * 在"整批卡在等密码"时把输入框拎出来 —— 用户明确要求这个行为，两者不冲突。
             * 强度是 Strong：一直闪任务栏（闪到被点）+ 连响三声。
             */
            ArchiveFixer.Helpers.WindowAttention.Attach(window, ArchiveFixer.Helpers.AttentionStrength.Strong);

            // 焦点给输入框（窗口被激活之后才拿得到键盘焦点）。
            window.Loaded += (_, _) => passwordBox.Focus();

            window.ShowDialog();

            return result;
        }

        /// <summary>
        /// 可执行 / 脚本类后缀 —— 判据只有这一份（别在别处再列一遍）。
        ///
        /// 取自 WinRAR 的"解压时排除的文件类型"示例（<c>*.scr *.pif *.exe</c>），
        /// 再加上脚本类与快捷方式。**只用于统计提示**，不做任何过滤（见 <see cref="AnalyzeDangerousEntries"/>）。
        /// </summary>
        private static readonly string[] DangerousEntryExtensions =
        {
            ".exe", ".scr", ".pif", ".com", ".msi", ".msp", ".cpl",
            ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
            ".hta", ".lnk", ".reg", ".jar", ".inf"
        };

        /// <summary>提示里最多列几个条目名（列太多会把详情刷屏，用户反而不看）。</summary>
        private const int MaxDangerousEntriesInHint = 5;

        /// <summary>
        /// 「危险条目提示」的展示前缀。它**不是**任务状态（不进 StatusText / 配色 / 统计），
        /// 只是一句提示文本的前缀，所以按 §7 的要求用常量收在这里，不散落字面量。
        /// </summary>
        internal const string DangerousEntriesHintPrefix = "可疑条目提示：";

        /// <summary>
        /// 在**已有的那一遍 list 结果**里统计"可执行 / 脚本类条目"，产出给用户看的一句话。
        ///
        /// <para>
        /// 三条纪律（<c>docs/WinRAR功能参考.md</c> §3 第 4/5 条）：
        /// ① **绝不为此再 list 一遍** —— 只吃调用方已经拿到手的那份清单（加密包每多列一次目录就多一次失败机会）；
        /// ② **只提示，绝不阻断** —— 真实资源包里安装器 / 补丁经常就是内容物，不做全局硬排除掩码；
        /// ③ 关掉设置项时返回空串，调用方据此不写任务字段、也不打日志。
        /// </para>
        ///
        /// 返回空串 = "没统计" 或 "统计了但没有可疑条目"，两种情况调用方都不必区分：
        /// 提示的意义只在"有东西要说"的时候。
        /// </summary>
        internal static string AnalyzeDangerousEntries(IEnumerable<ArchiveEntry>? entries, bool enabled)
        {
            if (!enabled || entries == null)
            {
                return string.Empty;
            }

            var found = new List<string>();
            int dangerousCount = 0;

            foreach (ArchiveEntry? entry in entries)
            {
                if (entry == null || entry.IsDirectory)
                {
                    continue;
                }

                string path = entry.Path ?? string.Empty;

                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                string extension = Path.GetExtension(path);

                if (string.IsNullOrWhiteSpace(extension))
                {
                    continue;
                }

                if (!DangerousEntryExtensions.Any(
                        known => string.Equals(known, extension, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                dangerousCount++;

                if (found.Count < MaxDangerousEntriesInHint)
                {
                    found.Add(Path.GetFileName(path));
                }
            }

            if (dangerousCount == 0 || found.Count == 0)
            {
                return string.Empty;
            }

            string samples = string.Join("、", found);

            if (dangerousCount > found.Count)
            {
                samples += $"…等 {dangerousCount} 个";
            }

            return $"{DangerousEntriesHintPrefix}本包含 {dangerousCount} 个可执行 / 脚本类条目（{samples}）。" +
                   "这不是错误，只是提醒你先看清楚再打开；程序不会因此阻断解压。";
        }

        /// <summary>
        /// 这条提示去哪儿显示：任务字段（失败清单里逐归档的第二级会原样带出）＋ 界面日志。
        ///
        /// 与"文件名已加密"/"路径过长"同一处产出（同一次 list），保证三个结论口径一致。
        /// </summary>
        private void PublishDangerousEntriesHint(ArchiveTask task, string hint, string level = "WARN")
        {
            if (string.IsNullOrWhiteSpace(hint))
            {
                return;
            }

            task.DangerousEntriesWarning = hint;
            AppendLog(level, $"{task.LogName}：{hint}");
        }

        /// <summary>
        /// 把本批"密码错误 / 密码错误或文件损坏 / 达到密码尝试上限"的任务**合并成一次**提示。
        ///
        /// 旧做法：在每个任务内部各弹一次模态框，50 个错包 = 50 次阻塞点击 ——
        /// 批量处理根本跑不动，用户看到的就是"点了没反应"。
        /// 新做法：循环里只登记（<see cref="RecordPasswordFailure"/>），批次结束后在这里一次说清：
        /// 有几个、都是谁、其中几个是"到上限"（不是密码错误）、几个是"两义档"（引擎只说"可能"）。
        /// 日志与弹窗是同一句话，所以没有界面时（无头宿主）也留得下证据。
        /// </summary>
        private async Task ShowPasswordFailuresSummaryAsync(bool showDialog = true)
        {
            List<(string FileName, string Status)> failures;

            lock (_passwordFailuresLock)
            {
                if (_passwordFailures.Count == 0)
                {
                    return;
                }

                failures = _passwordFailures.ToList();
                _passwordFailures.Clear();
            }

            /*
             * 三档而不是两档（2026-10-01）：`PasswordOrCorrupted`（RAR 的"文件已损坏或密码错误"）
             * 也要算进"密码类失败"—— 它恰恰是最可能缺密码的一档。⚠ 原来只分两档时，
             * 把它登记进来会被算成"达到密码尝试上限"，那是对用户的**误报**，
             * 所以登记判据与这里的分档必须**同时**改（§9.5：同一件事在多处出现，值 + 通知都要钉住）。
             */
            int wrongPasswordCount = failures.Count(f => f.Status == StatusText.WrongPassword);
            int ambiguousCount = failures.Count(f => f.Status == StatusText.PasswordOrCorrupted);
            int attemptLimitCount = failures.Count - wrongPasswordCount - ambiguousCount;

            /*
             * 措辞有个坑：日志会被 PasswordMasker 兜底脱敏，规则是"密码 + 冒号 → 本行剩余全部打码"。
             * 所以这里**不要在"密码"后面直接跟冒号**，否则用户看到的是"本批 3 个包…密码：******"，
             * 文件名与原因全被吃掉（实测踩过）。原因写成"原因：密码错误…"这种形态是安全的。
             *
             * ⚠ 前两档（全是"密码错误" / 全是"到上限"）的**原话一个字都不改** ——
             * 有用例逐字钉着它们（`ExtractionPipelineFixTests` 的"本批 3 个包没能解开"那一条）。
             */
            string reason;

            if (ambiguousCount == 0 && attemptLimitCount == 0)
            {
                reason = "密码错误或缺少正确密码";
            }
            else if (wrongPasswordCount == 0 && ambiguousCount == 0)
            {
                reason = $"{StatusText.PasswordAttemptLimitReached}（候选还没试完就按上限停了，不等于密码错误）";
            }
            else if (wrongPasswordCount == 0 && attemptLimitCount == 0)
            {
                reason = $"{StatusText.PasswordOrCorrupted}（引擎在那条路上只说了「可能」："
                         + "要么密码不对、要么数据真的坏了，程序不当它是确认的密码问题）";
            }
            else
            {
                // 混合档：有几档说几档，每档都带上它自己的免责（⛔ 不许笼统说成"密码错误"）。
                var parts = new List<string>();

                if (wrongPasswordCount > 0)
                {
                    parts.Add($"密码错误 {wrongPasswordCount} 个");
                }

                if (ambiguousCount > 0)
                {
                    parts.Add($"{StatusText.PasswordOrCorrupted} {ambiguousCount} 个（只说了「可能」，不等于确认是密码问题）");
                }

                if (attemptLimitCount > 0)
                {
                    parts.Add(
                        $"{StatusText.PasswordAttemptLimitReached} {attemptLimitCount} 个"
                        + "（候选还没试完就按上限停了，不等于密码错误）");
                }

                reason = string.Join(" / ", parts);
            }

            var builder = new StringBuilder();

            builder.Append(failures.Count == 1
                ? "有 1 个包没能解开"
                : $"本批 {failures.Count} 个包没能解开");

            builder.Append("，原因：");
            builder.Append(reason);
            builder.AppendLine();

            foreach (string name in failures.Select(f => f.FileName).Take(MaxPasswordFailureNamesInDialog))
            {
                builder.AppendLine(name);
            }

            if (failures.Count > MaxPasswordFailureNamesInDialog)
            {
                builder.AppendLine($"…等共 {failures.Count} 个（完整清单见「复制失败列表 / 导出失败清单」）。");
            }

            /*
             * 手动输入密码的**出口指引**（WinRAR 参考 §2 H 组采纳项）。
             *
             * 关键的一句是"重跑这一批会自动再问一次"：用户在这里看到"密码错误"时，
             * 下一步该做什么不是"去设置里改密码本"，而是"再点一次、把密码输进去"。
             * 只写"可以用手动密码"而不说怎么再触发，等于没说。
             */
            builder.AppendLine();

            /*
             * 撞上「每层密码尝试上限」时的指路（用户 2026-10-02 真机要求）：没试出来**不等于**这些包
             * 一定需要密码 —— 可能本来就没有密码，也可能对的密码排在候选更靠后（上限截断了）。
             * ⛔ 只在真的有包撞上限时写；⛔ 上面那两句 reason 原话一个字不动（有用例逐字钉着）。
             */
            if (attemptLimitCount > 0)
            {
                builder.AppendLine(string.Format(
                    CultureInfo.CurrentCulture,
                    StatusText.PasswordAttemptLimitHintFormat,
                    MaxPasswordAttemptsPerLayer));
            }

            builder.AppendLine(
                _manualBatchPasswords.Count > 0
                    ? "本次运行你已经手动给过密码；如果还是解不开，说明它不是这些包的密码 —— 换一个再试，或者去核对密码本。"
                    : _batchHadPasswordFailures
                        ? "下一步：再点一次「一键处理 / 只解压」——「只解压」会在开始前问你要不要手动输一个密码；"
                          + "一键处理则是把密码填进开始前的那个确认框（填了会加到「密码」页列表末尾，以后一直有效）。"
                        : "如果这些包需要密码：「只解压」会在开始前问你；一键处理则在开始前的确认框里有「本批手动密码」一格。");

            string message = builder.ToString().TrimEnd();

            // 日志与弹窗同源：日志一定写（无界面宿主也留得下证据），弹窗只在真的有界面时弹。
            // 这里把换行换成"；"是为了让日志一行看完 —— 换行后的内容不会被脱敏规则吃掉。
            AppendLog("WARN", message.Replace(Environment.NewLine, "；"));

            if (showDialog)
            {
                await ShowWarningOnUiThreadAsync(message);
            }
        }

        /// <summary>
        /// 一键处理期间**关掉所有"需要用户回答"的框**（用户 2026-09-27："以后不要出现弹窗"）。
        ///
        /// <para>
        /// 只做一件事：把同名冲突询问标成"问不到答案"—— 那条判据在
        /// <c>PostProcessSuccessAsync</c> 与 <c>PrecheckFinalLayoutConflicts</c> 两处已经是
        /// "问不到就走保守档（自动重命名、绝不覆盖）"，所以这里不需要第二套逻辑。
        /// 其余几个框（多分支 / 缺卷补救 / 批次结束提示）各自在调用点上读 <c>oneClickRun</c>。
        /// </para>
        ///
        /// <para>
        /// ⚠ 2026-10-04：这一行过去写着"多分支不展开" —— 那是"一键档一律保守"的旧口径
        /// （用户当场推翻：一键档只决定"问不问"，展开策略按②页所选档位）。
        /// </para>
        /// </summary>
        private void SuppressDecisionPromptsForOneClickRun()
        {
            _conflictStep.SuppressPromptsForRun();


            AppendLog(
                "INFO",
                "一键处理：本次不弹任何确认框（多分支按②页所选档位办、冲突走自动重命名、缺卷不补救、结束提示只写日志）——"
                + "该问的事一律按设置里你选的那一档办，理由逐条写在日志里。");
        }


        /// <summary>合并提示里最多列几个文件名（再多就让用户去看失败清单），别弹一个占满屏幕的框。</summary>
        private const int MaxPasswordFailureNamesInDialog = 10;

        private readonly List<(string FileName, string Status)> _passwordFailures = new();

        private readonly object _passwordFailuresLock = new();

        /// <summary>本批是否已经问过手动密码（一次性：问过就不再问，无论用户填没填）。</summary>
        private bool _manualPasswordPrompted;

        /// <summary>
        /// 本批是否出现过密码类失败（密码错误 / 达到尝试上限）。
        /// 批次结束后的合并提示据此换一句话：出过密码问题时指引"再点一次、把密码输进去"，
        /// 没出过就不提（免得每次跑完都念一遍）。
        /// </summary>
        private bool _batchHadPasswordFailures;

    }
}