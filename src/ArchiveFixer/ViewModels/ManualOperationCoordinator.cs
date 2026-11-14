using ArchiveFixer.Detection;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 手动检查器的文件分布/同组查找；属性留在 MainViewModel 便于 XAML 绑定。
/// </summary>
internal sealed class ManualOperationCoordinator
{
    private readonly MainViewModel _vm;

    public ManualOperationCoordinator(MainViewModel vm)
    {
        _vm = vm;
    }

    internal void RefreshManualDistribution()
    {
        _vm.SetManualDistributionTitle("文件分布");
        _vm.SetManualDistributionPath(string.Empty);
        _vm.SetManualDistributionEmptyText(string.Empty);
        _vm.SetManualDistributionEntries(Array.Empty<ManualDistributionEntry>());
        _vm.SetManualVolumeCandidates(Array.Empty<ManualDistributionEntry>());
        _vm.SetManualVolumeSearchText("尚未查找同组文件。");

        ArchiveTask? task = _vm.SelectedTask;

        if (task == null)
        {
            _vm.SetManualDistributionEmptyText("先在主界面选中一个任务。");
            _vm.NotifyManualDistributionChanged();
            return;
        }

        bool useOutput = !_vm.Settings.ExtractToOriginalDirectory
            && !string.IsNullOrWhiteSpace(_vm.Settings.CustomOutputDirectory);

        string sourceEntry = task.VolumePaths.Count > 0
            ? task.VolumePaths[0]
            : task.CurrentPath;

        string? root = useOutput
            ? _vm.Settings.CustomOutputDirectory
            : Path.GetDirectoryName(sourceEntry);

        _vm.SetManualDistributionTitle(useOutput
            ? "输出位置文件分布"
            : "原位置文件分布（入口包所在目录）");
        _vm.SetManualDistributionPath(root ?? string.Empty);

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _vm.SetManualDistributionEmptyText(useOutput
                ? "指定的输出目录尚不存在；不会为了展示而创建它。"
                : "找不到当前源包所在目录。");
            _vm.NotifyManualDistributionChanged();
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
                    SizeText = directory ? "-" : MainViewModel.FormatFileSize(info),
                    ModifiedText = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                    FullPath = info.FullName
                });
            }

            _vm.SetManualDistributionEntries(entries);
            _vm.SetManualDistributionEmptyText(entries.Count == 0 ? "这个目录是空的。" : string.Empty);
        }
        catch (Exception ex)
        {
            _vm.SetManualDistributionEmptyText("读取目录失败：" + ex.Message);
        }

        _vm.NotifyManualDistributionChanged();
    }

    internal async Task FindVolumeSiblingsAsync()
    {
        ArchiveTask? task = _vm.SelectedTask;

        if (task == null)
        {
            return;
        }

        _vm.SetManualVolumeSearchText("正在按基名查找同组文件…");
        _vm.SetManualVolumeCandidates(Array.Empty<ManualDistributionEntry>());
        _vm.NotifyManualVolumeChanged();

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
        AddRoot(string.IsNullOrWhiteSpace(_vm.Settings.CustomOutputDirectory)
            ? null
            : _vm.Settings.CustomOutputDirectory);

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
                        SizeText = MainViewModel.FormatFileSize(file),
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

            _vm.SetManualVolumeCandidates(entries);
            _vm.SetManualVolumeSearchText(entries.Count <= 1
                ? "没有按基名找到其他同组文件。"
                : $"按基名找到 {entries.Count} 个同组文件（含当前）；只是提示，不会自动搬移。");
        }
        catch (Exception ex)
        {
            _vm.SetManualVolumeCandidates(Array.Empty<ManualDistributionEntry>());
            _vm.SetManualVolumeSearchText("查找同组失败：" + ex.Message);
        }

        _vm.NotifyManualVolumeChanged();
    }
}


