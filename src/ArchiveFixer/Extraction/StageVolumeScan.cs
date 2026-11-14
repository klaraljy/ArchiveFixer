using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 暂存树的一次分卷扫描：**已经认出来的组** + **逐目录的事实清单**（后者给"孤儿卷"复查用）。
    ///
    /// <para>为什么要带着事实清单：带卷标记的文件**不一定**落进某个组 ——
    /// <see cref="VolumeGroupDetector"/> 对"名字被伪装过"的桶还要过一道尺寸规律，
    /// 过不了就把整桶丢掉（2026-09-28 三层方案，防认错组）。丢掉之后那些文件在
    /// <see cref="PlanFinalLayout"/> 里就只剩"按扩展名像不像分卷"这一条老判据了 ——
    /// 而它们恰恰可能是**缺了兄弟的那一组**。所以那一档必须回头问判定器（见调用点）。</para>
    /// </summary>
    internal sealed class StageVolumeScan
    {
        /// <summary>认出来的组（每组一条结论）。</summary>
        public IReadOnlyList<StageVolumeGroupView> Groups { get; init; } = Array.Empty<StageVolumeGroupView>();

        /// <summary>逐目录的事实清单（键 = 目录绝对路径）。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<VolumeGroupEntry>> EntriesByDirectory { get; init; } =
            new Dictionary<string, IReadOnlyList<VolumeGroupEntry>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>这个文件的同目录事实清单（拿不到就返回空 —— 空清单在判定器里等于"判不出"）。</summary>
        public IReadOnlyList<VolumeGroupEntry> EntriesFor(string filePath)
        {
            try
            {
                string? directory = Path.GetDirectoryName(filePath);

                return !string.IsNullOrWhiteSpace(directory) &&
                       EntriesByDirectory.TryGetValue(directory, out IReadOnlyList<VolumeGroupEntry>? entries)
                    ? entries
                    : Array.Empty<VolumeGroupEntry>();
            }
            catch
            {
                return Array.Empty<VolumeGroupEntry>();
            }
        }
    }
}
