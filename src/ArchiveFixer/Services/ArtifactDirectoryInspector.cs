using ArchiveFixer.BindingModels;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Services;

/// <summary>
/// 其余物目录的**内容清单**：顶层条目（给用户看的清单）与其中的**源包文件**
/// （删掉之后需要重新下载的那几个）。
///
/// <para>
/// 为什么单独成类：这两件事都只是"看"，判据却会各自演进 —— 清单要排序、要截断；
/// 源包识别要分"任务登记过的精确判据"与"按扩展名兜底"，还牵扯别的模块的口径
/// （"搬源包进其余物"与"删除源包"认的必须是同一批文件）。留在清理流程里，
/// 每改一次识别规则都要动一次删除代码，而删除代码是不可逆的那一侧。
/// </para>
/// <para>
/// 本类只读路径、只读名字：不删除、不移动、不创建任何东西（调用方拿到的只是清单）。
/// </para>
/// </summary>
public static class ArtifactDirectoryInspector
{
    /// <summary>其余物目录里的顶层条目名（目录在前、文件在后，各自按名字排序）。</summary>
    public static IReadOnlyList<string> ListTopLevelEntries(string directory)
    {
        var items = new List<string>();

        items.AddRange(Directory.GetDirectories(directory)
            .Select(FileNameHelper.GetFileName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

        items.AddRange(Directory.EnumerateFiles(directory)
            .Select(FileNameHelper.GetFileName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

        return items;
    }

    /// <summary>
    /// 顶层条目里的**源包文件**（删掉之后要重新下载的那些）。
    ///
    /// <para>
    /// 判据优先级（先精确、后兜底）：
    /// ① 任务自己登记的源文件（分卷组取整组各卷，单文件任务取它自己）—— 这是**精确**判据；
    /// ② 任务一个源文件都没登记（手工构造的任务等）时才退到"扩展名像归档"这个兜底判据。
    /// </para>
    /// <para>
    /// ⚠ 为什么不直接按扩展名全量判：其余物里同时躺着**内层归档**与**分卷**（<c>.7z.001</c> 这类），
    /// 它们由源包生成，删了还能再解出来；把它们也算成"源包"会让警告天天误报，
    /// 真出现源包时用户反而不当回事。分卷组的第一卷**是**源包（任务登记着它），所以仍然会报。
    /// </para>
    /// <para>只认文件、不认目录：目录名匹配归档后缀的（例如 <c>其余物\222\</c>）不构成"源包文件"。</para>
    /// </summary>
    public static IReadOnlyList<string> FindSourcePackageNames(
        string directory,
        IReadOnlyList<string> topLevelItems,
        ArchiveTask? task)
    {
        IReadOnlyList<string> registered = CollectRegisteredSourceFiles(task);
        bool useFallback = registered.Count == 0;
        var packages = new List<string>();

        foreach (string name in topLevelItems)
        {
            if (!SafePathHelper.FileExists(SafePathHelper.Combine(directory, name)))
            {
                continue;
            }

            bool isSourcePackage = useFallback
                ? LooksLikeArchiveFile(name)
                : registered.Any(path => string.Equals(
                    FileNameHelper.GetFileName(path),
                    name,
                    StringComparison.OrdinalIgnoreCase));

            if (isSourcePackage)
            {
                packages.Add(name);
            }
        }

        return packages;
    }

    /// <summary>
    /// 其余物里**源包文件**的精确判据：任务自己登记的源文件清单。
    ///
    /// <para>
    /// 清单只来自任务自身，**不扫目录** —— 与"搬源包进其余物"和"清理源包"两处同一口径
    /// （那两处一个搬、一个删，这边是提示，三处必须认同一批源包）。
    /// 分卷组只认 <see cref="ArchiveTask.VolumePaths"/>：为空说明分组信息不完整，
    /// 与其猜一卷，不如什么都不报（少一个提示好过多一个假警报）。
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> CollectRegisteredSourceFiles(ArchiveTask? task)
    {
        var paths = new List<string>();

        if (task == null)
        {
            return paths;
        }

        IEnumerable<string> candidates = task.IsVolumeGroup
            ? task.VolumePaths
            : new[] { task.CurrentPath };

        foreach (string candidate in candidates)
        {
            string full = SafePathHelper.GetFullPathSafe(candidate);

            if (!string.IsNullOrWhiteSpace(full)
                && !paths.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(full);
            }
        }

        return paths;
    }

    /// <summary>
    /// 兜底判据：名字的扩展名是**归档本体**（<c>.7z</c>/<c>.rar</c>/<c>.zip</c>/<c>.tar.gz</c>…）。
    ///
    /// <para>
    /// 为什么兜底**不认**"伪装成常见文件"的后缀（<c>.jpg</c>/<c>.txt</c>）：那是识别阶段的事，
    /// 到了这个位置文件已经在其余物目录里躺着了，把一个 <c>readme.txt</c> 说成"源包、删了要重新下载"
    /// 是纯粹的误报（用户还会以为程序认错了格式）。宁可漏报一个改坏后缀的源包，
    /// 也不要每次弹窗都喊"含源包文件"。
    /// </para>
    /// </summary>
    private static bool LooksLikeArchiveFile(string fileName)
    {
        string extension = Path.GetExtension(fileName);

        return !string.IsNullOrWhiteSpace(extension)
               && ExtensionHelper.IsKnownArchiveExtension(extension);
    }
}
