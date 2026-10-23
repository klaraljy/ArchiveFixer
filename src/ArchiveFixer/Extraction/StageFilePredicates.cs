using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using System.IO;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 暂存树里的文件分类（第 20 轮：从 <c>ExtractionCoordinator.RestHandlingStep.cs</c> 的静态方法搬出来；
    /// 定稿侧与其余物侧共用这一份判据）。
    /// </summary>
    internal static class StageFilePredicates
    {

        /// <summary>
        /// 这条路是不是"其余物"（契约 §3.2）：分卷段（<c>.001</c>/<c>.z01</c>/<c>.r00</c>/<c>.part1</c>）
        /// 与归档本体（<c>.7z</c>/<c>.zip</c>/<c>.rar</c>/…）。抠出来的中间 ZIP 也落在这一条里。
        /// </summary>
        internal static bool IsProcessArtifactFile(string filePath)
        {
            string extension = Path.GetExtension(filePath);

            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            return ExtensionHelper.IsVolumePartExtension(extension) ||
                   ExtensionHelper.IsKnownArchiveExtension(extension);
        }

        /// <summary>
        /// 这个文件是不是**带卷标记的那一片**（<c>.001</c> / <c>.z01</c> / <c>.r00</c> / <c>.partN</c>）。
        ///
        /// <para>与 <see cref="IsProcessArtifactFile"/> 的区别就在这一格：那一条把"归档本体"
        /// （<c>.zip</c> / <c>.7z</c> / <c>.rar</c>）也算其余物，而归档本体**自己就是完整包**，
        /// 不需要分卷那条判据；只有带卷标记的这一档才可能是"某组缺了兄弟的一片"。</para>
        /// </summary>
        internal static bool IsVolumePartFile(string filePath) =>
            ExtensionHelper.IsVolumePartExtension(Path.GetExtension(filePath));
        /// <summary>这个名字的内容证据是不是"某组分卷的一员"（唯一出口：<see cref="VolumePieceFacts"/>）。</summary>
        internal static bool CardSaysItIsOneOfTheGroup(string path)
        {
            try
            {
                return VolumePieceFacts.CardSaysItIsOneOfTheGroup(FileNature.Inspect(path));
            }
            catch
            {
                return false;
            }
        }
    }
}