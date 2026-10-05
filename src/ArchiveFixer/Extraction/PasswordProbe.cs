using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Engines;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **先试密码，再解整包**（用户 2026-09-25 第 37 条）。
    ///
    /// <para><b>真机现场</b>：他那个 12.22 GiB 的包（名字可见、条目加密 —— <c>-mhe=off</c> + <c>Encrypted = +</c>）
    /// 用**空密码**当第一个候选跑了整整 **10 分 18 秒**（16:50:11 → 17:00:29），把 12 GiB 垃圾写进工作区，
    /// 最后才报"密码错误"；第二个候选（正确密码）又跑一遍 10 分钟。他原话：
    /// "一个空密码花了我10分钟来试，这才12G要是40G怎么办，这是非常危险非常危险的bug"。</para>
    ///
    /// <para>为什么以前躲不过：7-Zip 对 <c>-mhe=off</c> 的包**列目录不需要密码**，任何候选都能列出来；
    /// 而 `Copy` 方法（stored）+ AES-CBC 的数据要**整条读完**才能比对 CRC —— 于是错密码的代价 = 解一遍整包。
    /// （加密头的包反而快：7-Zip 读头就拒绝了，所以小包一直是"瞬间密码错误"。）</para>
    ///
    /// <para><b>本类的做法</b>：从清单里挑一个**最小的条目**当探针，用候选密码**只解那一个**：
    /// 解开了 = 这个密码是对的（再去解整包）；解不开 = 这个候选不对（**一个字节的整包数据都没动**）。
    /// 挑不出探针（清单里没有"小到值得当探针"的文件）就返回 null，调用方退回老路（整包试解）。</para>
    /// </summary>
    public static class PasswordProbe
    {
        /// <summary>
        /// 探针条目的大小上限（16 MiB）。
        ///
        /// <para>取这个量级：真实资源包里几乎总有一两个说明文件 / 小图（几 KB 到几 MB），
        /// 解它基本是瞬时的；而再大就不划算（探针本身也要读盘）。</para>
        /// </summary>
        public const long MaxProbeEntryBytes = 16L * 1024 * 1024;

        /// <summary>
        /// 值得做预检的最小**声明总量**（64 MiB）。
        ///
        /// <para>小包本来就快（整包解一遍也就一两秒），为它多跑一次引擎调用纯属添乱；
        /// 预检是为"错一次就白跑几分钟甚至几十分钟"的大包准备的。</para>
        /// </summary>
        public const long MinWorthProbingBytes = 64L * 1024 * 1024;

        /// <summary>
        /// 这个包值不值得做"先试密码"的预检。
        ///
        /// <para>判据刻意收窄，两条缺一不可：①**清单里有加密条目**（`Encrypted = +`）——
        /// 只有这种包"错密码的代价 = 把整包数据读完"；加密头的包（`-mhe=on`）读头就拒绝、本来就快，
        /// 不加密的包压根没有密码这回事；②声明总量 ≥ <see cref="MinWorthProbingBytes"/>。</para>
        /// </summary>
        public static bool IsWorthProbing(ArchiveListResult? list, long declaredTotalBytes)
        {
            return list is { Success: true, IsEncrypted: true } && declaredTotalBytes >= MinWorthProbingBytes;
        }

        /// <summary>
        /// 挑探针时看**归档顺序里最靠前的几个**条目（见 <see cref="ChooseProbeEntry"/>）。
        /// </summary>
        public const int ProbeOrderWindow = 3;

        /// <summary>
        /// 从清单里挑"最便宜的探针条目"的名字；挑不出来返回 <c>null</c>。
        ///
        /// <para><b>2026-10-05 真机改口径：按"归档顺序靠前"挑，不再挑全局最小的那个。</b>
        /// 现场（`ArchiveFixer-本次操作_20261005_134118.txt` 第 2 层）：为了验一个 **173 字节**的
        /// 说明文件，7-Zip 把前面 **6.84 GB** 的 solid 包整解了一遍 —— **56 秒**（13:31:19 → 13:32:15）。
        /// 道理：探针的代价 ≈ **它前面还有多少数据要被读出来**（7-Zip 只解开头那几个字节，
        /// 但必须先把前面那段解开），与"它自己多大"基本无关；而"最小的那个条目"完全可能排在几十 GB 之后。</para>
        ///
        /// <para>规则（用户 2026-10-05 原话："优先挑归档顺序里靠前的条目（第一个，或前几个里最小的）"）：
        /// 按清单顺序取**前 <see cref="ProbeOrderWindow"/> 个合格条目**，在其中挑最小的那一个。
        /// 拿不到顺序（没有清单 / 一个合格的都没有）⇒ 照旧什么都不挑（调用方退回整包试解）。</para>
        ///
        /// <para>跳过目录、0 字节与超限的条目；也跳过名字里带通配符（<c>* ? [</c>）的条目 ——
        /// 7-Zip 的条目过滤是**模式匹配**，把这种名字交回去会误伤别的条目。</para>
        /// </summary>
        public static string? ChooseProbeEntry(ArchiveListResult? list)
        {
            if (list == null || !list.Success || list.Entries == null)
            {
                return null;
            }

            /*
             * 窗口内挑最小：⛔ 不是"全局最小"。窗口满了就不再往后看 —— 位置越靠后越贵，
             * 为一个 1 KB 的条目多读几十 GB 正是这次要修的东西。
             */
            string? best = null;
            long bestSize = long.MaxValue;
            int window = 0;

            foreach (ArchiveEntry? entry in list.Entries)
            {
                if (entry == null || entry.IsDirectory)
                {
                    continue;
                }

                if (entry.Size <= 0 || entry.Size > MaxProbeEntryBytes)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.Path) || ContainsWildcard(entry.Path))
                {
                    continue;
                }

                if (entry.Size < bestSize)
                {
                    best = entry.Path;
                    bestSize = entry.Size;
                }

                /*
                 * 只数"合格的"：不合格的条目（目录 / 0 字节 / 超限 / 通配符）不占窗口 ——
                 * 它们是跳过，不是"看过了"。
                 */
                if (++window >= ProbeOrderWindow)
                {
                    break;
                }
            }

            return best;
        }

        /// <summary>
        /// 这个包是不是**整包加密**（清单里说有条目加密）。
        ///
        /// <para>用途只有一个（第 37 条）：**加密包绝不试空密码**。
        /// 7-Zip / WinRAR 都造不出"用空密码加密"的包（`-p""` 等于不加密），
        /// 拿空密码去打一个已加密的包必然白跑一整包 —— 他真机那 10 分钟就是这么来的。</para>
        /// </summary>
        public static bool ShouldSkipEmptyPassword(ArchiveListResult? list)
        {
            return list is { Success: true, IsEncrypted: true };
        }

        /// <summary>
        /// 预检要读的"解密后开头"字节数（64）。
        ///
        /// <para>够覆盖常见格式的魔数（mp4 的 <c>ftyp</c> 在第 4–8 字节、其余都在前 8 字节）；
        /// 再多读没有额外信息，只会让 7-Zip 多写一点。</para>
        /// </summary>
        public const int PrefixProbeBytes = 64;

        /// <summary>
        /// 从清单里挑"用来读开头字节"的条目：优先最小的那个（顺带也能做小样预检），
        /// **没有小条目时退而取第一个文件条目** —— 读开头 64 字节的代价与条目多大无关
        /// （用户 2026-09-25 第 38 条："没有小于 16 MB 的文件怎么办"）。
        /// </summary>
        public static string? ChoosePrefixEntry(ArchiveListResult? list)
        {
            string? small = ChooseProbeEntry(list);

            if (small != null)
            {
                return small;
            }

            if (list == null || !list.Success || list.Entries == null)
            {
                return null;
            }

            foreach (ArchiveEntry? entry in list.Entries)
            {
                if (entry == null || entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Path))
                {
                    continue;
                }

                if (ContainsWildcard(entry.Path))
                {
                    continue;
                }

                return entry.Path;
            }

            return null;
        }

        /// <summary>
        /// 解出来的开头这几十字节**像不像一个正常文件的开头**。
        ///
        /// <para>用途只有一个：给候选密码分组 —— 像文件开头的先试，什么都不像的排到最后再试。
        /// ⛔ **判据必须保守**：这里返回 <c>false</c> 只表示"看不出是什么文件"，
        /// **不等于**"密码错"（有些真实文件就是没有魔数的裸数据）。所以调用方只许拿它**调顺序**，
        /// 绝不许据此丢掉一个候选。</para>
        /// </summary>
        public static bool LooksLikeFileStart(byte[]? prefix)
        {
            if (prefix == null || prefix.Length < 4)
            {
                return false;
            }

            ReadOnlySpan<byte> span = prefix;

            foreach (byte[] magic in KnownMagics)
            {
                if (span.Length >= magic.Length && span[..magic.Length].SequenceEqual(magic))
                {
                    return true;
                }
            }

            // mp4 / mov：前 4 字节是长度，第 5–8 字节是 "ftyp" / "moov" / "mdat" / "free"。
            if (span.Length >= 8 && MatchesAscii(span[4..8], "ftyp"))
            {
                return true;
            }

            // RIFF 容器（wav / avi / webp）：RIFF????WAVE/AVI /WEBP
            if (span.Length >= 12 && MatchesAscii(span[..4], "RIFF"))
            {
                return true;
            }

            // 全 0 开头（光盘镜像的系统区就是这样）或全同一个字节：说不准，按"像文件"放过。
            bool allZero = true;
            bool allSame = true;

            for (int i = 1; i < span.Length; i++)
            {
                if (span[i] != 0)
                {
                    allZero = false;
                }

                if (span[i] != span[0])
                {
                    allSame = false;
                }
            }

            if (allZero || allSame)
            {
                return true;
            }

            return LooksLikeText(span);
        }

        /// <summary>看着像文本（说明文件 / txt / 配置）：可打印字节占绝大多数，或带 UTF-8 BOM。</summary>
        private static bool LooksLikeText(ReadOnlySpan<byte> span)
        {
            if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
            {
                return true;
            }

            int printable = 0;

            foreach (byte b in span)
            {
                // 制表 / 换行 / 回车，或普通可打印 ASCII。
                if (b == 0x09 || b == 0x0A || b == 0x0D || (b >= 0x20 && b <= 0x7E))
                {
                    printable++;
                }
            }

            return printable >= span.Length - (span.Length / 8);
        }

        private static bool MatchesAscii(ReadOnlySpan<byte> span, string text)
        {
            if (span.Length < text.Length)
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (span[i] != (byte)text[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static readonly byte[][] KnownMagics = new[]
        {
            new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C },             // 7z
            new byte[] { 0x50, 0x4B, 0x03, 0x04 },                         // zip（本地文件头）
            new byte[] { 0x50, 0x4B, 0x05, 0x06 },                         // zip（空归档）
            new byte[] { 0x50, 0x4B, 0x07, 0x08 },                         // zip（分卷）
            new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 },             // rar
            new byte[] { 0x1F, 0x8B },                                     // gzip
            new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 },             // xz
            new byte[] { 0x42, 0x5A, 0x68 },                               // bzip2
            new byte[] { 0x1A, 0x45, 0xDF, 0xA3 },                         // mkv / webm
            new byte[] { 0x46, 0x4C, 0x56 },                               // flv
            new byte[] { 0xFF, 0xD8, 0xFF },                               // jpeg
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, // png
            new byte[] { 0x47, 0x49, 0x46, 0x38 },                         // gif
            new byte[] { 0x25, 0x50, 0x44, 0x46 },                         // pdf
            new byte[] { 0x4D, 0x5A },                                     // exe / dll / 自解压包
            new byte[] { 0x49, 0x44, 0x33 },                               // mp3（带 ID3）
            new byte[] { 0x4F, 0x67, 0x67, 0x53 },                         // ogg
            new byte[] { 0x66, 0x4C, 0x61, 0x43 },                         // flac
            new byte[] { 0x75, 0x73, 0x74, 0x61, 0x72 },                   // tar
            new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C },             // （重复一次无害，保持表可读）
        };

        private static bool ContainsWildcard(string path)
        {
            return path.IndexOfAny(new[] { '*', '?', '[' }) >= 0;
        }

        /// <summary>
        /// **探针目录的落点**（两条解压路共用这一处判据，2026-10-05 统一）。
        ///
        /// <para>口径以递归那侧为准：探针是"只解一个条目"的临时产物，必须落在**产物目录的外面**
        /// （同级兄弟位置）。为什么不能放里面：产物目录里的东西要参与**结果校验与发布** ——
        /// 探针文件混进去会让"这一层到底解出了什么"数错；万一 finally 那次删除失败
        /// （占用 / 权限），残留还会被当成本层的产物搬进最终目录。</para>
        ///
        /// <para>拿不到父目录（相对路径这种畸形输入）时退回产物目录**里面**：宁可靠老位置，
        /// 也不要把临时目录建到一个说不清的地方。</para>
        /// </summary>
        /// <param name="productDirectory">这一层的**产物目录**（单层路是暂存目录，递归路是 <c>layer-NNN\output</c>）。</param>
        public static string ResolveProbeDirectory(string productDirectory)
        {
            string product = productDirectory ?? string.Empty;

            string? parent = string.IsNullOrWhiteSpace(product) ? null : Path.GetDirectoryName(product);

            return Path.Combine(
                string.IsNullOrWhiteSpace(parent) ? product : parent,
                RecursiveExtractor.ProbeDirectoryName);
        }

        /// <summary>探针条目的说明（日志用；把"只解了哪一个"说清，用户才对得上时间）。</summary>
        public static string Describe(string entryPath, long entrySize)
        {
            return $"「{entryPath}」（{ArchiveFixer.Storage.TaskSpaceEstimate.FormatSize(entrySize)}）";
        }
    }
}
