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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 接片判据/落点说明（从 `ExtractionCoordinator.PieceAdoptionStep.cs` 搬出）：
    /// 接片没接上那句话的唯一出口、判据输入落日志、用户自己那份文件的查找、能否搬进组、
    /// 本单暂存/链目录解析；⛔ 判据与行为沿用原实现。
    /// </summary>
    internal sealed class ExtractionPieceHandoverRules
    {
        private readonly ExtractionCoordinator _host;

        internal ExtractionPieceHandoverRules(ExtractionCoordinator host)
        {
            _host = host;
        }


        /// <summary>
        /// 「判不出 ⇒ 什么都不做」时把**判据输入**写进日志（用户 2026-10-10 目标单③）。
        ///
        /// <para>为什么必须有它：这些分支原先只有**结论**（「接不进去」），没有**判据吃进去的东西** ——
        /// 拿一份用户日志看不出"这一片被判成哪一族、第几卷"，也看不出"规范名/目标层算成了什么"。
        /// 真机 FFFF 那一轮就是卡在这里：只知道没接上，不知道是名字认不出还是入口层推不出来。</para>
        ///
        /// <para>⛔ 只写程序当场算出来的值（判不出就写「判不出」），⛔ 它一处判据都不改。</para>
        /// </summary>
        /// <summary>
        /// **接片没接上**这句话本身（唯一出口）—— 与 `IsTargetSlotAlreadyThisPiece` **同一把尺子**。
        ///
        /// <para>为什么单独收一个出口（用户 2026-10-12，大测试 `run-8888` 逐字）：
        /// 那一行写着 `目标名已经被别的文件占着（⛔ 绝不覆盖）`，而**目标名就是这一片自己的路径**
        /// —— 老写法只用 `File.Exists(target)` 判，于是把"这一片早就在位（或它已经被接过一次、
        /// 原位置那份已经不在了）"说成了"被别人占着"，下一轮排查顺着这句去找一个根本不存在的"别人"。</para>
        ///
        /// <para>三种事实分开说（⛔ 判据与行为一个字不改，只把话说对）：
        /// ① 目标位上就是**这一片自己** ⇒ 那根本不该走到"没接上"（`alreadyInPlace` 那一支已经先接走）；
        /// ② 这一片**已经不在原位置**（被前一次接片搬走/改名）⇒ 判不出目标位上那份是谁的，照实说；
        /// ③ 其余（目标位上确实站着**别人**的文件）⇒ 照旧"绝不覆盖"。</para>
        /// </summary>
        internal static string DescribeAdoptRejection(string piecePath, string target)
        {
            if (!File.Exists(target))
            {
                return "硬链接 / 同盘改名都没成功（同名已在、或跨了卷）";
            }

            if (IsTargetSlotAlreadyThisPiece(piecePath, target))
            {
                return "目标位上就是这一片自己（应当按「已经在位」接走）";
            }

            return File.Exists(piecePath)
                ? "目标名已经被别的文件占着（⛔ 绝不覆盖）"
                : "这一片已经不在原来的位置了（判不出目标位上那份与它是不是同一份，⛔ 绝不覆盖）";
        }

        internal void LogAdoptCriterionInputs(
            string piecePath,
            string reason,
            string canonical,
            string targetDir,
            string target)
        {
            string name = FileNameHelper.GetFileName(piecePath);
            string baseName = FileNameHelper.GetArchiveBaseName(piecePath);
            string? first = LegacyVolumeNames.TryGetFirstVolumeName(name);
            int? index = LegacyVolumeNames.TryGetIndex(name);

            /*
             * ⚠ **同名位上到底是什么**：必须写出来（2026-10-11 环台实测逮到的假拒绝）——
             * 同一秒的日志里出现过自相矛盾的两句：这句说「目标名已经被别的文件占着（⛔ 绝不覆盖）」，
             * 而同一单的定稿那一句说「已经在目标位上、而且是「同一份文件」（接片时建的硬链接）」。
             * 光看"被占了"分不出是**别人**的文件（该拒）还是**自己这一片**早就链接在那儿了（该当成功），
             * 下一步无从下手 ⇒ 这里直接把物理同一性写进判据输入（⛔ 只写事实，不改判据）。
             */
            string sameNameSlot = target.Length == 0
                ? "（没有目标名可看）"
                : !File.Exists(target)
                    ? "没有同名文件"
                    : FileIdentity.IsSamePhysicalFile(piecePath, target)
                        ? "同一份文件（就是这一片自己，只是名字已经在位）"
                        : "另一个文件（⛔ 绝不覆盖）";

            _host.AppendLog(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.CriterionInputsAdoptFormat,
                    name,
                    reason,
                    name,
                    baseName,
                    string.IsNullOrWhiteSpace(first) ? "判不出" : first,
                    index?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "判不出",
                    canonical.Length == 0 ? "判不出" : canonical,
                    targetDir.Length == 0 ? "判不出（推不出这一组该拼到哪一层）" : targetDir,
                    target.Length == 0 ? "判不出" : target,
                    sameNameSlot));
        }

        /// <summary>
        /// **用户自己那一份现在在哪**（改名之后也要找得到）：他最初放的那个目录里、
        /// 与起点（这一组的入口包）**同族**、且**包基名与最初导入那一份相同**的那个文件。
        ///
        /// <para>为什么要这么找：`OriginalPath` 是"最初导入路径"（改名不会跟着变，真机实测），
        /// 而账上那几卷可能散在别的目录（别的单的片）⇒ 只有"同目录 + 同族 + 同基名"这一条
        /// 能把"用户自己那一片"认出来（⛔ 只用于**列表显示**，不参与任何搬运 / 删除判据）。</para>
        /// <para>判不出 ⇒ 空串（调用方那一行显示照旧，⛔ 不猜）。</para>
        /// </summary>
        internal static string FindUserOwnFileBesideItsDirectory(ArchiveTask task, string startPointPath)
        {
            string original = task.OriginalPath ?? string.Empty;
            string directory = Path.GetDirectoryName(original) ?? string.Empty;

            if (directory.Length == 0 || string.IsNullOrWhiteSpace(startPointPath) || !Directory.Exists(directory))
            {
                return string.Empty;
            }

            string startName = FileNameHelper.GetFileName(startPointPath);
            string originalBaseName = FileNameHelper.GetArchiveBaseName(FileNameHelper.GetFileName(original));

            try
            {
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    string name = FileNameHelper.GetFileName(file);

                    if (string.Equals(name, startName, StringComparison.OrdinalIgnoreCase)
                        || !LegacyVolumeNames.BelongsToSameGroup(name, startName)
                        || !string.Equals(
                            FileNameHelper.GetArchiveBaseName(name),
                            originalBaseName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return file;
                }
            }
            catch
            {
                // 读不动 ⇒ 判不出。
            }

            return string.Empty;
        }

        /// <summary>
        /// **这一片该"搬"还是该"只建链接"** —— 判据只有两条，都是路径事实：
        /// ① 落在**会被整份删掉的过程物目录**里（`.ArchiveFixer.work` / 其余物）⇒ 倾向搬
        ///    （留一份在那种目录里，等于接了一个马上就要消失的名字）；
        /// ② ⛔ **但它正躺在"本单自己的暂存目录"里 ⇒ 只建链接、绝不搬走**：那是收尾链
        ///    （结果校验 / 定稿）要读的那一份，搬走 = 把自己这一单唯一的产物拿走。
        ///
        /// <para><b>真机 CCCC 2026-10-06 18:13</b>（用户原话：「出现了解压失败的文字，明明成功了」）：
        /// 某一单（`111(2)_.zip`）自己解出来的那一片先被"接片"那一档从**它的暂存目录**里搬走，
        /// 收尾链紧接着读暂存目录读到空 ⇒「校验未通过：输出目录是空目录，没有产物」⇒ 列表显示
        /// 「解压失败」+ 源包一个字节都不处理。触发它是一段**竞态**：同一批另一单（`111.rar`）恰好在
        /// 两次接片调用之间把入口包 `111.zip` 落到了落点层 ⇒ 第一次接片还不认识那一层（什么都不做）、
        /// 第二次已经认识 ⇒ 搬走的就是暂存目录里那一份。</para>
        ///
        /// <para>⛔ 这条判据**不看顺序、不看时序**：暂存目录里的那一份永远不搬
        /// （它随任务收尾一起清掉，落点那一份是硬链接、零字节、照样在）。</para>
        ///
        /// <para><b>为什么还要第 ③ 条</b>（同一类竞态的另一半，2026-10-11 定案到行）：收拢那一档
        /// （<c>TryGatherGroupPiecesInto</c> → <c>TryAdoptUnresolvedVolumePiece</c>）传进来的
        /// <paramref name="ownerStageDirectory"/> 是**消费方**（要解开这一组的那一单）的暂存目录，
        /// 而被收的那一片常常属于**另一条还在跑的链**（躺在它自己的
        /// <c>…\recursive\&lt;那一单&gt;_&lt;ts&gt;_&lt;hash&gt;\layer-000\output\</c> 里）。
        /// 按老判据它是"过程物目录里的东西"、又不在**调用方**的暂存目录里 ⇒ 判成"搬"
        /// ⇒ 那条链的定稿/校验读到空 ⇒ **假「解压失败」**（全量并行负载下实测：两单落
        /// <c>Failed</c>、整组凑不齐；同一份夹具单跑 6/6 全绿）。
        /// ⛔ 判不出（一条还没收场的链都拿不到）⇒ 照旧按老口径办。</para>
        /// </summary>
        internal static bool ShouldMovePieceIntoGroup(
            string piecePath,
            string ownerStageDirectory,
            IEnumerable<string>? liveChainWorkDirectories = null)
        {
            if (!ProcessArtifactLayout.IsInsideDeletableProcessFolders(piecePath))
            {
                return false;
            }

            // ② 本单自己的暂存目录：只建链接。
            if (ownerStageDirectory.Length > 0
                && ArchivePathGuard.IsInsideRoot(ownerStageDirectory, piecePath, out _))
            {
                return false;
            }

            /*
             * ③ 别的**还没收场**那条链的工作树：也只建链接。
             * 硬链接是实名的第二份名字 —— 那条链随后删掉自己的过程物目录，不会连带把这一份弄没；
             * 反过来"搬"就是把它这条链唯一的产物拿走（正是上面那条红线的形状）。
             */
            if (liveChainWorkDirectories != null)
            {
                foreach (string directory in liveChainWorkDirectories)
                {
                    if (!string.IsNullOrWhiteSpace(directory)
                        && ArchivePathGuard.IsInsideRoot(directory, piecePath, out _))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// **还没收场的那几条链的工作树**（收拢那一档用来判"这一片是不是别人的在制品"，
        /// 见 <see cref="ShouldMovePieceIntoGroup"/> 第 ③ 条）。
        ///
        /// <para>⛔ 只看**机器终态**：<see cref="TaskOutcome.Pending"/> = 还没落结论（含"正在跑"
        /// 与"停在需要用户决定的层"）—— 那些链的过程物目录里那一片**还不能搬**。
        /// 判不出（算不出工作树 / 拿不到任务表）⇒ 空表，调用方照旧按老口径办，⛔ 不放宽。</para>
        /// </summary>
        internal List<string> CollectLiveChainWorkDirectories()
        {
            var directories = new List<string>();

            foreach (ArchiveTask task in ExtractionCoordinator.SnapshotTaskTable(_host.Tasks))
            {
                if (task == null || task.Outcome != TaskOutcome.Pending)
                {
                    continue;
                }

                try
                {
                    string directory = _host._pathService.BuildTaskWorkDirectory(task);

                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        directories.Add(directory);
                    }
                }
                catch
                {
                    // 算不出来就不算它（少一条保护，⛔ 不改任何结论）。
                }
            }

            return directories;
        }

        /// <summary>
        /// 目标位上已经有同名文件时：那到底**是不是这一片自己**（同一份物理文件）。
        ///
        /// <para><b>为什么必须有它</b>（2026-10-11 环台实测逮到的假拒绝）：接片那一档原来只判
        /// `File.Exists(target)` 就返回"接不进"，而目标位上那一份**恰恰就是这一片自己**
        /// （早前已经硬链接过去、或它自己就是从那儿来的）——
        /// 于是"接片成功"被写成 WARN 假警报，**更要命的是后面那套记账整段被跳过**
        /// （`RememberGroupPieceProducer` / `RememberPieceSupplyingTask` / `RememberAdoptedLandedPiece`）
        /// ⇒ 产出方那一份"已经落地的片"没人收（用户 2026-10-10 点名要收的那一条）。
        /// 实测对照（同一份样本、同一个环台）：修前 6 条假拒绝 / 0 条"早就在位"；修后 0 条假拒绝 / 11 条"早就在位"。</para>
        ///
        /// <para>判据只读事实：**同一份物理文件**（<see cref="FileIdentity.IsSamePhysicalFile"/>）。
        /// true ⇒ 接片算成功（⛔ 一个字节都不搬、不覆盖）；false ⇒ 别人的文件 ⇒ 照旧判"接不进"。</para>
        /// </summary>
        internal static bool IsTargetSlotAlreadyThisPiece(string piecePath, string target)
            => !string.IsNullOrWhiteSpace(target)
               && File.Exists(target)
               && FileIdentity.IsSamePhysicalFile(piecePath, target);

        /// <summary>本单自己的暂存目录（算不出来 / 判不出 ⇒ 空串 = 调用方落回老口径"按过程物目录那一档"）。</summary>
        internal string ResolveOwnerStageDirectory(ArchiveTask? owner)
        {
            if (owner == null)
            {
                return string.Empty;
            }

            try
            {
                return _host._pathService.BuildTaskStageDirectory(owner);
            }
            catch
            {
                return string.Empty;
            }
        }

        internal string ResolveOwnerChainDirectory(ArchiveTask? owner)
        {
            if (owner == null || string.IsNullOrWhiteSpace(owner.CurrentPath))
            {
                return string.Empty;
            }

            /*
             * ⚠ 2026-10-09 真机 `EEEE`（18:48 那一趟）**实测的根因**：这一组的"家"被**已经搬走的源包**带偏了。
             *
             * 现场（逐条有出处）：行 161 先把产出方那一单的源包搬进其余物
             * （`111.rar → …\111\111\其余物\111.rar`）⇒ 下面那两条路**都读 `owner.CurrentPath`**
             * （`ResolveOutputPlacement` 内部读它；退路 `Path.GetDirectoryName(owner.CurrentPath)` 也读它）
             * ⇒ 算出来的"这一组的家"变成 **`…\111\111\其余物\111`** —— 与真机 6 条收片的 `targetDir`
             * **逐字相同**（行 164/171/177/178/186）。片全被收进"其余物"里 ⇒ 组永远凑不齐，
             * 批末「其余物 = 彻底删除」再把它们**连同入口包一起删掉**（行 296）⇒ 盘上 `111\111\` 空掉。
             *
             * ⇒ **先用产出方当初记下的"实际输出目录"**（`owner.OutputPath`）：那是"产物到底在哪"的
             * **唯一权威来源**（写入点 `ExtractSingleTaskAsync` 里 `task.OutputPath = outputPath;`，
             * 那段注释自己写着"解压后校验、结果归集、清理源包、界面那一列、以及一键处理的续解全都以它为准"）。
             * ⛔ 不再用"会动的 `CurrentPath`"现算一遍 —— 源包一被搬走，那个算式就指向其余物。
             */
            if (!string.IsNullOrWhiteSpace(owner.OutputPath))
            {
                return owner.OutputPath;
            }

            try
            {
                OutputPlacementResult placement = _host._pathService.ResolveOutputPlacement(
                    owner,
                    _host.BuildExtractOptions(tryExtractUnknownFormat: false));

                if (placement.Success && !string.IsNullOrWhiteSpace(placement.DestinationDirectory))
                {
                    return placement.DestinationDirectory;
                }

                /*
                 * ⛔ **这一刻 `task.OutputPath` 往往还没落**（真机 2026-10-06 当场逮到：这一档"搬了 0 份"，
                 * 就是因为这里返回空 ⇒ 目标层算不出来 ⇒ 整档静默不做）。⇒ 按**同一套落点公式现算一份**：
                 * 未指定位置那一档 = `源包所在目录 + 包名文件夹`（`111\111.rar` ⇒ `111\111\`），
                 * 与 <see cref="OutputPlacement.ResolveDestinationDirectory"/> 的公式逐字同口径
                 * （用包基名，⛔ 不是"剥后缀再加一层"的自创写法）。
                 */
                string? ownerDirectory = Path.GetDirectoryName(owner.CurrentPath);

                if (string.IsNullOrWhiteSpace(ownerDirectory))
                {
                    return string.Empty;
                }

                string packageName = FileNameHelper.GetArchiveBaseName(
                    FileNameHelper.GetFileName(owner.CurrentPath));

                return packageName.Length > 0
                    ? Path.Combine(ownerDirectory, packageName)
                    : ownerDirectory;
            }
            catch (Exception ex)
            {
                // 加法：算不出落点 ⇒ 退回既有口径（这一片落在哪儿与改动前一个字都不差）。
                _host.AppendLog("WARN", $"跨链收卷算不出这一片的落点（{ex.Message}）。");
            }

            return string.Empty;
        }
    }
}

