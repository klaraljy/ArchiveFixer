using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;


namespace ArchiveFixer.ViewModels
{
    /// <summary>停在中途那一组的自证与缺名描述（第 9 块，从 Volume 大杂烩搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// **这一层里的东西是不是都属于这一组**（"过路层"其余物放行前的**前置闸门**，唯一出口）。
        ///
        /// <para><b>为什么要有它</b>（2026-10-11）：这里原来把引用名写死成 `<组基名>.zip`，
        /// 而"同一组"那把尺子是**族 + 基名** —— 组只要是 RAR 新式（`X.partN.rar`）或数字族（`X.7z.001`），
        /// `BelongsToSameGroup(组内成员, "X.zip")` **恒为 false** ⇒ 闸门永远判"里面有不属于这一组的东西"
        /// ⇒ **那一层其余物永远删不掉**（用户选了「其余物：彻底删除」也不动），
        /// 而 ZIP 族恰好能过 —— 同一个设置两种结果。
        /// 引用名改走唯一出口 <see cref="ResolveGroupReferenceName"/>（= 这一单自己的文件名，它就在这一组里）。</para>
        ///
        /// <para>⛔ 判据只转调既有尺子；**一个文件都不属于 / 读不到 ⇒ false**（判不出就不删，红线不动）。</para>
        /// </summary>
        internal static bool AllRestFilesBelongToGroup(
            ArchiveTask? task,
            string groupBaseName,
            IEnumerable<string>? fileNames)
        {
            if (fileNames == null)
            {
                return false;
            }

            string reference = ResolveGroupReferenceName(task, groupBaseName);

            foreach (string file in fileNames)
            {
                if (string.IsNullOrWhiteSpace(file)
                    || !VolumeGroupDetector.BelongsToSameGroup(FileNameHelper.GetFileName(file), reference))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>缺卷清单那一句话（任务账上那份；空 ⇒ 如实说"名字上看不出"）。</summary>
        private static string DescribeMissingVolumeNames(ArchiveTask task)
        {
            IReadOnlyList<string> names = task == null ? Array.Empty<string>() : task.MissingVolumeNames;

            return names.Count > 0
                ? string.Join("、", names)
                : StatusText.CrossLayerGatherNoMissingNames;
        }

        /// <summary>
        /// **链停下来的那一组，就是这一单自己这一组吗** —— 中途撞缺卷那一档**进批末名单**的前提（2026-10-10）。
        ///
        /// <para>为什么必须问这一句：批末那一站（<see cref="RecheckDeferredVolumeDeficits"/>）问"这一组齐了没"
        /// 用的是**这一单自己的包基名**（<see cref="ResolveBatchGroupDirectory"/> 与
        /// <see cref="DescribeMissingVolumeNames"/> 读的都是它）⇒ 把"自己名字跟那一组无关、只是恰好解出了
        /// 那一组一片"的单塞进名单，批末就会拿它的名字去问一个不相干的组，然后如实落一句
        /// 「分卷缺失，未开始解压」。</para>
        ///
        /// <para>⛔ <b>判据必须走项目唯一那把"同一组"的尺子</b>
        /// （<see cref="VolumeGroupDetector.BelongsToSameGroup"/> = **族 + 基名**），
        /// ⛔ 不许只比基名。2026-10-10 真机 EEEE 就是只比基名翻的车：`111.rar`（**RarOld 族**）与
        /// `111.zip`（**ZipSpanned 族**）基名都是 `111` ⇒ 被登记进名单 ⇒ 批末拿它的名字去问一个
        /// **永不可能凑齐**的老式 RAR 组 ⇒ **同一单**既在列表里写着「这一片随整组解开（由「111.z03」那单解的）」，
        /// 又被扣一句 `[ERROR] 分卷缺失，未开始解压` + 「按部分完成记」。</para>
        ///
        /// <para><b>断点证据</b>（debug-mcp，停在 `ExtractionCoordinator.cs:9912`）：调用栈 = 
        /// `RememberDeferredVolumeDeficit:9912` ← `RunRecursiveAsync():6846`，`task.FileName = "111.rar"`，
        /// 名单当时 1 条（另一条是解前预检登记的 `111.z03`，那一条是对的）。</para>
        /// </summary>
        /// <param name="task">这一单（读它当前的落点文件名）。</param>
        /// <param name="result">这一次递归的结果（读它没能落地的那几片）。</param>
        internal static bool IsStalledGroupOwnGroup(ArchiveTask? task, RecursionResult? result)
        {
            if (task == null || result == null)
            {
                return false;
            }

            string ownName = FileNameHelper.GetFileName(task.CurrentPath ?? string.Empty);

            if (ownName.Length == 0)
            {
                return false;
            }

            foreach (string piece in result.UnresolvedVolumePieces)
            {
                // 唯一尺子：族 + 基名都要对上（`111.zip` 与 `111.z03` 同组；`111.rar` 与 `111.zip` 不同组）。
                if (VolumeGroupDetector.BelongsToSameGroup(FileNameHelper.GetFileName(piece), ownName))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 这一趟递归**停下来的那一片**（可能属于**别人那一组**）—— 登记进批末名单时要用它当参照物。
        ///
        /// <para>判据只有一条：`UnresolvedVolumePieces` 里第一个**自报是分卷片**的名字
        /// （唯一出口 <see cref="VolumeGroupDetector.TryGetVolumeIndex"/> 认得出卷号）。
        /// 一个都没有 ⇒ 空串 ⇒ 调用方照旧不登记（老口径一个字不改）。</para>
        ///
        /// <para>⛔ 不比中文文案、⛔ 不猜族；工作区里那一份与落点那一份名字相同，拿哪一份当参照物都一样。</para>
        /// </summary>
        internal static string FindStalledVolumePiece(RecursionResult? result)
        {
            if (result == null)
            {
                return string.Empty;
            }

            foreach (string? piece in result.UnresolvedVolumePieces)
            {
                string name = FileNameHelper.GetFileName(piece ?? string.Empty);

                if (name.Length == 0)
                {
                    continue;
                }

                /*
                 * ⚠ 2026-10-14（插桩实测两版，`10101010` 那一组，⛔ 记下来免得再踩）：
                 *
                 * <para>第一版**只认名字读得出卷号的片** ⇒ 逐字
                 * `[排障·名单] pack111-W0-0.7z ⇒ 自己那一组=False 记下的片=(空) 自报片数=1`
                 * —— 递归明明记住了那一片，这里却取不出来（名字里那一截卷标记被伪装尾巴盖住）。</para>
                 *
                 * <para>第二版改成"读卡" ⇒ **还是空**：那一刻那一份**已经不在盘上**（它的链收尾把工作区
                 * 清掉了）⇒ `FileNature.Inspect` 读不出格式与专属算法。</para>
                 *
                 * <para>⇒ 判据回到最硬的那条事实：**能进 <c>UnresolvedVolumePieces</c> 的，本身就是
                 * 递归判过"这一份是某个还没凑齐的分组的一片"的** —— 那是**我们自己的结论**、唯一出口
                 * `RecursiveExtractor.IsPieceOfIncompleteGroup`，⛔ 不需要在这里拿名字或卡再推一遍。
                 * 它只是当**参照名**用（下面那条"不许拿其余物里那一份当参照物"的闸门一个字不放宽）。</para>
                 */
                string path = piece!;

                /*
                 * ⛔ **不许拿"其余物"里的那一份当参照物**（2026-10-13 实测，真机形状守门用例
                 * `真机形状_过路层那一单的其余物按档删掉_48MB那处` 当场变红）：那一份的目录会被当成
                 * "这一组该拼到哪一层" ⇒ 逐字 `批末重判 —— 这一组该拼到哪一层 = …\111\其余物`
                 * ⇒ 整组往其余物里凑、随后被删除档清掉，树上留下一堆残渣。
                 *
                 * ⚠ 工作区里的那一份**照旧可以**（困难批 `11111111` 记下来的正是暂存目录里那一份）：
                 * 它只是当**参照名**用的，`GroupVolumeDirectory.Resolve` 自己会跳过"已不在盘上"的成员
                 * （实测：三种位置各记过一次，盘上真正决定落点的是落点树里那几份）。
                 */

                if (path.Contains(ProcessArtifactLayout.ArtifactDirectoryName, StringComparison.Ordinal))
                {
                    continue;
                }

                return path;
            }

            return string.Empty;
        }

    }
}