using System;
using System.Collections.Generic;
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
        /// 从清单里挑"最便宜的探针条目"的名字；挑不出来返回 <c>null</c>。
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

            string? best = null;
            long bestSize = long.MaxValue;

            foreach (ArchiveEntry? entry in list.Entries)
            {
                if (entry == null || entry.IsDirectory)
                {
                    continue;
                }

                if (entry.Size <= 0 || entry.Size > MaxProbeEntryBytes || entry.Size >= bestSize)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.Path) || ContainsWildcard(entry.Path))
                {
                    continue;
                }

                best = entry.Path;
                bestSize = entry.Size;
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

        private static bool ContainsWildcard(string path)
        {
            return path.IndexOfAny(new[] { '*', '?', '[' }) >= 0;
        }

        /// <summary>探针条目的说明（日志用；把"只解了哪一个"说清，用户才对得上时间）。</summary>
        public static string Describe(string entryPath, long entrySize)
        {
            return $"「{entryPath}」（{ArchiveFixer.Storage.TaskSpaceEstimate.FormatSize(entrySize)}）";
        }
    }
}
