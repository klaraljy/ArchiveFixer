using ArchiveFixer.Storage;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ArchiveFixer.Engines.WinRar
{
    /// <summary>
    /// 为双面文件创建一个**同卷硬链接别名**，只改 WinRAR 看到的后缀，不复制、不改源文件名。
    /// 别名目录必须与源文件在同一卷；调用方负责选择目录并在 finally 删除别名。
    /// </summary>
    internal static class WinRarHardlinkAlias
    {
        /// <summary>创建别名；目标已存在 / 跨卷 / 文件系统不支持时返回 false，不覆盖任何东西。</summary>
        public static bool TryCreate(
            string sourcePath,
            string aliasPath,
            out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(aliasPath))
            {
                error = "源文件或别名路径为空";
                return false;
            }

            sourcePath = Path.GetFullPath(sourcePath);
            aliasPath = Path.GetFullPath(aliasPath);

            if (!File.Exists(sourcePath))
            {
                error = "源文件不存在：" + sourcePath;
                return false;
            }

            if (File.Exists(aliasPath) || Directory.Exists(aliasPath))
            {
                error = "别名目标已存在：" + aliasPath;
                return false;
            }

            string sourceRoot = Path.GetPathRoot(sourcePath) ?? string.Empty;
            string aliasRoot = Path.GetPathRoot(aliasPath) ?? string.Empty;

            if (!string.Equals(sourceRoot, aliasRoot, StringComparison.OrdinalIgnoreCase))
            {
                error = "硬链接必须与源文件同卷";
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(aliasPath)!);
            }
            catch (Exception ex)
            {
                error = "无法创建别名目录：" + ex.Message;
                return false;
            }

            if (!CreateHardLink(aliasPath, sourcePath, IntPtr.Zero))
            {
                int code = Marshal.GetLastWin32Error();
                error = $"CreateHardLink 失败（Win32 {code}）：{new System.ComponentModel.Win32Exception(code).Message}";
                return false;
            }

            return true;
        }

        /// <summary>删除别名；只删我们创建的那一个路径，绝不递归、绝不碰源文件。</summary>
        public static void TryDelete(string? aliasPath)
        {
            if (string.IsNullOrWhiteSpace(aliasPath))
            {
                return;
            }

            try
            {
                if (File.Exists(aliasPath))
                {
                    FileOperations.Delete(aliasPath);
                }
            }
            catch
            {
                // 清理失败不影响任务结论；上层按自己的清理路径处理。
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(
            string fileName,
            string existingFileName,
            IntPtr securityAttributes);
    }
}
