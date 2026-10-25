using System;
using System.IO;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;

namespace ArchiveFixer.ViewModels
{
    /// <summary>一键处理 · 内层包候选策略（功能代码独立文件，⛔ 不再塞回主文件）。</summary>
    internal sealed partial class OneClickCoordinator
    {
        /// <summary>
        /// 空间不足模式下，其余物里的过程物已经完成使命：成功时会被彻底删除，失败时只留给用户。
        /// ⛔ 不能再把它当下一轮的新内层包，否则删掉源包后副本会被重新解开，出现同名重复目录。
        /// </summary>
        internal static bool ShouldSkipAsProcessArtifact(
            string file,
            bool spaceTight,
            out string reason)
        {
            if (spaceTight && ProcessArtifactLayout.IsInsideDeletableProcessFolders(file))
            {
                reason = "空间不足模式：其余物里的过程物不再当新内层包";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        /// <summary>同一内层包在其余物里还有副本时，用它按「同名 + 同字节数」识别重复。</summary>
        internal static string InnerPackageKey(string path)
        {
            try
            {
                return Path.GetFileName(path) + "|" + new FileInfo(path).Length;
            }
            catch
            {
                return Path.GetFileName(path) + "|0";
            }
        }
    }
}
