using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 改名流程协调器。
    /// 负责智能修正/添加/替换/删除后缀的预览与执行。
    /// </summary>
    internal sealed class RenameCoordinator
    {
        private readonly MainViewModel _vm;
        private readonly ScanCoordinator _scanCoordinator;
        private readonly RenameService _renameService;
        private readonly DialogService _dialogService;

        public RenameCoordinator(
            MainViewModel vm,
            ScanCoordinator scanCoordinator,
            RenameService renameService,
            DialogService dialogService)
        {
            _vm = vm;
            _scanCoordinator = scanCoordinator;
            _renameService = renameService;
            _dialogService = dialogService;
        }

        private AppSettings Settings => _vm.Settings;
        private ObservableCollection<ArchiveTask> Tasks => _vm.Tasks;
        private bool IsBusy { get => _vm.IsBusy; set => _vm.IsBusy = value; }
        private void AppendLog(string message) => _vm.AppendLog(message);
        private void AppendLog(string level, string message) => _vm.AppendLog(level, message);
        private void UpdateSummary() => _vm.UpdateSummary();
        private void RefreshOutputPaths() => _vm.RefreshOutputPaths();
        private void RebuildTaskIndex() => _vm.RebuildTaskIndex();

        /// <summary>
        /// 这一次改名会动到哪些任务（**必须在执行之前对好**）。
        ///
        /// <para>为什么不能执行之后再找：执行阶段会把任务的 <c>CurrentPath</c> 换到新名字上，
        /// 那时再拿预览项里记的"原路径"去比就对不上了（第二次改名尤其明显）。</para>
        /// </summary>
        private List<ArchiveTask> MatchTasks(IEnumerable<RenamePreviewItem> items)
        {
            var matched = new List<ArchiveTask>();
            var seen = new HashSet<ArchiveTask>();

            foreach (RenamePreviewItem item in items)
            {
                ArchiveTask? task = RenameService.FindTaskByPath(Tasks, item.OriginalPath);

                if (task != null && seen.Add(task))
                {
                    matched.Add(task);
                }
            }

            return matched;
        }

        /// <summary>
        /// 改名跑完只重扫**这一次真正改过名的那些任务**。
        ///
        /// <para>⛔ 不许退回 <c>_scanCoordinator.ScanTasksAsync()</c>（2026-09-26 审计修的真缺陷）：
        /// 那一条会遍历**整张任务列表**，而 <c>ArchiveDetectService.ApplyDetectResultAsync</c>
        /// 一进来就把 <c>Status = 扫描中</c>、<c>ErrorMessage = ""</c>，收尾再统一写成「已识别 / 格式未知」——
        /// 于是用户改一个文件的后缀，整张表里已经解压成功的行变回「已识别」、
        /// 失败行的**失败原因被清空**，而汇总与失败清单读的是另一套字段
        /// （<c>Outcome</c> / <c>OutputVerification</c> / <c>ErrorMessage</c>）→
        /// "汇总说失败 3 个、列表里一行失败都看不到"。顺带还要把整表重新识别一遍（大包很慢）。</para>
        ///
        /// <para>一键处理那条链早就绕开了它（见 <c>OneClickCoordinator.ScanRoundAsync</c> 的注释），
        /// 「按建议改名并重试」也是按任务重扫 —— 只有手动这五颗按钮漏了。</para>
        /// </summary>
        private async Task RescanRenamedTasksAsync(IEnumerable<ArchiveTask> renamedTasks)
        {
            foreach (ArchiveTask task in renamedTasks)
            {
                await _scanCoordinator.RescanTaskAsync(task).ConfigureAwait(true);
            }
        }

        internal async Task SmartRenameAsync()
        {
            await RenameByOptionsAsync(BuildFixByDetectedFormatOptions());
        }

        /// <summary>
        /// **一键处理里的自动改名**（用户 2026-09-25 明确指示）。
        ///
        /// <para><b>用户原话</b>：「这个一键处理自己会自动改名自动解压，为什么遇到这种改名的压缩包还要我两次确认，
        /// 这个对用户来说是完全多余的，繁琐的操作，那种情况只有手动档才会有」。
        /// 所以一键档**不弹改名预览、也不弹执行前确认**：计划与手动档**完全同一套**
        /// （同一个 <see cref="BuildPreviewAsync"/>、同一条 <c>RenameService.ExecuteRenameAsync</c>），
        /// 差别只在"给不给人看那一页"。</para>
        ///
        /// <para><b>为什么不违反"改名不静默"</b>：改后缀是**可逆**操作，而且每一个改名都照旧
        /// 逐条写进日志（`旧名 -> 新名，状态：…`）与任务列表；一键档的语义本来就是
        /// "点一次，剩下全自动"。手动档（②页/右键那几条命令）**照旧先出预览**，一个字没改。</para>
        ///
        /// <para>冲突一律按**保守档**处理（自动重命名，绝不覆盖）——与手动档"没选 = 自动重命名"
        /// 是同一条兜底（<see cref="RenameService.ApplyConflictChoice"/>），不另写一套判定。</para>
        /// </summary>
        /// <returns>真的执行了改名返回 true；没有可改的项（或参数被挡下）返回 false。</returns>
        internal async Task<bool> AutoFixExtensionsAsync()
        {
            List<RenamePreviewItem>? previewItems = await BuildPreviewAsync(BuildFixByDetectedFormatOptions());

            if (previewItems == null)
            {
                return false;
            }

            foreach (RenamePreviewItem item in previewItems)
            {
                _renameService.ApplyConflictChoice(item);
            }

            List<RenamePreviewItem> selected = previewItems.Where(x => x.IsSelected && x.CanRename).ToList();

            if (selected.Count == 0)
            {
                AppendLog("INFO", "一键处理：自动改名这一步没有可执行的项，跳过。");
                return false;
            }

            IsBusy = true;

            try
            {
                AppendLog(
                    "INFO",
                    $"一键处理：自动修正后缀 {selected.Count} 项（一键档不弹预览窗口，逐条写日志）。");

                List<ArchiveTask> renamedTasks = MatchTasks(selected);

                await _renameService.ExecuteRenameAsync(selected, Tasks);

                foreach (RenamePreviewItem item in selected)
                {
                    AppendLog(
                        "INFO",
                        $"{item.OriginalFileName} -> {item.NewFileName}，状态：{item.Status}，错误：{item.ErrorMessage}");
                }

                await RescanRenamedTasksAsync(renamedTasks);

                RebuildTaskIndex();
                RefreshOutputPaths();
                UpdateSummary();

                return true;
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "自动改名失败：" + ex.Message);
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>「按识别结果修正后缀」的参数（手动档与一键档共用同一份，绝不分叉）。</summary>
        private RenameOptions BuildFixByDetectedFormatOptions() => new RenameOptions
        {
            OperationType = "FixByDetectedFormat",
            TargetExtension = Settings.DefaultExtension,
            DeleteExtensionCount = 1,
            ConflictAction = Settings.ConflictAction,
            PreviewBeforeRename = true,
            UnknownFormatAction = Settings.UnknownFormatAction
        };

        /// <summary>
        /// 「改后缀…」：**一颗按钮 + 一个小窗**（模式下拉 + 值），取代原来的三颗按钮
        /// （添加 / 替换最后一个 / 删除最后一个 —— 2026-09-26 审计：同一件事拆成三颗，
        /// 手动这一栏就变成了"六七颗按不动的按钮"）。
        ///
        /// <para>三条路仍然走**同一套**预览与执行（<see cref="RenameByOptionsAsync"/>）：
        /// 先给你看改名预览 → 确认 → 才动盘；⛔ 绝不覆盖、绝不先删后移。</para>
        /// </summary>
        internal async Task ChangeNameAsync()
        {
            (string Mode, string Extension, string FindText, string ReplaceText)? choice = ShowRenameDialog();

            if (choice == null)
            {
                AppendLog("INFO", "用户取消改名。");
                return;
            }

            string mode = choice.Value.Mode;
            string extension = choice.Value.Extension;
            string findText = choice.Value.FindText;
            string replaceText = choice.Value.ReplaceText;

            if (string.Equals(mode, "ReplaceFileNameText", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(findText))
                {
                    _dialogService.ShowWarning("「查找」不能为空：要删掉 / 换掉哪一段，得先写出来。");
                    return;
                }
            }
            else if (!string.Equals(mode, "DeleteLastExtension", StringComparison.OrdinalIgnoreCase) &&
                     string.IsNullOrWhiteSpace(extension))
            {
                _dialogService.ShowWarning("后缀不能为空。");
                return;
            }

            var options = new RenameOptions
            {
                OperationType = mode,
                TargetExtension = extension,
                DeleteExtensionCount = 1,
                FindText = findText,
                ReplaceText = replaceText,
                ConflictAction = Settings.ConflictAction,
                PreviewBeforeRename = true,
                UnknownFormatAction = Settings.UnknownFormatAction
            };

            await RenameByOptionsAsync(options);
        }

        // 「删除多个后缀」已退役（2026-09-26 审计）：要先想清"N 是几"，而真实数据里几乎没有
        // "连续多个后缀"的形状 —— 它与「替换后缀」重叠，路径上还多一次输入框。
        // 退役方式 = 按钮 + 命令 + 这里的方法 + RenameOptions 的工厂 + 服务分支一起删（⛔ 不留半截）。

        private async Task RenameByOptionsAsync(RenameOptions options)
        {
            List<RenamePreviewItem>? previewItems = await BuildPreviewAsync(options);

            if (previewItems == null)
            {
                return;
            }

            var window = new RenamePreviewWindow(previewItems)
            {
                Owner = Application.Current.MainWindow
            };

            bool? dialogResult = window.ShowDialog();

            if (dialogResult != true)
            {
                AppendLog("INFO", "用户取消改名。");
                return;
            }

            RenamePreviewViewModel vm = window.ViewModel;

            /*
             * 「询问」档在改名路径上的落地（真实缺陷修正，2026-09-22）。
             *
             * 以前 Ask 被并进"其余 → 自动重命名"分支，于是预览表里会出现
             * 「询问」+「将自动重命名」并列 —— 界面说一套、代码做一套。
             * 现在预览会给撞名的行一个下拉框（覆盖 / 跳过 / 自动重命名），用户在预览里**逐条**选；
             * 这里把选定的结论写死到 NewPath 上，于是"预览里显示的那个路径"就是"执行时会落的那个路径"。
             *
             * ⛔ 不变量 3：**没选 = 自动重命名，绝不覆盖**（ApplyConflictChoice 的兜底分支）。
             * 不再弹第二个对话框 —— 预览本来就是"先给你看"的那个地方，再问一遍纯属多余。
             *
             * 对**预览里的每一条**都算一遍，而不是只算勾选的：用户在预览里明确选了「跳过」的那一条
             * 会被 MarkSkip 顺手取消勾选，如果这里只看勾选项，他的选择就既没被执行也没留痕。
             */
            int conflictChoices = 0;

            foreach (RenamePreviewItem item in vm.Items)
            {
                if (_renameService.ApplyConflictChoice(item))
                {
                    conflictChoices++;
                }
            }

            if (conflictChoices > 0)
            {
                AppendLog("INFO", $"同名冲突：按你在预览里的选择处理了 {conflictChoices} 项（没选的按保守档自动重命名，绝不覆盖）。");
            }

            // 冲突结论写完再取勾选项：选了「跳过」的那些到这里已经被取消勾选，不会进执行。
            List<RenamePreviewItem> selectedPreviewItems = vm.GetSelectedItems();

            if (selectedPreviewItems.Count == 0)
            {
                _dialogService.ShowInfo(
                    conflictChoices > 0
                        ? "冲突处理之后没有可执行的改名项了（可能都选了「跳过」）。"
                        : "当前没有可执行的改名项，请查看改名预览中的状态和错误信息。");
                return;
            }

            bool finalConfirm = _dialogService.ShowConfirm(
                $"确定要执行改名吗？\n\n将改名 {selectedPreviewItems.Count} 个文件。\n\n注意：这是实际文件重命名操作。");

            if (!finalConfirm)
            {
                AppendLog("INFO", "用户在最终确认时取消改名。");
                return;
            }

            IsBusy = true;

            try
            {
                AppendLog("INFO", $"开始执行改名，共 {selectedPreviewItems.Count} 个文件。");

                List<ArchiveTask> renamedTasks = MatchTasks(selectedPreviewItems);

                await _renameService.ExecuteRenameAsync(
                    selectedPreviewItems,
                    Tasks);

                foreach (RenamePreviewItem item in selectedPreviewItems)
                {
                    AppendLog("INFO",
                        $"{item.OriginalFileName} -> {item.NewFileName}，状态：{item.Status}，错误：{item.ErrorMessage}");
                }

                await RescanRenamedTasksAsync(renamedTasks);

                RebuildTaskIndex();
                RefreshOutputPaths();
                UpdateSummary();

                AppendLog("INFO", "改名流程完成。");
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "改名失败：" + ex.Message);
                _dialogService.ShowError("改名失败：" + ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// 选中检查 → 参数归一 → 生成预览 → **写一条"真实目标"日志**。
        ///
        /// <para>
        /// 单独抽出来有两个理由：
        /// ① 让"日志里那个目标后缀"能被**直接测到**（缺陷 2 就是这一行写错了）；
        /// ② 窗口那一段（<see cref="RenameByOptionsAsync"/> 的后半）在本测试进程里没有 UI 宿主，
        ///    抽开之后"预览与日志"这条路可以脱离窗口单测。
        /// </para>
        /// </summary>
        /// <returns>预览项；被参数/勾选挡下时为 null（此时已经提示过，调用方直接返回）。</returns>
        internal async Task<List<RenamePreviewItem>?> BuildPreviewAsync(RenameOptions options)
        {
            var selectedTasks = Tasks.Where(x => x.IsSelected).ToList();

            if (selectedTasks.Count == 0)
            {
                // 与界面蓝字同一套词（"勾选"）：旧文案"请先选择需要改名的任务"容易被读成"点中一行也算"。
                _dialogService.ShowWarning($"请先勾选要改名的任务（最左侧一列）。{Environment.NewLine}{Environment.NewLine}"
                                           + $"列表里有 {Tasks.Count} 个任务，当前一个都没勾。");
                return null;
            }

            if (options == null)
            {
                _dialogService.ShowWarning("改名参数为空。");
                return null;
            }

            options.Normalize();

            List<RenamePreviewItem> previewItems = await Task.Run(
                () => _renameService.BuildPreview(selectedTasks, options));

            /*
             * ⚠ 这一行必须打**真实目标**（缺陷 2）。
             *
             * 原实现打的是 options.TargetExtension，也就是设置里的 DefaultExtension（例如 .7z）——
             * 而 FixByDetectedFormat 走的是"识别结果"给出的后缀（检测到 ZIP → .zip），
             * options.TargetExtension 只是"格式未知"时的兜底。于是日志写着「目标后缀=.7z」、
             * 预览窗口里那一行却是 `整包.mp4 → 整包.zip`，排查的人会一路去怀疑设置项。
             * 现在改为从**新文件名**里取真实目标（多条不同目标就按项数汇总成一句）。
             */
            AppendLog("INFO", BuildRenamePreviewLog(options, previewItems));

            if (previewItems.Count == 0)
            {
                _dialogService.ShowInfo("没有生成任何改名预览项。");
                return null;
            }

            return previewItems;
        }

        /// <summary>
        /// 「生成改名预览」那条日志的正文（**唯一构造处**，纯函数便于直接断言）。
        ///
        /// <para>形态：</para>
        /// <list type="bullet">
        /// <item><description>一条：<c>生成改名预览：操作=FixByDetectedFormat，目标后缀=.zip（1 项：整包.mp4 → 整包.zip），删除数量=1</c></description></item>
        /// <item><description>多条同目标：<c>…目标后缀=.zip（3 项），删除数量=1</c></description></item>
        /// <item><description>多条不同目标：<c>…目标后缀=.zip（2 项）、.rar（1 项），删除数量=1</c></description></item>
        /// <item><description>一条都没有：<c>…没有生成任何预览项（删除数量=1）</c></description></item>
        /// </list>
        ///
        /// <para>
        /// 后缀一律取自 <see cref="RenamePreviewItem.NewFileName"/>：那才是执行时会落到磁盘上的东西。
        /// 生成失败 / 认不出目标的那一条写「（未知）」而不是拿原后缀冒充（日志里的数字宁可没有，
        /// 也不能是编的 —— 与预览统计的同一口径）。
        /// </para>
        /// </summary>
        internal static string BuildRenamePreviewLog(
            RenameOptions? options,
            IReadOnlyList<RenamePreviewItem>? items)
        {
            string operation = options?.OperationType ?? string.Empty;
            int deleteCount = options?.DeleteExtensionCount ?? 0;

            if (items == null || items.Count == 0)
            {
                return $"生成改名预览：操作={operation}，没有生成任何预览项（删除数量={deleteCount}）";
            }

            // 按"出现的先后顺序"分组：日志读起来与预览表的顺序一致（先看到的那一类排前面）。
            var extensionCounts = new List<KeyValuePair<string, int>>();

            foreach (RenamePreviewItem item in items)
            {
                string extension = ResolveTargetExtension(item);
                int index = extensionCounts.FindIndex(pair =>
                    string.Equals(pair.Key, extension, StringComparison.OrdinalIgnoreCase));

                if (index >= 0)
                {
                    extensionCounts[index] = new KeyValuePair<string, int>(
                        extensionCounts[index].Key,
                        extensionCounts[index].Value + 1);
                }
                else
                {
                    extensionCounts.Add(new KeyValuePair<string, int>(extension, 1));
                }
            }

            string targets = string.Join(
                "、",
                extensionCounts.Select(pair => $"{pair.Key}（{pair.Value} 项）"));

            // 只有一条时顺手把"谁改成谁"写出来：这正是用户在真机上想看到的那一句。
            string detail = items.Count == 1
                ? $"（1 项：{items[0].OriginalFileName} → {items[0].NewFileName}）"
                : string.Empty;

            string targetText = items.Count == 1
                ? targets.Replace("（1 项）", detail, StringComparison.Ordinal)
                : targets;

            return $"生成改名预览：操作={operation}，目标后缀={targetText}，删除数量={deleteCount}";
        }

        /// <summary>
        /// 一条预览项的**真实目标后缀**（缺陷 2 的判据）：从新文件名里取，取不到就说取不到。
        /// </summary>
        private static string ResolveTargetExtension(RenamePreviewItem item)
        {
            string newName = item.NewFileName;

            if (string.IsNullOrWhiteSpace(newName))
            {
                return "（未知）";
            }

            string extension = Path.GetExtension(newName);

            return string.IsNullOrWhiteSpace(extension) ? "（无后缀）" : extension;
        }

        private static string NormalizeUserExtension(string? extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return string.Empty;
            }

            string value = extension.Trim();

            while (value.StartsWith("*.", StringComparison.Ordinal))
            {
                value = value.Substring(1);
            }

            if (value.StartsWith(".", StringComparison.Ordinal))
            {
                return value;
            }

            return "." + value;
        }

        /// <summary>
        /// 「改名…」那个小窗：**模式下拉 + 值 / 查找→替换**（2026-09-26 用户批准：这一栏收成一颗按钮）。
        ///
        /// <para>四种模式：**替换文件名里的文字**（默认，正是他真机上最需要的那一档 ——
        /// 打包者往名字里塞字，加/替换/删后缀都救不了）/ 替换最后一个后缀 / 添加后缀 / 删除最后一个后缀。</para>
        ///
        /// <para>走 <c>AppWindowStyle</c> + <c>SizeToContent</c>（可缩放、主操作在最右、提示紧跟输入框）。
        /// 返回 <c>null</c> = 用户取消。</para>
        /// </summary>
        private (string Mode, string Extension, string FindText, string ReplaceText)? ShowRenameDialog()
        {
            string defaultExtension = string.IsNullOrWhiteSpace(Settings.DefaultExtension)
                ? ".zip"
                : Settings.DefaultExtension;

            var window = new Window
            {
                Title = "改名",
                Width = 560,
                MinWidth = 460,
                MaxWidth = 780,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                ShowInTaskbar = false,
                Owner = Application.Current?.MainWindow
            };

            window.SetResourceReference(FrameworkElement.StyleProperty, "AppWindowStyle");

            /*
             * ⛔ 模态子窗一律不许最小化（用户 2026-10-05 拍板 A2；口径与 8 个 XAML 子窗同一份策略）。
             * 这个框是**代码里内联建的**，不在 XAML 名单里，漏了它就等于留着"最小化以后任务栏没有按钮、
             * 主窗口又被模态禁用 ⇒ 窗口找不回来"那个陷阱（它正是 `CanResize` + 不显示任务栏按钮那种形状）。
             */
            ArchiveFixer.Views.WindowMinimizePolicy.Apply(window);

            var root = new System.Windows.Controls.Grid
            {
                Margin = new Thickness(18, 16, 18, 14)
            };

            for (int i = 0; i < 6; i++)
            {
                root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
                {
                    Height = System.Windows.GridLength.Auto
                });
            }

            var message = new System.Windows.Controls.TextBlock
            {
                Text = "对勾选的任务改名。确认后会先弹改名预览（绝不覆盖、绝不先删后移）；分卷文件只有「替换文件名里的文字」会动。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            System.Windows.Controls.Grid.SetRow(message, 0);
            root.Children.Add(message);

            var modeBox = new System.Windows.Controls.ComboBox
            {
                MinHeight = 30,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            modeBox.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = "替换文件名里的文字（名字被塞了字就用它）",
                Tag = "ReplaceFileNameText",
                ToolTip = "把名字里出现的那一段换成另一段；「替换为」留空 = 直接删掉。整个文件名都算（含后缀段），区分大小写。"
            });

            modeBox.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = "替换最后一个后缀",
                Tag = "ReplaceLastExtension",
                ToolTip = "只换最后那一段：test.jpg -> test.zip"
            });

            modeBox.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = "添加后缀",
                Tag = "AddExtension",
                ToolTip = "在原名后面再挂一个后缀：x.mp4 -> x.mp4.zip"
            });

            modeBox.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = "删除最后一个后缀",
                Tag = "DeleteLastExtension",
                ToolTip = "把最后那一段去掉：test.rar.jpg -> test.rar"
            });

            modeBox.SelectedIndex = 0;
            System.Windows.Controls.Grid.SetRow(modeBox, 1);
            root.Children.Add(modeBox);

            // ── 后缀那一行（后缀三种模式用） ──
            var extensionRow = new System.Windows.Controls.Grid
            {
                Margin = new Thickness(0, 8, 0, 0)
            };
            extensionRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            {
                Width = new System.Windows.GridLength(96)
            });
            extensionRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            {
                Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
            });

            var extensionLabel = new System.Windows.Controls.TextBlock
            {
                Text = "后缀",
                VerticalAlignment = VerticalAlignment.Center
            };
            System.Windows.Controls.Grid.SetColumn(extensionLabel, 0);
            extensionRow.Children.Add(extensionLabel);

            var extensionBox = new System.Windows.Controls.TextBox
            {
                Text = defaultExtension,
                MinHeight = 30,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            System.Windows.Controls.Grid.SetColumn(extensionBox, 1);
            extensionRow.Children.Add(extensionBox);

            System.Windows.Controls.Grid.SetRow(extensionRow, 2);
            root.Children.Add(extensionRow);

            // ── 查找 → 替换为（文字模式用） ──
            var textRow = new System.Windows.Controls.Grid
            {
                Margin = new Thickness(0, 8, 0, 0)
            };
            textRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            {
                Width = new System.Windows.GridLength(64)
            });
            textRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            {
                Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
            });
            textRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            {
                Width = new System.Windows.GridLength(12)
            });
            textRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            {
                Width = new System.Windows.GridLength(64)
            });
            textRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            {
                Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
            });

            var findLabel = new System.Windows.Controls.TextBlock
            {
                Text = "查找",
                VerticalAlignment = VerticalAlignment.Center
            };
            System.Windows.Controls.Grid.SetColumn(findLabel, 0);
            textRow.Children.Add(findLabel);

            var findBox = new System.Windows.Controls.TextBox
            {
                MinHeight = 30,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "要删掉 / 换掉的那一段（区分大小写）"
            };
            System.Windows.Controls.Grid.SetColumn(findBox, 1);
            textRow.Children.Add(findBox);

            var replaceLabel = new System.Windows.Controls.TextBlock
            {
                Text = "替换为",
                VerticalAlignment = VerticalAlignment.Center
            };
            System.Windows.Controls.Grid.SetColumn(replaceLabel, 3);
            textRow.Children.Add(replaceLabel);

            var replaceBox = new System.Windows.Controls.TextBox
            {
                MinHeight = 30,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "留空 = 直接删掉那一段（最常用）"
            };
            System.Windows.Controls.Grid.SetColumn(replaceBox, 4);
            textRow.Children.Add(replaceBox);

            System.Windows.Controls.Grid.SetRow(textRow, 3);
            root.Children.Add(textRow);

            var hint = new System.Windows.Controls.TextBlock
            {
                Text = "提示：「后缀」那一栏只在后缀三种模式下生效；文字模式在「整个文件名」上替换（含后缀段）。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                Margin = new Thickness(0, 10, 0, 0)
            };
            hint.SetResourceReference(FrameworkElement.StyleProperty, "HintTextStyle");
            System.Windows.Controls.Grid.SetRow(hint, 4);
            root.Children.Add(hint);

            string SelectedMode()
            {
                return modeBox.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
                       item.Tag is string tag
                    ? tag
                    : "ReplaceFileNameText";
            }

            void ApplyMode()
            {
                string mode = SelectedMode();
                bool isTextMode = string.Equals(mode, "ReplaceFileNameText", StringComparison.OrdinalIgnoreCase);
                bool needsValue = !string.Equals(mode, "DeleteLastExtension", StringComparison.OrdinalIgnoreCase);

                textRow.Visibility = isTextMode ? Visibility.Visible : Visibility.Collapsed;
                extensionRow.Visibility = isTextMode ? Visibility.Collapsed : Visibility.Visible;
                extensionBox.IsEnabled = needsValue;
                extensionBox.Opacity = needsValue ? 1.0 : 0.5;
            }

            modeBox.SelectionChanged += (_, _) => ApplyMode();
            ApplyMode();

            var buttons = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };

            var cancelButton = new System.Windows.Controls.Button
            {
                Content = StatusText.OpCancel,
                MinWidth = 88,
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true
            };
            cancelButton.SetResourceReference(FrameworkElement.StyleProperty, "SecondaryButtonStyle");

            var okButton = new System.Windows.Controls.Button
            {
                Content = "确定",
                MinWidth = 96,
                IsDefault = true
            };
            okButton.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButtonStyle");

            okButton.Click += (_, _) =>
            {
                window.DialogResult = true;
                window.Close();
            };

            cancelButton.Click += (_, _) =>
            {
                window.DialogResult = false;
                window.Close();
            };

            buttons.Children.Add(cancelButton);
            buttons.Children.Add(okButton);

            System.Windows.Controls.Grid.SetRow(buttons, 5);
            root.Children.Add(buttons);

            window.Content = root;

            window.Loaded += (_, _) =>
            {
                if (string.Equals(SelectedMode(), "ReplaceFileNameText", StringComparison.OrdinalIgnoreCase))
                {
                    findBox.Focus();
                }
                else
                {
                    extensionBox.Focus();
                }
            };

            bool? result = window.ShowDialog();

            if (result != true)
            {
                return null;
            }

            return (
                SelectedMode(),
                NormalizeUserExtension(extensionBox.Text),
                findBox.Text ?? string.Empty,
                replaceBox.Text ?? string.Empty);
        }
    }
}
