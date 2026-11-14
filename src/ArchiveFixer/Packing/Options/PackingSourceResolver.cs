using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.IO;

namespace ArchiveFixer.Packing;

public static class PackingSourceResolver
{
    public static PackingSourceResolution Resolve(string? selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return Fail("请先选择要打包的文件夹（或一个文件）。");
        }

        string full;

        try
        {
            full = Path.GetFullPath(selectedPath);
        }
        catch (Exception ex)
        {
            return Fail($"这个路径用不了（{ex.Message}）。");
        }

        if (Directory.Exists(full))
        {
            string folderName = Path.GetFileName(full.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (string.IsNullOrWhiteSpace(folderName))
            {
                return Fail($"这是盘根，不能直接打包：{full}。请选一个文件夹。");
            }

            return new PackingSourceResolution
            {
                Success = true,
                SourcePath = full,
                Kind = PackingSourceKind.Folder,
                FolderToPack = full,
                SourceName = folderName
            };
        }

        if (File.Exists(full))
        {
            string fileName = Path.GetFileName(full);
            string baseName = Path.GetFileNameWithoutExtension(full);

            if (string.IsNullOrWhiteSpace(baseName))
            {
                return Fail($"这个文件取不出名字（{fileName}），没法给它建同名文件夹。");
            }

            string? parent = Path.GetDirectoryName(full);

            if (string.IsNullOrWhiteSpace(parent))
            {
                return Fail($"这个文件没有所在目录（{full}），没法在它旁边生成文件夹。");
            }

            string wrapper = PackingNaming.ResolveUniqueFolderPath(parent!, baseName);

            return new PackingSourceResolution
            {
                Success = true,
                SourcePath = full,
                Kind = PackingSourceKind.File,
                FolderToPack = wrapper,
                WrapperFolderToCreate = wrapper,
                SourceName = baseName
            };
        }

        return Fail($"这个路径不存在：{full}");
    }

    private static PackingSourceResolution Fail(string reason) => new()
    {
        Success = false,
        Reason = reason
    };
}

/// <summary>命名：装分卷的文件夹 / 最终 rar 的取名规则（用户 2026-09-26 第 5 条）。</summary>

