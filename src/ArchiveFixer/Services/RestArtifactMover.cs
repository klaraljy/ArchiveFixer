using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services;

/// <summary>
/// 把**一个过程物文件**搬进其余物的唯一动作出口（生命周期：归集执行；失败模型：绝不覆盖，跨卷先复制成功再删原件）。
///
/// <para>三处消费点共用它（⛔ 不许各写一份）：① <see cref="ChainRestArtifactCollector.CollectAsync"/>；
/// ② <see cref="ChainRestArtifactCollector.CollectLayerInputsIntoRest"/>；③ <c>_host.PurgeGroupLandedPieces</c>。
/// 判据与源包搬运（<c>SourcePackageMover</c>）**同一口径**：目标名被占 ⇒ 让位；同卷直接改名、
/// 跨卷/被占用才回落到"先复制成功再删原件"——复制失败就一个字节都不动。</para>
/// </summary>
internal static class RestArtifactMover
{
    /// <summary>
    /// **把一个过程物文件搬进其余物**（唯一出口：让位 + 同卷改名 / 跨卷复制 + 绝不覆盖）。
    ///
    /// <para>三处消费点共用它（⛔ 不许各写一份）：① <c>ChainRestArtifactCollector.CollectAsync</c>
    /// （链尾把"内层包"集中进其余物）；② <c>ChainRestArtifactCollector.CollectLayerInputsIntoRest</c>
    /// （链内那一族的片在链尾集中）；③ <c>_host.PurgeGroupLandedPieces</c>
    /// （被整组接走的那份副本集中进其余物）。</para>
    ///
    /// <para>判据与源包搬运（<c>SourcePackageMover</c>）**同一口径**：目标名被占 ⇒
    /// <see cref="ProcessArtifactLayout.MakeUniqueTarget"/> 让位（连"本计划里前面那几条"一起算），
    /// 同卷直接改名、跨卷/被占用才回落到"先复制成功再删原件" —— 复制失败就一个字节都不动。</para>
    /// </summary>
    /// <param name="from">源（必须在盘上）。</param>
    /// <param name="to">目标（最终名可能因让位而变，见 <paramref name="target"/>）。</param>
    /// <param name="movedTargets">本趟已经落下去的目标名（让位时要看它）。</param>
    /// <param name="target">真的搬到的那个名字（失败时为空串）。</param>
    /// <param name="failure">失败原因（成功时为 null）。</param>
    internal static bool TryMoveIntoRest(
        string from,
        string to,
        HashSet<string> movedTargets,
        out string target,
        out string? failure)
    {
        target = to;
        failure = null;

        try
        {
            if (movedTargets.Contains(target) || File.Exists(target) || Directory.Exists(target))
            {
                target = ProcessArtifactLayout.MakeUniqueTarget(
                    target,
                    isDirectory: false,
                    movedTargets,
                    FileSystemArtifactTargetProbe.Instance);
            }

            movedTargets.Add(target);

            SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(target) ?? string.Empty);

            try
            {
                File.Move(from, target, overwrite: false);
            }
            catch (IOException)
            {
                File.Copy(from, target, overwrite: false);
                File.Delete(from);
            }
            catch (NotSupportedException)
            {
                File.Copy(from, target, overwrite: false);
                File.Delete(from);
            }

            return true;
        }
        catch (Exception ex)
        {
            failure = ex.Message;

            return false;
        }
    }
}
