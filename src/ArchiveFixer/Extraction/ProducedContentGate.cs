using System;
using System.IO;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 「这一趟**真的解出东西了吗**」的**唯一出口**（用户 2026-10-01 真机 `giu910`）。
    ///
    /// <para><b>它解决的是什么</b>：候选循环过去把"密码错"与"密码对、个别文件坏了"当成同一件事
    /// （都继续试下一个候选），而这两件事的**签名完全不同**：</para>
    /// <list type="bullet">
    /// <item><description><b>密码错</b>：第一份数据就过不去（7-Zip 在加密流上判死）——
    /// 产物目录里只留 0 字节桩 / 至多一个文件。这一档**必须继续试下一个候选**
    /// （正确的密码可能排在后面，真机出过"11 个候选只试了第 1 个"的坑）。</description></item>
    /// <item><description><b>密码对、个别文件坏了</b>：整个包基本都解了出来（真机那一单 557 个文件、
    /// 只死在一个 mp4 的 CRC 上）—— 这一档再试密码**没有任何意义**，密码已经被这一趟解压本身证明了。
    /// 老口径不认这件事，于是"解完 13 分钟再回去试 7 个候选"、13 分钟的产物全扔、最后报「密码错误」。</description></item>
    /// </list>
    ///
    /// <para><b>门槛 = 至少 <see cref="MinimumFiles"/> 个非空文件、且字节数 &gt; 0</b>：
    /// 一个文件 / 全是零字节桩 / 数不出来 ⇒ 一律按老口径**继续试候选**（保守档，⛔ 不许放宽）。</para>
    ///
    /// <para>⛔ <b>两条跑解压的路都必须问它</b>：<c>ExtractionCoordinator</c>（单层）与
    /// <c>RecursiveExtractor</c>（递归）。2026-10-01 那一版只接在单层那条路上，而真机 `giu910`
    /// 走的是**递归**那条（`SingleChain`）—— 用户重跑一次，13 分钟又白扔了一遍、还是报「密码错误」。
    /// 这一条就是那次漏接的教训：判据只此一处，⛔ 别再各写一份。</para>
    /// </summary>
    public static class ProducedContentGate
    {
        /// <summary>
        /// "密码已被这一趟解压证实"至少要有这么多个非空文件。
        /// 取 2 而不是 1：只解出一个文件与"错密码"的签名分不开（真机那 10 个错候选里就有留下一个文件的）。
        /// </summary>
        public const int MinimumFiles = 2;

        /// <summary>
        /// 数一数 <paramref name="directory"/> 里**真解出来的东西**（非空文件数与字节数），
        /// 并回答"够不够证明密码是对的"。
        ///
        /// <para>只数事实，⛔ 不比任何引擎文案（引擎措辞会随版本变，事实不会）。</para>
        ///
        /// <para>读不到（目录不在 / 被占用 / 权限）一律 <c>false</c>：
        /// 那只会让这一次退回老口径（继续试候选），⛔ 不会把"数不出来"当成"密码已证实"。</para>
        /// </summary>
        public static bool TryMeasure(string? directory, out int files, out long bytes)
        {
            files = 0;
            bytes = 0;

            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return false;
                }

                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    long length;

                    try
                    {
                        length = new FileInfo(file).Length;
                    }
                    catch
                    {
                        continue;
                    }

                    if (length <= 0)
                    {
                        continue;   // 0 字节桩文件不算"解出了东西"（错密码的典型签名）
                    }

                    files++;
                    bytes += length;
                }
            }
            catch
            {
                return false;   // 数不出来 ⇒ 按老口径办（继续试候选），宁可不省这一步
            }

            return files >= MinimumFiles && bytes > 0;
        }
    }
}
