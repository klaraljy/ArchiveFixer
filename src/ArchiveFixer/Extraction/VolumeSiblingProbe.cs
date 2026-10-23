using System;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using System.IO;

namespace ArchiveFixer.Extraction
{
    /// <summary>同目录里有没有同组的兄弟卷（纯路径判据；从协调器分片提到 Extraction）。</summary>
    internal static class VolumeSiblingProbe
    {

        /// <summary>
        /// 同目录里还有没有"同一分卷组的别的卷"（只按名字判，**只用于兜底拒搬**）。
        ///
        /// <para>用于"分组信息没拿到时宁可不动"那一条：搬走一卷、把同组别的卷留在原地，
        /// 等于把一套完整的包拆成废件（2026-09-28 真机：内层包 4 卷只搬走了 `.001`）。</para>
        /// </summary>
        internal static bool HasSiblingVolumeBeside(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return false;
                }

                string name = FileNameHelper.GetFileName(path);

                if (!FileNameHelper.IsVolumePartFileName(name))
                {
                    return false;
                }

                string directory = Path.GetDirectoryName(path) ?? string.Empty;

                foreach (string sibling in Directory.EnumerateFiles(directory))
                {
                    string siblingName = Path.GetFileName(sibling);

                    if (string.Equals(siblingName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (FileNameHelper.IsVolumePartFileName(siblingName) &&
                        VolumeGroupDetector.BelongsToSameGroup(name, siblingName))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // 读目录失败就当"没有兄弟卷"：调用点那边保守方向是"不搬"，不会因为这里失败而多搬东西。
            }

            return false;
        }
    }
}
