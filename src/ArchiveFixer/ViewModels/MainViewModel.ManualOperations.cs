using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.Views;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArchiveFixer.ViewModels
{
    public partial class MainViewModel
    {
        public bool HasSelectedTask => _selectedTask != null;

        /// <summary>当前行有任务，或只勾选了一个任务时，手动操作入口可用。</summary>
        public bool CanOpenManualOperation =>
            SelectedTask != null || Tasks.Count(task => task != null && task.IsSelected) == 1;

        private ArchiveTask? _manualDistributionTask;

        private void SubscribeManualDistributionTask(ArchiveTask? task)
        {
            if (_manualDistributionTask != null)
            {
                _manualDistributionTask.PropertyChanged -= ManualDistributionTask_PropertyChanged;
            }

            _manualDistributionTask = task;

            if (task != null)
            {
                task.PropertyChanged += ManualDistributionTask_PropertyChanged;
            }
        }

        private void ManualDistributionTask_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ArchiveTask.OutputPath)
                or nameof(ArchiveTask.CurrentPath)
                or nameof(ArchiveTask.Status)
                or nameof(ArchiveTask.StatusDisplayText)
                or nameof(ArchiveTask.Outcome))
            {
                RefreshManualDistribution();
            }
        }

        /// <summary>
        /// 手动弹窗顶部提示：单包告诉用户按步骤走；分卷组说明入口包与落点。
        /// </summary>
        public string ManualGuideText
        {
            get
            {
                ArchiveTask? task = SelectedTask;

                if (task == null)
                {
                    return "先在主界面选中一个任务。";
                }

                if (task.VolumePaths.Count > 1 || task.IsVolumeGroup || task.IsVolumeGroupFollower)
                {
                    string entry = task.VolumePaths.Count > 0
                        ? task.VolumePaths[0]
                        : task.CurrentPath;

                    return "分卷组：入口包是 "
                        + Path.GetFileName(entry)
                        + "。先看内容 / 试密码，再只解压这一层；解压会落到入口包所在目录。";
                }

                return "单一压缩包：建议按 1 看内容 → 2 试密码 → 3 只解压这一层 → 4 打开输出目录 的顺序操作。";
            }
        }

        // 手动操作弹窗里的“目录分布”：只读参考，不参与操作目标判定。
        public string ManualDistributionTitle { get; private set; } = "文件分布";

        public string ManualDistributionPath { get; private set; } = string.Empty;

        public string ManualDistributionEmptyText { get; private set; } = string.Empty;

        public IReadOnlyList<ManualDistributionEntry> ManualDistributionEntries { get; private set; }
            = Array.Empty<ManualDistributionEntry>();

        /// <summary>手动弹窗打开时按基名推测的同组文件（只读候选，不直接移动/删除）。</summary>
        public IReadOnlyList<ManualDistributionEntry> ManualVolumeCandidates { get; private set; }
            = Array.Empty<ManualDistributionEntry>();

        public string ManualVolumeSearchText { get; private set; } = "尚未查找同组文件。";

        public ICommand FindVolumeSiblingsCommand { get; }

        /// <summary>
        /// 手动操作弹窗打开/切换当前任务时，刷新目录分布。
        /// 没指定输出位置 = 看源包所在目录；指定了输出位置 = 看那个目录，但这里只展示，不操作。
        /// </summary>
        private void RefreshManualDistribution()
        {
            ManualDistributionTitle = "文件分布";
            ManualDistributionPath = string.Empty;
            ManualDistributionEmptyText = string.Empty;
            ManualDistributionEntries = Array.Empty<ManualDistributionEntry>();
            ManualVolumeCandidates = Array.Empty<ManualDistributionEntry>();
            ManualVolumeSearchText = "尚未查找同组文件。";

            ArchiveTask? task = SelectedTask;

            if (task == null)
            {
                ManualDistributionEmptyText = "先在主界面选中一个任务。";
                NotifyManualDistributionChanged();
                return;
            }

            bool useOutput = !Settings.ExtractToOriginalDirectory
                && !string.IsNullOrWhiteSpace(Settings.CustomOutputDirectory);

            string sourceEntry = task.VolumePaths.Count > 0
                ? task.VolumePaths[0]
                : task.CurrentPath;

            string? root = useOutput
                ? Settings.CustomOutputDirectory
                : Path.GetDirectoryName(sourceEntry);

            ManualDistributionTitle = useOutput
                ? "输出位置文件分布"
                : "原位置文件分布（入口包所在目录）";
            ManualDistributionPath = root ?? string.Empty;

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                ManualDistributionEmptyText = useOutput
                    ? "指定的输出目录尚不存在；不会为了展示而创建它。"
                    : "找不到当前源包所在目录。";
                NotifyManualDistributionChanged();
                return;
            }

            try
            {
                var entries = new List<ManualDistributionEntry>();

                foreach (FileSystemInfo info in new DirectoryInfo(root)
                             .EnumerateFileSystemInfos()
                             .OrderBy(item => item is DirectoryInfo ? 0 : 1)
                             .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                             .Take(500))
                {
                    bool directory = info is DirectoryInfo;
                    entries.Add(new ManualDistributionEntry
                    {
                        Name = info.Name,
                        Kind = directory ? "文件夹" : "文件",
                        SizeText = directory ? "-" : FormatFileSize(info),
                        ModifiedText = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                        FullPath = info.FullName
                    });
                }

                ManualDistributionEntries = entries;
                ManualDistributionEmptyText = entries.Count == 0 ? "这个目录是空的。" : string.Empty;
            }
            catch (Exception ex)
            {
                ManualDistributionEmptyText = "读取目录失败：" + ex.Message;
            }

            NotifyManualDistributionChanged();
        }

        private async Task FindVolumeSiblingsAsync()
        {
            ArchiveTask? task = SelectedTask;

            if (task == null)
            {
                return;
            }

            ManualVolumeSearchText = "正在按基名查找同组文件…";
            ManualVolumeCandidates = Array.Empty<ManualDistributionEntry>();
            NotifyManualVolumeChanged();

            string entryPath = task.VolumePaths.Count > 0
                ? task.VolumePaths[0]
                : task.CurrentPath;
            string? entryDirectory = Path.GetDirectoryName(entryPath);

            var roots = new List<string>();

            void AddRoot(string? root)
            {
                if (!string.IsNullOrWhiteSpace(root)
                    && Directory.Exists(root)
                    && !roots.Contains(root, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(root);
                }
            }

            // 先扫入口包所在目录的上一级（可以覆盖同一下载目录下的多个子文件夹），
            // 再扫用户指定输出目录；都只读，不移动任何文件。
            AddRoot(entryDirectory);
            AddRoot(Path.GetDirectoryName(entryDirectory));
            AddRoot(string.IsNullOrWhiteSpace(Settings.CustomOutputDirectory)
                ? null
                : Settings.CustomOutputDirectory);

            try
            {
                List<string> found = await Task.Run(() =>
                {
                    var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (string root in roots)
                    {
                        try
                        {
                            foreach (string file in Directory.EnumerateFiles(
                                         root,
                                         "*",
                                         SearchOption.AllDirectories))
                            {
                                if (paths.Count >= 20000)
                                {
                                    break;
                                }

                                if (LegacyVolumeNames.BelongsToSameGroup(
                                        Path.GetFileName(file),
                                        Path.GetFileName(task.CurrentPath)))
                                {
                                    paths.Add(file);
                                }
                            }
                        }
                        catch
                        {
                            // 单个目录无权限/被占用：跳过它，其他根继续。
                        }

                        if (paths.Count >= 20000)
                        {
                            break;
                        }
                    }

                    paths.Add(task.CurrentPath);
                    return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
                }).ConfigureAwait(true);

                var entries = new List<ManualDistributionEntry>(found.Count);

                foreach (string path in found)
                {
                    try
                    {
                        var file = new FileInfo(path);
                        entries.Add(new ManualDistributionEntry
                        {
                            Name = file.Name,
                            Kind = path.Equals(task.CurrentPath, StringComparison.OrdinalIgnoreCase)
                                ? "当前"
                                : "同组",
                            SizeText = FormatFileSize(file),
                            ModifiedText = file.Exists
                                ? file.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                                : "-",
                            FullPath = path
                        });
                    }
                    catch
                    {
                    }
                }

                ManualVolumeCandidates = entries;
                ManualVolumeSearchText = entries.Count <= 1
                    ? "没有按基名找到其他同组文件。"
                    : $"按基名找到 {entries.Count} 个同组文件（含当前）；只是提示，不会自动搬移。";
            }
            catch (Exception ex)
            {
                ManualVolumeCandidates = Array.Empty<ManualDistributionEntry>();
                ManualVolumeSearchText = "查找同组失败：" + ex.Message;
            }

            NotifyManualVolumeChanged();
        }

        private void NotifyManualVolumeChanged()
        {
            OnPropertyChanged(nameof(ManualVolumeCandidates));
            OnPropertyChanged(nameof(ManualVolumeSearchText));
        }

        private static string FormatFileSize(FileSystemInfo info)
        {
            try
            {
                if (info is not FileInfo file)
                {
                    return "-";
                }

                string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
                double value = file.Length;
                int unit = 0;

                while (value >= 1024 && unit < units.Length - 1)
                {
                    value /= 1024;
                    unit++;
                }

                return unit == 0
                    ? $"{value:0} {units[unit]}"
                    : $"{value:0.#} {units[unit]}";
            }
            catch
            {
                return "-";
            }
        }

        private void NotifyManualDistributionChanged()
        {
            OnPropertyChanged(nameof(ManualDistributionTitle));
            OnPropertyChanged(nameof(ManualDistributionPath));
            OnPropertyChanged(nameof(ManualDistributionEmptyText));
            OnPropertyChanged(nameof(ManualDistributionEntries));
            OnPropertyChanged(nameof(ManualGuideText));
            OnPropertyChanged(nameof(ManualVolumeCandidates));
            OnPropertyChanged(nameof(ManualVolumeSearchText));
        }

        public ICommand AddFilesCommand { get; }
        public ICommand AddFolderCommand { get; }

        /// <summary>
        /// 「文件 → 追加到列表 → 追加文件…」（用户 2026-09-24 第 12 条补拍）。
        ///
        /// <para>语义 = **旧行为**（保留现有任务，只把新选的加到末尾）。它有意做成菜单里的一条
        /// **显式**入口：默认的「添加」是替换，想追加就必须自己说出来 ——
        /// 用户原话是"每次我新选择了其他的，无论是什么，你都要将列表彻底清空"，
        /// 所以"追加"只能是显式动作，不能是默认动作。</para>
        /// </summary>
        public ICommand AppendFilesCommand { get; }

        /// <summary>「文件 → 追加到列表 → 追加文件夹…」。</summary>
        public ICommand AppendFolderCommand { get; }

        public ICommand ScanCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand RemoveSelectedCommand { get; }

        public ICommand SmartRenameCommand { get; }
        public ICommand ChangeNameCommand { get; }

        public ICommand StartExtractCommand { get; }

        /// <summary>
        /// 手动档「解压到当前文件夹」（用户 2026-09-27 拍板新加的一个按钮）：
        /// 与 <see cref="StartExtractCommand"/> 同一条管线，只多一个"这次不建包名那一层"的运行期标记。
        ///
        /// <para>⛔ 它**不是**"只解压"的别名：两者的落点差别写在
        /// <see cref="ExtractionCoordinator.StartExtractAsync(bool)"/> 上（<c>111\222.rar</c> →
        /// <c>111\内容物</c> vs <c>111\222\内容物</c>）。一键处理 / 批量**永远不会**走这一档。</para>
        /// </summary>
        public ICommand ExtractIntoSourceFolderCommand { get; }

        public ICommand StopCommand { get; }
        public ICommand CancelCurrentCommand { get; }
        public ICommand PauseAllOperationsCommand { get; }

        public ICommand OpenSettingsCommand { get; }
        public ICommand OpenPasswordListCommand { get; }

        /// <summary>
        /// 打包入口（**⑤「打包」选项卡**，整页在那儿；用户 2026-09-22 需求第 10 条）。
        /// 打包是独立的一条流水线（7z 分卷 → 外层加密 rar），不依赖任务列表，所以没有勾选之类的前置条件。
        ///
        /// <para>2026-09-24 第 11 条之后它不再打开窗口，只负责把界面切到⑤页
        /// （原来的打包窗口已退休，界面整页搬进了选项卡）。</para>
        /// </summary>
        public ICommand OpenPackingCommand { get; }

        /// <summary>③页那句"其余物现状"旁边的按钮：跳到②页底部红区（危险模式）。</summary>

        /// <summary>导入密码本（记住路径，下次启动自动加载）。用户已多次要求：导入一次就够。</summary>
        public ICommand ImportPasswordBookCommand { get; }
        public ICommand ExportLogCommand { get; }

        /// <summary>导出当前操作的完整诊断包（zip，给开发者排查）。</summary>
        public ICommand ExportDiagnosticBundleCommand { get; }

        /// <summary>导出**全部历史日志**（日志目录里每一份；用户 2026-09-25 第 38 条降级成显式入口）。</summary>
        public ICommand ExportAllLogsCommand { get; }
        public ICommand CopyFailedListCommand { get; }

        /// <summary>把失败清单导出成 txt（M3：能说清每个失败为什么失败，且能带走）。</summary>
        public ICommand ExportFailedListCommand { get; }
        public ICommand OpenOutputDirectoryCommand { get; }
        public ICommand SelectOutputDirectoryCommand { get; }

        /// <summary>
        /// ①页「输出位置」那一格右键菜单的「复制完整路径」（用户 2026-09-25 第 27 条）。
        ///
        /// <para>为什么要有它：那一行显示的是**中间省略**过的路径（"我们最好能够看到完整的解压地址"，
        /// 但一行放不下 200 个字符），要拿去粘贴的人得能拿到全文 —— ToolTip 能看，这个能复制。</para>
        /// </summary>
        public ICommand CopyOutputLocationCommand { get; }
        public ICommand OpenLogDirectoryCommand { get; }

        /// <summary>打开递归解压的工作区目录（中断后残留的中间产物在这里）。</summary>
        public ICommand OpenWorkDirectoryCommand { get; }

        /// <summary>
        /// 清理工作区残留（用户 2026-09-24 第 22 条）：列出每个残留的名字 / 体积，**确认之后**才删。
        ///
        /// <para>为什么值得单开一条：残留是刻意保留的（失败 / 取消时那是用户唯一的一份产物线索），
        /// 但以前只有一行日志、没有入口，用户实测攒到 5.7 GB 才发现。</para>
        /// </summary>
        public ICommand ClearWorkspaceLeftoversCommand { get; }

        /// <summary>
        /// 删除当前勾选任务自己那一份「其余物」（默认移入回收站，激进档彻底删除）。
        ///
        /// ⚠ 作用域只到本任务那一份：包有自己的目录时是 <c>&lt;输出目录&gt;\其余物\</c>，
        /// 共享输出目录时是 <c>&lt;共享根&gt;\其余物\&lt;本任务包基名&gt;\</c> —— 绝不碰别的包的目录。
        /// </summary>
        public ICommand CleanProcessArtifactsCommand { get; }

        /// <summary>
        /// 删除当前输出目录下的**全部**其余物（会影响同目录里的所有包，确认框里写明个数）。
        /// </summary>
        public ICommand CleanAllProcessArtifactsCommand { get; }

        /// <summary>清理当前任务输出根下"任意层级都没有文件"的空文件夹。</summary>
        public ICommand CleanEmptyFoldersCommand { get; }

        public ICommand RemoveTaskCommand { get; }

        /// <summary>
        /// 移除**勾选的**任务（用户 2026-09-24 第 15 条："列表要能删无用物"）。
        /// 只动列表，源文件不动；移除错了再添加一次就回来，所以不做二次确认。
        /// </summary>
        public ICommand RemoveCheckedTasksCommand { get; }

        /// <summary>
        /// 「按建议改名并重试」（用户 2026-09-25 第 41 条）：给"名字被改坏的分卷第一卷"按标准名改名，
        /// 只改名字、不覆盖、不删内容，改完立刻重新识别并重试解压。只在真有可改的任务时才亮。
        /// </summary>
        public ICommand RenameBySuggestionAndRetryCommand { get; }
        public ICommand RescanTaskCommand { get; }
        public ICommand CopyTaskInfoCommand { get; }
        public ICommand CopyTaskPathCommand { get; }
        public ICommand CopyTaskErrorCommand { get; }
        public ICommand OpenTaskDirectoryCommand { get; }

        /// <summary>
        /// 「打开源目录」：把**第一个勾选任务**所在的文件夹在资源管理器里打开（手动那一栏那颗按钮，用户 2026-09-26 批准加）。
        ///
        /// <para>为什么要有它：右键菜单里那条「打开文件所在目录」只对"当前行"生效 ——
        /// 批量处理时用户手上往往没有"当前行"，只想顺手看一眼源目录在哪。</para>
        /// </summary>
        public ICommand OpenCheckedSourceDirectoryCommand { get; }

        /// <summary>
        /// 「看内容」：只列清单（条目数 / 总大小 / 前几条），**不解压、不写盘**（用户 2026-09-26 批准加）。
        /// </summary>
        public ICommand InspectArchiveCommand { get; }

        /// <summary>
        /// 「试密码」：拿候选逐个问引擎"这个能不能开"，报出能开的那个（只显示候选说明，不显示密码）。
        /// </summary>
        public ICommand TryPasswordsCommand { get; }

        // 手动检查器：这些命令只作用于 SelectedTask，**不读勾选**。
        public ICommand InspectCurrentTaskCommand { get; }
        public ICommand TryPasswordsForCurrentTaskCommand { get; }
        public ICommand OpenCurrentSourceDirectoryCommand { get; }
        public ICommand SmartRenameCurrentTaskCommand { get; }
        public ICommand ChangeNameCurrentTaskCommand { get; }
        public ICommand RepairVolumeNameCurrentTaskCommand { get; }
        public ICommand StartExtractCurrentTaskCommand { get; }
        public ICommand ExtractIntoSourceFolderCurrentTaskCommand { get; }
        public ICommand OpenCurrentTaskOutputDirectoryCommand { get; }
        public ICommand CopyCurrentVolumeGroupPathsCommand { get; }
        public ICommand AddCurrentOutputFolderToTasksCommand { get; }
        public ICommand CleanCurrentTaskArtifactsCommand { get; }
        public ICommand RecycleCurrentTaskArtifactsCommand { get; }
        public ICommand CleanCurrentTaskEmptyFoldersCommand { get; }

        public ICommand OpenTaskOutputDirectoryCommand { get; }

        /// <summary>打开当前任务的其余物目录（旧名"过程物"）；没有就提示一句。</summary>
        public ICommand OpenTaskArtifactDirectoryCommand { get; }
        public ICommand ToggleShowPasswordCommand { get; }
        /// <summary>一键处理：识别 → 修正伪装后缀（一次预览确认）→ 按密码本解压 → 一行汇总。</summary>
        public ICommand OneClickProcessCommand { get; }

        /// <summary>
        /// 「继续解」：上一批撞到 10 层硬上限时，接着把剩下那些内层包解下去（用户 2026-09-24 第 16 条追加）。
        ///
        /// <para>它复用的就是一键处理本身（<see cref="OneClickCoordinator.RunAsync"/>）——
        /// 剩下的内层包在上一批结束时已经加进列表并勾好，所以"继续解"不需要另一条流水线。</para>
        /// </summary>
        public ICommand ContinueOneClickCommand { get; }

        public ICommand ResetSettingsCommand { get; }

        /// <summary>
        /// 按当前可用空间算一次"建议并行几个"（用户 2026-09-22 需求第 2 条）。
        /// 结果落在 <see cref="ParallelAdviceText"/> 上 —— 界面直接显示那句话。
        /// </summary>
        public ICommand RefreshParallelAdviceCommand { get; }

        /// <summary>
        /// **危险模式**入口（红色按钮）：确认 → 自测（并发数 × 2 个文件）→ 通过才开启。
        /// 已经开着时点它 = 关闭（关闭是安全方向，不需要二次确认）。
        /// </summary>

    }
}
