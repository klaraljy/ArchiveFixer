using ArchiveFixer.Helpers;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction;

/// <summary>
/// **特定解压的成品发布边界**：工作区里的 layer 是隐藏的中间物，最终成品目录必须是可见的产品。
///
/// <para>为什么需要它：最后一层没有单一包裹目录时，发布必须把 <c>layerRoot</c> 自己
/// <see cref="Directory.Move(string, string)"/> 到输出根。Windows 会把目录的 Hidden 属性
/// 一起搬过去，用户就会看到成品目录被隐藏（2026-11-15 真机 NO.044/NO.045）。
/// 本类把“成品命名、单包裹展平/flat-root、目录移动、成品根属性规范化”收在一个边界里；
/// runner 不再自己移动目录，也不在调用点散落属性修复。</para>
///
/// <para>契约：只清最终成品根目录的 Hidden；其它属性（ReadOnly/Archive 等）和内容物属性
/// 一律不动；移动仍是同卷 <see cref="Directory.Move(string, string)"/>，保留原子性与性能。</para>
/// </summary>
internal sealed class SpecificExtractionOutputPublisher
{
    private readonly Action<string>? _log;

    internal SpecificExtractionOutputPublisher(Action<string>? log)
    {
        _log = log;
    }

    /// <summary>
    /// 把这一层的成品从工作区发布到输出根，返回真实落点（同名冲突时可能带自动改名后缀）。
    /// </summary>
    internal string Publish(string lastArchivePath, string layerRoot, string outputRoot)
    {
        string name = SpecificOutputNaming.Resolve(lastArchivePath, layerRoot);
        string finalPath = Path.Combine(outputRoot, name);

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath) ?? ".");

        if (Directory.Exists(finalPath))
        {
            finalPath = SafePathHelper.AutoRenameDirectoryPath(finalPath);
        }

        string source = ResolveSourceDirectory(layerRoot);

        Directory.Move(source, finalPath);

        EnsureProductRootVisible(finalPath);

        return finalPath;
    }

    /// <summary>最后一层只有一个包裹文件夹且没有散文件 ⇒ 用这个文件夹；否则用 layer 根。</summary>
    private static string ResolveSourceDirectory(string layerRoot)
    {
        List<string> entries = Directory
            .GetFileSystemEntries(layerRoot)
            .Where(path => !WorkspaceTree.IsWorkspaceDirectoryName(path))
            .ToList();

        List<string> directories = entries.Where(Directory.Exists).ToList();

        return directories.Count == 1
               && !entries.Any(File.Exists)
               && !Directory.EnumerateFiles(layerRoot, "*", SearchOption.TopDirectoryOnly).Any()
            ? directories[0]
            : layerRoot;
    }

    /// <summary>
    /// 只清最终成品根目录的 Hidden；失败只记 WARN，不让“属性清不掉”把已经落地的成品判失败。
    /// </summary>
    private void EnsureProductRootVisible(string finalPath)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(finalPath);

            if ((attributes & FileAttributes.Hidden) != 0)
            {
                File.SetAttributes(finalPath, attributes & ~FileAttributes.Hidden);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"成品目录清除隐藏属性失败：{finalPath}（{ex.Message}）");
        }
    }
}
