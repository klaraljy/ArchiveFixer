using System;
using System.Collections.Generic;
using ArchiveFixer.Engines;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **一层归档的清单结论**（条目数 + 解压后总字节）—— 检验等级 L3 的预期来源。
    ///
    /// <para><b>为什么要有这个东西</b>（用户 2026-09-30 真机报的 bug）：一条展开多层的续解链里，
    /// 只有"最终落地的那些文件"才是用户要的内容物，而它们由**最里那一层（叶子层）**产出。
    /// 老口径拿第 0 层的清单去核对这些文件必然对不上，于是干脆整段放弃核对
    /// （<c>ExtractionCoordinator</c> 里"展开 &gt; 1 层就给一份 Success=false 的空预期"），
    /// 结果 L4 只能判「判不出完整性」⇒ 源包一个字节都不敢动 —— 明明每一层都列得出清单。</para>
    ///
    /// <para><b>口径只有一个</b>：折算（仅大小写去重）走 <see cref="OutputVerifier.CollapseCaseOnlyDuplicates"/>，
    /// ⛔ 这里不许再写第二套折算规则。</para>
    /// </summary>
    public sealed class LayerManifest
    {
        /// <summary>这一层的清单**没取到**（引擎列不出来 / 加密头没密码 / 截断），
        /// 原因写在 <see cref="Reason"/> 里（必须点名是哪一层、为什么）。</summary>
        public static LayerManifest Unavailable(string reason)
        {
            return new LayerManifest { Available = false, Reason = reason ?? string.Empty };
        }

        /// <summary>引擎**真的列出了**这一层：记下折算后的条目数与解压后总字节。</summary>
        public static LayerManifest From(ArchiveListResult? list)
        {
            if (list == null || !list.Success)
            {
                return Unavailable(DescribeListFailure(list));
            }

            (int fileCount, long totalSize, _, _) = OutputVerifier.CollapseCaseOnlyDuplicates(list);

            return new LayerManifest
            {
                Available = true,
                FileCount = fileCount,
                TotalSize = totalSize
            };
        }

        /// <summary>引擎给不出清单时的一句原因（不含层号/包名 —— 那是调用方拼的，它才知道自己在哪一层）。</summary>
        public static string DescribeListFailure(ArchiveListResult? list)
        {
            if (list == null)
            {
                return "引擎没能列出这一层的目录（列目录失败或超时）";
            }

            string reason = string.IsNullOrWhiteSpace(list.Message) ? "引擎没能列出这一层的目录" : list.Message;

            return string.Equals(list.ErrorType, EngineErrorTypes.EncryptedHeaders, StringComparison.Ordinal)
                ? reason + "（这是加密头归档：配上密码就能列出清单）"
                : reason;
        }

        /// <summary>有没有可信清单。</summary>
        public bool Available { get; init; }

        /// <summary>折算后的文件条目数（仅大小写去重后）。</summary>
        public int FileCount { get; init; }

        /// <summary>折算后的解压后总字节。</summary>
        public long TotalSize { get; init; }

        /// <summary>取不到时的原因（给用户看的一句话）；取得到时为空串。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>
        /// 折成 <see cref="OutputVerifier.Verify"/> 要的那种"预期清单"。
        ///
        /// <para><c>Entries</c> 刻意留空：折算已经在 <see cref="From"/> 里做过一次，
        /// 而 <see cref="OutputVerifier.CollapseCaseOnlyDuplicates"/> 在 <c>Entries</c> 为空时
        /// 会原样退回这两个总数（见那里的第一支）—— 于是折算口径仍然只有一处，不会折两遍。</para>
        /// </summary>
        public ArchiveListResult ToExpected()
        {
            return new ArchiveListResult
            {
                Success = true,
                FileCount = FileCount,
                TotalUncompressedSize = TotalSize,
                Entries = Array.Empty<ArchiveEntry>(),
                Message = $"本层清单：{FileCount} 个文件 / {TotalSize} 字节"
            };
        }

        /// <summary>本层清单在日志/证据里的一行说法（不含层号 —— 层号由调用方拼）。</summary>
        public string Describe()
        {
            return Available
                ? $"清单 {FileCount} 个文件 / {TotalSize} 字节"
                : Reason;
        }
    }
}
