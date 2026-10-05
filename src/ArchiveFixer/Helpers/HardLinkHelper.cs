using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ArchiveFixer.Helpers
{
    /// <summary>
    /// **硬链接的唯一出口**（<c>CreateHardLinkW</c> 只许出现在这个文件里）。
    ///
    /// <para><b>为什么要有它</b>：这一档动作是"给同一份字节多起一个名字"——零字节、瞬时、**原文件一个字节不动、
    /// 名字也不改**（删掉链接不影响数据）。本项目已经在三处各写了一遍同样的 P/Invoke 声明
    /// （分卷试拼 <c>SplitVolumeAssembler</c> / 试开校验 <c>VolumeProbeVerifier</c> / 打包前的暂存
    /// <c>PackingService</c>），跨盘 zip 收卷又要用第四次 —— 按 §9.5「同一件事只有一个出口」收在这里，
    /// 那三处转调本类（⛔ 判据与语义一个字没改）。</para>
    ///
    /// <para>⛔ **只在同一卷上做**：<see cref="CanHardLink"/> 判据 = 盘根逐字相同。跨盘 ⇒ 不做
    /// （⛔ 绝不改成复制大文件 —— 那既慢又占一份新空间）。</para>
    /// </summary>
    internal static class HardLinkHelper
    {
        /// <summary>这个文件能不能在 <paramref name="targetDirectory"/> 里建硬链接（判据 = 同一卷）。</summary>
        public static bool CanHardLink(string? sourcePath, string? targetDirectory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourcePath)
                    || string.IsNullOrWhiteSpace(targetDirectory)
                    || !File.Exists(sourcePath))
                {
                    return false;
                }

                string sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourcePath)) ?? string.Empty;
                string targetRoot = Path.GetPathRoot(Path.GetFullPath(targetDirectory)) ?? string.Empty;

                return sourceRoot.Length > 0
                       && string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 给 <paramref name="existingPath"/> 在 <paramref name="linkPath"/> 上再起一个名字。
        /// 目标已存在 / 不同卷 / 系统调用失败 ⇒ 返回 false（⛔ 绝不覆盖）。
        /// </summary>
        public static bool TryCreateHardLink(string linkPath, string existingPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(linkPath)
                    || string.IsNullOrWhiteSpace(existingPath)
                    || File.Exists(linkPath))
                {
                    return false;
                }

                return CreateHardLinkW(linkPath, existingPath, IntPtr.Zero);
            }
            catch
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLinkW(
            string lpFileName,
            string lpExistingFileName,
            IntPtr lpSecurityAttributes);
    }
}
