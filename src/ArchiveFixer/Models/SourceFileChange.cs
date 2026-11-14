using ArchiveFixer.Models;
using System;

namespace ArchiveFixer.Models
{
    /// <summary>一个文件上比对出来的差异（一项差异 = 一个文件上的一类变化，不叠加）。</summary>
    public sealed class SourceFileChange
    {
        /// <summary>差异那一项的类型。</summary>
        public SourceChangeKind Kind { get; init; }

        /// <summary>出问题的那个文件（完整路径）。分卷组里就是**具体哪一卷**。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>显示用的文件名（报错文案里点名用）。</summary>
        public string FileName { get; init; } = string.Empty;

        /// <summary>
        /// 这一项是不是**分卷**（而不是主文件）。
        ///
        /// <para>文案上必须分得清：分卷组里"第二卷不见了"时，只说"文件不见了"用户会以为整个包没了；
        /// 程序得点名"是哪一个卷"（AGENTS.md §9.3 对缺卷的同一口径）。</para>
        /// </summary>
        public bool IsVolume { get; init; }

        /// <summary>当时的大小（<see cref="SourceChangeKind.Missing"/> 时为 0）。</summary>
        public long PreviousLength { get; init; }

        /// <summary>现在的大小（<see cref="SourceChangeKind.Missing"/> 时为 0）。</summary>
        public long CurrentLength { get; init; }

        /// <summary>当时的最后写入时间。</summary>
        public DateTime PreviousLastWriteTimeUtc { get; init; }

        /// <summary>现在的最后写入时间。</summary>
        public DateTime CurrentLastWriteTimeUtc { get; init; }

        /// <summary>
        /// 这一项差异怎么念给用户听（`大小 1.2 GiB → 1.3 GiB` / `修改时间 … → …` / `文件不见了`）。
        /// 文案模板的唯一来源是 <see cref="StatusText"/>（AGENTS.md §7：界面文案不许手写散落）。
        /// </summary>
        public string Describe()
        {
            return Kind switch
            {
                SourceChangeKind.Size =>
                    string.Format(
                        StatusText.SourceChangeSizeFormat,
                        SizeFormat.Format(PreviousLength),
                        SizeFormat.Format(CurrentLength)),

                SourceChangeKind.LastWriteTime =>
                    string.Format(
                        StatusText.SourceChangeTimeFormat,
                        FormatTime(PreviousLastWriteTimeUtc),
                        FormatTime(CurrentLastWriteTimeUtc)),

                _ => IsVolume ? StatusText.SourceChangeVolumeMissingText : StatusText.SourceChangeMissingText
            };
        }

        /// <summary>
        /// 时间按**本地时间**显示。
        ///
        /// 存的是 UTC（跨时区比较不会因为夏令时跳变而误判），但给用户看的一律是本地时间 ——
        /// 用户在资源管理器里看到的"修改时间"就是本地时间，直接给他 UTC 等于让他自己换算。
        /// </summary>
        private static string FormatTime(DateTime utc)
        {
            if (utc == DateTime.MinValue)
            {
                return StatusText.SourceChangeUnknownTimeText;
            }

            return utc.ToLocalTime().ToString(StatusText.SourceChangeTimeDisplayFormat);
        }
    }
}
