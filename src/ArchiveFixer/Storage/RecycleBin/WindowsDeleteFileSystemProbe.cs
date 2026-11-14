using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ArchiveFixer.Storage;
/// <summary>真实文件系统的探针。</summary>
public sealed class WindowsDeleteFileSystemProbe : IDeleteFileSystemProbe
{
    public static WindowsDeleteFileSystemProbe Instance { get; } = new();

    public FileAttributes? TryGetAttributes(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return File.GetAttributes(path);
        }
        catch
        {
            // 不存在、路径非法、权限不足…… 全部收敛成"读不到"。
            return null;
        }
    }

    public bool TryListChildren(string? directory, out IReadOnlyList<string> children)
    {
        children = Array.Empty<string>();

        try
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            children = Directory.GetFileSystemEntries(directory);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryListChildDirectories(string? directory, out IReadOnlyList<string> directories)
    {
        directories = Array.Empty<string>();

        try
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            directories = Directory.GetDirectories(directory);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
