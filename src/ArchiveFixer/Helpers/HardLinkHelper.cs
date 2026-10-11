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

                bool created = CreateHardLinkW(linkPath, existingPath, IntPtr.Zero);

                if (created)
                {
                    Trace(linkPath, existingPath);
                }

                return created;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// **取证钩子**（默认一个字节都不写）：环境变量 <c>ARCHIVEFIXER_HARDLINK_PROBE</c> 指向一个文件时，
        /// 每建一条硬链接就往里追加一行 <c>&lt;新名字&gt; &lt;- &lt;老名字&gt;</c>。
        ///
        /// <para><b>为什么要有它</b>（2026-10-11）：用户那条口径「**去掉暗链当出口**」要回答的正是
        /// "这一条链接建在哪 —— 用户的源目录里，还是我们自己的产出树里"。这件事**只能看事实**：
        /// 本仓库给无头环台 `_tmp\ArchiveFixer\cycle.ps1` 早就设了这个环境变量、也早就把旧文件删干净了，
        /// 可**写入端在某个时候被删掉了、谁都没发现** —— 环台上跑出来的日志里一个链接都看不见，
        /// 于是"没建链接"与"钩子坏了"根本分不开（实测：`ARCHIVEFIXER_HARDLINK_PROBE` 在整个仓库里
        /// **0 处引用**，文件根本没被创建过）。⇒ 这里把写入端补回来。</para>
        ///
        /// <para>⛔ 它**只写日志**：不参与任何判据、不改返回值、失败一律吞掉（取证不许影响产品行为）。
        /// ⛔ 没设那个环境变量 ⇒ 连一次 <c>File.AppendAllText</c> 都不会发生（默认档零开销、零副作用）。</para>
        /// </summary>
        private static void Trace(string linkPath, string existingPath)
        {
            try
            {
                string? probe = Environment.GetEnvironmentVariable("ARCHIVEFIXER_HARDLINK_PROBE");

                if (string.IsNullOrWhiteSpace(probe))
                {
                    return;
                }

                File.AppendAllText(
                    probe,
                    linkPath + " <- " + existingPath + Environment.NewLine);
            }
            catch
            {
                // 取证失败绝不影响"链接已经建成了"这条事实。
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLinkW(
            string lpFileName,
            string lpExistingFileName,
            IntPtr lpSecurityAttributes);
    }
}
