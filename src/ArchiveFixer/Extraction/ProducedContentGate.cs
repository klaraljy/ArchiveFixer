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
    /// <para>⚠ <b>2026-10-12 真机补第二条判据（「每一条都失败」不算证明）</b>：上面那条门槛
    /// 在 <b>RAR4 + 存储方式（<c>-m0</c>）+ 数据加密</b> 的包上会得出**反的**结论 ——
    /// 错密码时 7-Zip 照样把垃圾明文**按原尺寸写满盘**，只是收尾报
    /// <c>CRC Failed in encrypted file. Wrong password?</c>。实测（本机 26.03 + WinRAR 6.11，
    /// 夹具 = <c>Rar.exe a -ma4 -m0 -p&lt;密码&gt;</c>：两个 4 MiB 文件）：</para>
    /// <list type="bullet">
    /// <item><description><b>错密码</b>：退出码 2、<c>DetectedErrorType=WrongPassword</c>、
    /// <c>ReportedSubItemErrors=2</c>、<c>FailedEntryNames=[alpha.bin, beta.bin]</c>、
    /// 盘上 **2 个文件 / 8 388 608 字节**（与对密码**逐个字节同尺寸**）⇒ 老门槛判「密码已证实」；
    /// 于是候选循环当场 <c>break</c>、**正确密码（密码本第 7 位）永远没被试**，
    /// 真机 <c>2222</c> 那条链就死在第 3 层、报「文件损坏」。</description></item>
    /// <item><description><b>对密码</b>：退出码 0、<c>ReportedSubItemErrors=0</c>、
    /// <c>FailedEntryNames=0</c>、同样的 2 个文件 / 同样字节数。</description></item>
    /// </list>
    ///
    /// <para>⇒ 能分开这两者的是**引擎自己报的失败条目数**（那是引擎的事实，不是我们的推断）：
    /// **失败条目数 ≥ 盘上产出的文件数** ⇒ 每一条都没过 ⇒ 这一趟<u>没有</u>证明密码，
    /// 必须继续试候选。真机 <c>giu910</c> 那一档（557 个文件只坏 1 个）里 1 &lt; 557 ⇒ 照旧判"已证实"，
    /// 老行为一个字不变。</para>
    ///
    /// <para>⛔ 拿不到引擎事实（别的引擎 / 手写结果对象 / 老调用点）⇒ 两个计数都是 0 ⇒
    /// 逐字退回老口径，⛔ 不许把"数不出来"当"没证实"以外的东西。</para>
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
        ///
        /// <para>⛔ 不传引擎事实的重载 = 老口径（两个计数按 0 算）。</para>
        /// </summary>
        public static bool TryMeasure(string? directory, out int files, out long bytes) =>
            TryMeasure(directory, reportedSubItemErrors: 0, namedFailedEntries: 0, out files, out bytes);

        /// <summary>
        /// 带上**引擎那一趟的结构化事实**再判一次：见类注释里 2026-10-12 那条（错密码会写出满尺寸垃圾）。
        ///
        /// <para><paramref name="reportedSubItemErrors"/> = 引擎自报的出错子项数
        /// （7-Zip <c>Sub items Errors: N</c> / UnRAR <c>Total errors: N</c>）；
        /// <paramref name="namedFailedEntries"/> = 引擎点名失败的条目数。
        /// 两者都只用来回答一个问题：**这一趟是不是每一条都失败了**。</para>
        /// </summary>
        public static bool TryMeasure(
            string? directory,
            int reportedSubItemErrors,
            int namedFailedEntries,
            out int files,
            out long bytes)
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

            if (files <= 0)
            {
                return false;
            }

            /*
             * 「每一条都失败」——**判据只读引擎给的两个计数**，⛔ 不比文案、⛔ 不猜"密码大概对不对"。
             *
             * 为什么是 `>=`（而不是"大于"）：错密码那一档里，盘上产出的文件数与失败条目数
             * **逐个相等**（实测 2 = 2；真机 `2222` 是 4 = 4）—— 引擎把每一条都点成失败，
             * 因为每一条的 CRC 都对不上。而"密码对、个别文件坏"那一档，失败数必然**小于**产出数
             * （真机 `giu910`：1 < 557）。
             */
            bool everyEntryFailed =
                (reportedSubItemErrors > 0 && reportedSubItemErrors >= files)
                || (namedFailedEntries > 0 && namedFailedEntries >= files);

            return files >= MinimumFiles && bytes > 0 && !everyEntryFailed;
        }
    }
}
