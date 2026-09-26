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
        private Task ScanTasksAsync() => _scanCoordinator.ScanTasksAsync();

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

                await _renameService.ExecuteRenameAsync(selected, Tasks);

                foreach (RenamePreviewItem item in selected)
                {
                    AppendLog(
                        "INFO",
                        $"{item.OriginalFileName} -> {item.NewFileName}，状态：{item.Status}，错误：{item.ErrorMessage}");
                }

                await ScanTasksAsync();

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

        internal async Task AddExtensionAsync()
        {
            string defaultExt = string.IsNullOrWhiteSpace(Settings.DefaultExtension)
                ? ".zip"
                : Settings.DefaultExtension;

            string? input = ShowTextInputDialog(
                "批量添加后缀",
                "请输入要添加的后缀。\n例如：.zip、.rar、.7z、.jpg、.bin、.001",
                defaultExt);

            if (string.IsNullOrWhiteSpace(input))
            {
                AppendLog("INFO", "用户取消添加后缀。");
                return;
            }

            string ext = NormalizeUserExtension(input);

            if (string.IsNullOrWhiteSpace(ext))
            {
                _dialogService.ShowWarning("后缀不能为空。");
                return;
            }

            var options = new RenameOptions
            {
                OperationType = "AddExtension",
                TargetExtension = ext,
                DeleteExtensionCount = 1,
                ConflictAction = Settings.ConflictAction,
                PreviewBeforeRename = true,
                UnknownFormatAction = Settings.UnknownFormatAction
            };

            await RenameByOptionsAsync(options);
        }

        internal async Task ReplaceExtensionAsync()
        {
            string defaultExt = string.IsNullOrWhiteSpace(Settings.DefaultExtension)
                ? ".zip"
                : Settings.DefaultExtension;

            string? input = ShowTextInputDialog(
                "批量替换最后一个后缀",
                "请输入新的后缀。\n例如：.zip、.rar、.7z、.jpg、.bin、.001\n\n示例：test.jpg -> test.zip",
                defaultExt);

            if (string.IsNullOrWhiteSpace(input))
            {
                AppendLog("INFO", "用户取消替换后缀。");
                return;
            }

            string ext = NormalizeUserExtension(input);

            if (string.IsNullOrWhiteSpace(ext))
            {
                _dialogService.ShowWarning("后缀不能为空。");
                return;
            }

            var options = new RenameOptions
            {
                OperationType = "ReplaceLastExtension",
                TargetExtension = ext,
                DeleteExtensionCount = 1,
                ConflictAction = Settings.ConflictAction,
                PreviewBeforeRename = true,
                UnknownFormatAction = Settings.UnknownFormatAction
            };

            await RenameByOptionsAsync(options);
        }

        internal async Task DeleteLastExtensionAsync()
        {
            bool confirm = _dialogService.ShowConfirm(
                "确定要删除选中文件的最后一个后缀吗？\n\n例如：\n" +
                "test.rar.jpg -> test.rar\n" +
                "test.zip -> test");

            if (!confirm)
            {
                AppendLog("INFO", "用户取消删除最后一个后缀。");
                return;
            }

            var options = new RenameOptions
            {
                OperationType = "DeleteLastExtension",
                TargetExtension = string.Empty,
                DeleteExtensionCount = 1,
                ConflictAction = Settings.ConflictAction,
                PreviewBeforeRename = true,
                UnknownFormatAction = Settings.UnknownFormatAction
            };

            await RenameByOptionsAsync(options);
        }

        internal async Task DeleteMultipleExtensionsAsync()
        {
            string? input = ShowTextInputDialog(
                "删除多个后缀",
                "请输入要删除的后缀数量。\n\n例如输入 2：\n" +
                "test.rar.pdf.jpg -> test.rar\n" +
                "abc.7z.jpg -> abc",
                "2");

            if (string.IsNullOrWhiteSpace(input))
            {
                AppendLog("INFO", "用户取消删除多个后缀。");
                return;
            }

            if (!int.TryParse(input.Trim(), out int count) || count < 1)
            {
                _dialogService.ShowWarning("删除数量必须是大于 0 的整数。");
                return;
            }

            if (count > 20)
            {
                bool confirmLarge = _dialogService.ShowConfirm(
                    $"你输入的删除数量是 {count}，数值较大，确定继续吗？");

                if (!confirmLarge)
                {
                    return;
                }
            }

            var options = new RenameOptions
            {
                OperationType = "DeleteMultipleExtensions",
                TargetExtension = string.Empty,
                DeleteExtensionCount = count,
                ConflictAction = Settings.ConflictAction,
                PreviewBeforeRename = true,
                UnknownFormatAction = Settings.UnknownFormatAction
            };

            await RenameByOptionsAsync(options);
        }

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

                await _renameService.ExecuteRenameAsync(
                    selectedPreviewItems,
                    Tasks);

                foreach (RenamePreviewItem item in selectedPreviewItems)
                {
                    AppendLog("INFO",
                        $"{item.OriginalFileName} -> {item.NewFileName}，状态：{item.Status}，错误：{item.ErrorMessage}");
                }

                await ScanTasksAsync();

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

        private string? ShowTextInputDialog(string title, string message, string defaultValue)
        {
            var window = new Window
            {
                Title = title,
                Width = 460,
                Height = 230,
                MinWidth = 420,
                MinHeight = 220,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Owner = Application.Current.MainWindow,
                Background = System.Windows.Media.Brushes.White
            };

            var root = new System.Windows.Controls.Grid
            {
                Margin = new Thickness(16)
            };

            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
            {
                Height = System.Windows.GridLength.Auto
            });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
            {
                Height = System.Windows.GridLength.Auto
            });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
            {
                Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
            });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
            {
                Height = System.Windows.GridLength.Auto
            });

            var textBlock = new System.Windows.Controls.TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            System.Windows.Controls.Grid.SetRow(textBlock, 0);
            root.Children.Add(textBlock);

            var textBox = new System.Windows.Controls.TextBox
            {
                Text = defaultValue ?? string.Empty,
                Height = 30,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };
            System.Windows.Controls.Grid.SetRow(textBox, 1);
            root.Children.Add(textBox);

            var hint = new System.Windows.Controls.TextBlock
            {
                Text = "提示：输入 zip 会自动变成 .zip；输入 .rar 会保持 .rar。",
                Foreground = System.Windows.Media.Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            System.Windows.Controls.Grid.SetRow(hint, 2);
            root.Children.Add(hint);

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var okButton = new System.Windows.Controls.Button
            {
                Content = "确定",
                Width = 86,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };

            var cancelButton = new System.Windows.Controls.Button
            {
                Content = StatusText.OpCancel,
                Width = 86,
                Height = 30,
                IsCancel = true
            };

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

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);

            System.Windows.Controls.Grid.SetRow(buttonPanel, 3);
            root.Children.Add(buttonPanel);

            window.Content = root;

            window.Loaded += (_, _) =>
            {
                textBox.Focus();
                textBox.SelectAll();
            };

            bool? result = window.ShowDialog();

            if (result == true)
            {
                return textBox.Text;
            }

            return null;
        }
    }
}
