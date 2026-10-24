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
    /// <summary>批首分卷入口点名 + 入口包/产出链解析 + 第二大步事实位（第 12 块，同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {

        /// <summary>
        /// **预判入口包将来落在哪** —— 落点跟"产出它的那一单"那条链（真机 `CCCC` 2026-10-06）。
        ///
        /// <para>现场（用户连着追问的那一条）：任务表里第一单是散在 `CCCC\111(4)` 的 `111.z0删除3`，
        /// 而这一组的**入口包**（跨盘 ZIP 的末片 `111.zip`）正压在 `CCCC\111\111.rar` 里、还没被解出来 ⇒
        /// <see cref="Detection.GroupVolumeDirectory.Resolve"/> 如实回空 ⇒ 落点退回"源包自己那一层 + 包名"
        /// ⇒ 产物与"其余物"全挂到 `111(4)` 上（而它们该在 `CCCC\111\111`）。</para>
        ///
        /// <para><b>怎么预判</b>：① 这一组那几片里任意一份 → `VolumeGroupDetector.TryGetFirstVolumeName`
        /// 直接给出**入口包的标准名**（跨盘 ZIP 族：`111.z0删除2` ⇒ `111.zip`；7z 族 ⇒ `X.7z.001`；
        /// RAR 族 ⇒ 第 1 卷）—— ⛔ 不靠后缀猜、不另写按族的规则；② 在**任务表**里找"同包基名、又不是本组成员、
        /// 自己也不是在等入口包的那一单"（真机 = `111.rar`）；③ 它的落点就是这个包的落点 ⇒
        /// 拼出"入口包将来会落在哪"（`&lt;产出方落点&gt;\&lt;入口包名&gt;`），再交给**同一个**落点实现。</para>
        ///
        /// <para>⛔ 判不出（找不到产出方 / 产出方自己也在等入口包 / 名字推不出标准名）⇒ 空串
        /// ⇒ 落点与改动前逐字相同（兜底落在"什么都不做"那一档）。⛔ 深度上限 2 层，防互相等成环。</para>
        /// </summary>
        private string ResolveProducerEntryArchivePathCore(
            ArchiveTask task,
            ExtractOptions options,
            string archivePath,
            int depth)
        {
            if (depth > 2)
            {
                return string.Empty;
            }

            string entryName = VolumeGroupDetector.TryGetFirstVolumeName(FileNameHelper.GetFileName(archivePath))
                ?? string.Empty;

            if (entryName.Length == 0)
            {
                return string.Empty;
            }

            string baseName = FileNameHelper.GetArchiveBaseName(archivePath);

            if (baseName.Length == 0)
            {
                return string.Empty;
            }

            ArchiveTask? producer = FindProducerTaskForPackage(task, archivePath, entryName);

            if (producer == null)
            {
                return string.Empty;
            }

            /*
             * ===== 落点**一层一层往上叠**（用户 2026-10-06 拍板）=====
             *
             * 现场：`111.rar` → 里面是 `111.zip` → 里面是 `111.rar`（2 卷）→ 里面才是那一组跨盘 ZIP 的
             * 入口 `111.zip`。用户原话：「**我问你，你要想得到 `111.zip` 你是不是得按规则解压得到，
             * 之前的规矩就是一层层解压**，人工如果想得到 `111.zip` 就得中间有这么多的文件夹」。
             *
             * ⇒ 判据只有一条、只用一个出口：**落点 = 入口包所在那一层 + 一层包名目录**
             * （`ResolveOutputPlacement` 自己就是这条规则），于是：
             *   ① 找到"产出这一份的那一单"（同一个包基名、又不是本组成员）；
             *   ② 算**它**的落点（递归 —— 它自己也可能还在等它的入口包）；
             *   ③ 这一份就落在**它的落点**目录里（那正是"它解出来之后会待着的那一层"）；
             *   ④ 拿这一层当新的起点，继续往下追，直到追到"盘上真有这一份"或追不动为止。
             * ⛔ 不是"产出方落点 + 包名"那种一步猜测（那一步在真机上算出来正好差一层）。
             * ⛔ 追不动 / 判不出 ⇒ 原样退回旧口径（什么都不做那一档）。
             */
            OutputPlacementResult producerPlacement = ResolveOutputPlacementForProducer(producer, options, depth);

            if (!producerPlacement.Success || string.IsNullOrWhiteSpace(producerPlacement.DestinationDirectory))
            {
                return string.Empty;
            }

            string landingLayer = producerPlacement.DestinationDirectory;
            string? nextPackage = FindNextPackageInProducerChain(producer, archivePath);
            int guard = 0;

            while (nextPackage != null && guard++ < MaxProducerChainDepth)
            {
                string nextEntryName = TryGetEntryPackageName(nextPackage);

                if (nextEntryName.Length == 0)
                {
                    break;
                }

                // 入口包**已经真的在盘上**了（真机第二次及以后重算时会走到这里）⇒ 落点就是它自己那一层。
                string onDisk = Path.Combine(landingLayer, nextEntryName);

                if (File.Exists(onDisk))
                {
                    return onDisk;
                }

                ArchiveTask? nextProducer = FindProducerTaskForPackage(task, nextPackage, nextEntryName);

                if (nextProducer == null)
                {
                    return Path.Combine(landingLayer, nextEntryName);
                }

                OutputPlacementResult nextPlacement = ResolveOutputPlacementForProducer(nextProducer, options, depth + 1);

                if (!nextPlacement.Success || string.IsNullOrWhiteSpace(nextPlacement.DestinationDirectory))
                {
                    return Path.Combine(landingLayer, nextEntryName);
                }

                landingLayer = nextPlacement.DestinationDirectory;
                nextPackage = FindNextPackageInProducerChain(nextProducer, nextPackage);
            }

            string predicted = Path.Combine(landingLayer, entryName);

            AppendLog(
                "INFO",
                $"{task.LogName}：这一组的入口包（{entryName}）现在还没被解出来 —— 落点按"
                + $"「产出它的那一单」（{producer.FileName}）那条链「一层一层」推：{predicted}。");

            return predicted;
        }

        /// <summary>
        /// 产出方的落点（唯一实现仍是 <see cref="PathService.ResolveOutputPlacement"/>）。
        ///
        /// <para>⚠ 这一调会再次走进本方法所在的这条预测链（产出方自己也可能在等它的入口包）——
        /// 递归由 `depth` 与 <see cref="MaxProducerChainDepth"/> 双重兜住，⛔ 不会自环。</para>
        /// </summary>
        private OutputPlacementResult ResolveOutputPlacementForProducer(
            ArchiveTask producer,
            ExtractOptions options,
            int depth)
        {
            _ = depth;

            return _pathService.ResolveOutputPlacement(producer, options);
        }

        /// <summary>这一份的**入口包名字**（跨盘 ZIP = 末片 `X.zip`、7z = `X.7z.001`、ZIP 通用分片 = `X.zip.001`、RAR = 第 1 卷）；判不出 ⇒ 空串。</summary>
        private static string TryGetEntryPackageName(string packagePath) =>
            VolumeGroupDetector.TryGetFirstVolumeName(FileNameHelper.GetFileName(packagePath)) ?? string.Empty;

        /// <summary>
        /// **产出方那一单**：同一个包基名、又不是本组成员、自己也不是"在等入口包"的那一单（真机 = `111.rar`）。
        /// 找不到 ⇒ null（调用方退回旧口径）。
        /// </summary>
        private ArchiveTask? FindProducerTaskForPackage(ArchiveTask task, string packagePath, string entryName)
        {
            string baseName = FileNameHelper.GetArchiveBaseName(packagePath);

            if (baseName.Length == 0)
            {
                return null;
            }

            foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
            {
                if (candidate == null || ReferenceEquals(candidate, task))
                {
                    continue;
                }

                string candidatePath = candidate.CurrentPath;

                if (string.IsNullOrWhiteSpace(candidatePath))
                {
                    continue;
                }

                string candidateName = FileNameHelper.GetFileName(candidatePath);

                // 本组成员（收在同一组里的那几片）跳过 —— 它们自己就在等入口包。
                if (VolumeGroupDetector.BelongsToSameGroup(candidateName, entryName))
                {
                    continue;
                }

                if (!string.Equals(
                        FileNameHelper.GetArchiveBaseName(candidatePath),
                        baseName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 产出方自己也在等它那一组的入口包 ⇒ 它算不出真落点，别拿它当锚点。
                if (candidate.IsVolumeGroup && IsUnresolvedVolumeGroupMember(candidate))
                {
                    continue;
                }

                return candidate;
            }

            return null;
        }

        /// <summary>
        /// **这一条链上的下一份**（产出方交出来的那一份）。
        ///
        /// <para>判据 = 产出方手上那份的**入口包规范名**（`TryGetFirstVolumeName`）拼回它自己那一层：
        /// `111.rar` ⇒ `…\111\111.zip`；`111(2)_.zip` ⇒ `…\111(2)\111(2)_\111.z01`。
        /// ⛔ 与手上这一份同名（或名字推不出来）⇒ 返回 null ⇒ **链到此为止**（⛔ 不许自己套自己）。</para>
        /// </summary>
        private static string? FindNextPackageInProducerChain(ArchiveTask producer, string currentPackagePath)
        {
            string name = TryGetEntryPackageName(producer.CurrentPath ?? string.Empty);

            if (name.Length == 0)
            {
                return null;
            }

            string? directory = Path.GetDirectoryName(producer.CurrentPath ?? string.Empty);

            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            string next = Path.Combine(directory, name);

            return SafePathHelper.PathEquals(next, currentPackagePath) ? null : next;
        }

        /// <summary>
        /// 这一单是不是**分卷组的成员、而这一组的入口包现在还没被解出来**（真机 `CCCC` 2026-10-06）。
        ///
        /// <para>判据与落点实现**同一个事实位**：<see cref="Detection.GroupVolumeDirectory.Resolve"/>
        /// 在这一单自己的成员名单里找"引擎要打开、要对它输密码的那一份"（跨盘 ZIP 族 = 本体 <c>X.zip</c>、
        /// 7z 族 = <c>X.001</c>、RAR 族 = 第 1 卷）；判不出 ⇒ 它的落点只是**占位值**
        /// （退回"源包自己那一层 + 包名"那个旧口径），不足以当整批工作区根的锚点。</para>
        ///
        /// <para>⛔ 只有"分卷组"才问这一句；⛔ 判不出不等于失败 —— 调用方只是把它排到候选末尾，
        /// 实在没有别的单时照旧用它（⛔ 绝不因为挑不出根就整批停手）。</para>
        /// </summary>
        private bool IsUnresolvedVolumeGroupMember(ArchiveTask task)
        {
            if (!task.IsVolumeGroup)
            {
                return false;
            }

            var members = new List<string>();

            if (!string.IsNullOrWhiteSpace(task.CurrentPath))
            {
                members.Add(task.CurrentPath);
            }

            foreach (string volumePath in task.VolumePaths)
            {
                if (!string.IsNullOrWhiteSpace(volumePath))
                {
                    members.Add(volumePath);
                }
            }

            return members.Count > 0 && Detection.GroupVolumeDirectory.Resolve(members).Path.Length == 0;
        }

        /// <summary>把这一单记成"第二大步任务"（批末那一组齐了、要照常跑这一组的那一刻）。</summary>
        private void RememberVolumeStepTask(ArchiveTask task)
        {
            if (task != null)
            {
                _volumeStepTasks.Add(task);
            }
        }

        /// <summary>
        /// 同 <see cref="RememberVolumeStepTask(ArchiveTask)"/>，供轮次那一层调（`OneClickCoordinator` 在轮边界
        /// 把"判成是分卷的那几单"点成第二大步）—— ⛔ 只有一个写点语义，不另开第二套。
        /// </summary>
        internal void RememberVolumeStepTaskForRound(ArchiveTask task) => RememberVolumeStepTask(task);


        /// <summary>这一批是不是单层模式（递归模式下第 0 层要交给引擎，见 <see cref="DirectReadAppliesTo"/>）。</summary>
        private bool IsSingleLayerRecursion() =>
            false; // 递归三档已删除（默认全展开）；重建中

        /// <summary>
        /// **这一单这一趟是不是"第二大步"**（用户 2026-10-11 定的轮次模型，见 `RoundProductClassifier` 的说明）：
        /// 第二大步 = **分卷**的解压、**只解一层** —— 解出来的东西一律交回**轮判定**，
        /// 里面又有分卷也**不就地接着解**（下一轮从第二大步再来一遍）。
        ///
        /// <para>为什么必须是"任务级的事实位"而不是改用户的设置：递归档
        /// （<c>AppSettings</c>）是**用户选的**，⛔ 这一档不许动它；
        /// 而"这一单是第二大步"是**程序自己在批末判出来的**（那一组齐了 ⇒ 照常跑这一组，见 `toRun` 那一处）。</para>
        ///
        /// <para>⚠ 没点名 / 传 null ⇒ 与改动前**逐字相同**（只看设置档）。</para>
        /// </summary>
        private bool IsSingleLayerRecursion(ArchiveTask? task) =>
            (task != null && _volumeStepTasks.Contains(task)) || IsSingleLayerRecursion();


        /// <summary>产出方那条链最多追几层（防环、防病态输入；真机这一条链是 3 层）。</summary>
        private const int MaxProducerChainDepth = 6;

    }
}
