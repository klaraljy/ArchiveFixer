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
    /// <summary>损坏压缩包的部分完成发布（损坏判据/停止层/部分完成候选；第 12 块，同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// **「这一单算不算『损坏压缩包』」的唯一出口**（用户 2026-10-14 口径：部分完成发布从"用户开关"
        /// 降到底层 —— 「就是『损坏压缩包的解压情况』，这种情况下会自动开启，**而且用户不能操作**」）。
        ///
        /// <para>判据只看**结构化事实**：引擎明说 <see cref="EngineErrorTypes.CorruptedArchive"/>
        /// （"归档损坏 / 校验和不符 / 数据不全"）。⛔ 不比中文文案、⛔ 不看体积/名字一类推断。</para>
        ///
        /// <para><b>为什么不含"两义"那一档</b>（<see cref="EngineErrorTypes.PasswordOrCorrupted"/>）：
        /// 引擎在那里同一句话里既说密码不对又说数据坏了，**它自己都判不出**；而这一类最常见的成因是
        /// 密码不对（旧式 RAR 加密包）⇒ 按项目铁律「判不出 ⇒ 什么都不做」，这一档**不发布**
        /// （要发布得先让用户看到那两种可能、由他决定，⛔ 不是我们替他挑一个）。</para>
        /// </summary>
        private static bool IsDamagedArchive(string? detectedErrorType) =>
            string.Equals(detectedErrorType, EngineErrorTypes.CorruptedArchive, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 记下"这一单失败时盘上还剩什么"（部分完成发布的输入）。**失败路径调用**，成功路径一次都不调。
        ///
        /// <para>⛔ 这里刻意**不看③页那个开关**：开关在收尾那一刻才读（见 <see cref="PartialPublishActive"/>），
        /// 否则"开着空间不足模式时本开关不生效"这句说明就没机会写出来了
        /// （一个候选都没记下来 ⇒ 收尾时连"为什么没发布"都答不上）。记的都是引用，没有额外的磁盘开销。</para>
        /// </summary>
        /// <param name="stagingRoot">已解出来的东西在哪（暂存目录 / 这一层的产物目录）。</param>
        /// <param name="manifestEntries">逐条清单（递归那条路直接带过来；没有就传 null，收尾时懒取一次）。</param>
        private void RecordPartialPublishCandidate(
            ArchiveTask task,
            string stagingRoot,
            string archivePath,
            string password,
            IReadOnlyList<(string Path, long Size)>? manifestEntries,
            IReadOnlyList<string>? engineFailedEntries,
            int engineReportedErrorCount,
            bool engineReportedFailure,
            string? detectedErrorType = null,
            bool chainProducedContent = false)
        {
            if (task == null || string.IsNullOrWhiteSpace(stagingRoot))
            {
                return;
            }

            _partialPublishCandidates[task] = new PartialPublishCandidate
            {
                StagingRoot = stagingRoot,
                ArchivePath = archivePath ?? string.Empty,
                Password = password ?? string.Empty,
                ManifestEntries = manifestEntries ?? Array.Empty<(string, long)>(),
                EngineFailedEntries = engineFailedEntries ?? Array.Empty<string>(),
                EngineReportedErrorCount = engineReportedErrorCount,
                EngineReportedFailure = engineReportedFailure,
                Damaged = IsDamagedArchive(detectedErrorType),
                DetectedErrorType = detectedErrorType ?? string.Empty,
                ChainProducedContent = chainProducedContent
            };
        }

        /// <summary>
        /// **「链停在哪一层」的唯一出口**：报告里**最后一个没跑成的层**（都在跑成 / 没跑过任何一层 ⇒ null）。
        ///
        /// <para>为什么要抽出来：这一件事现在有**两个**消费方 —— ① 部分完成发布的登记（那一层的产物就是
        /// "解了一半的现场"）；② 「最后一层过程物」按「删除操作」那一档留 / 删（那一层**要解的那个包**
        /// 就是口径里说的「最后一层过程物」）。⛔ 两处各判一遍迟早会分叉。</para>
        /// </summary>
        private static RecursionLayerReport? ResolveStoppedLayer(RecursionResult? result)
        {
            if (result == null)
            {
                return null;
            }

            RecursionLayerReport? stopped = null;

            foreach (RecursionLayerReport layer in result.Layers)
            {
                if (!layer.Success)
                {
                    stopped = layer;
                }
            }

            return stopped;
        }

        /// <summary>
        /// 递归那条路的登记（<see cref="RunRecursiveAsync"/> 收尾时调用）。
        ///
        /// <para>只认"**最深的那一层失败了**"这一档：那一层的产物才是"解了一半的现场"。
        /// 层成功、只是整条链按上限 / 多分支停住的那些情形**不登记** —— 它们的产物该走既有的
        /// 定稿与链尾口径（那是另一条路，⛔ 不许被这一档抢过来当成"部分完成"发布）。</para>
        /// </summary>
        private void RecordRecursivePartialPublishCandidate(ArchiveTask task, RecursionResult? result)
        {
            if (task == null || result == null || string.IsNullOrWhiteSpace(task.CurrentPath))
            {
                return;
            }

            RecursionLayerReport? failedLayer = ResolveStoppedLayer(result);

            if (failedLayer == null || string.IsNullOrWhiteSpace(failedLayer.OutputPath))
            {
                return;
            }

            /*
             * 密码传空串：这一层的候选是递归核心自己挑的，它只把**脱敏**的占位符交出来
             * （报告里绝不出现明文密码）。所以这一档要么拿现成的逐条清单（正常情形：
             * 解压前列目录那一次就是拿对的密码列的），要么什么都不发布 —— ⛔ 不去猜密码。
             */
            /*
             * 链上**任何一层**产出过文件 ⇒ "整体已有内容物解出"（用户 2026-10-16 口径）。
             *
             * ⚠ 还必须加上**停下那一层自己的产物目录**：失败层的报告里 `OutputFileCount` 按定义是 0
             * （`BuildLayerReport` 写的就是 0 —— 那一层没跑成），可它的产物目录里**可能真有东西**。
             * 实测（假引擎 + 5 个产物文件 + 密码错误）：第一版只读报告 ⇒ 判成"没有内容物"⇒ 终态落 `Failed`，
             * 与用户口径相反。判据仍走**唯一出口** `ProducedContentGate`，⛔ 不另定阈值。
             */
            bool chainProducedContent =
                result.Layers.Any(layer => layer.OutputFileCount > 0)
                || (ProducedContentGate.TryMeasure(
                        failedLayer.OutputPath,
                        failedLayer.ReportedSubItemErrors,
                        failedLayer.FailedEntryNames.Count,
                        out int failedLayerFiles,
                        out _)
                    && failedLayerFiles >= ProducedContentGate.MinimumFiles);

            RecordPartialPublishCandidate(
                task,
                failedLayer.OutputPath,
                failedLayer.ArchivePath,
                string.Empty,
                failedLayer.ManifestEntries,
                failedLayer.FailedEntryNames,
                failedLayer.ReportedSubItemErrors,
                engineReportedFailure: true,
                detectedErrorType: failedLayer.DetectedErrorType,
                chainProducedContent: chainProducedContent);
        }

        /// <summary>
        /// **「最后一层过程物」按③页「删除操作」那一档处置**（用户 2026-10-14 口径，链条模型 `A→B→C→D→a`）：
        /// 「**删除操作（失败和部分完成）**只**管"最后一层过程物"**……默认留下」
        /// ＋「`D` 肯定会留下，而且是完整的……前面的 `B` 和 `C` 都是成功的」（见
        /// <c>docs\大改动-2026-10-14-口径与冲突.md</c> §2.1 / §2.2）。
        ///
        /// <para><b>哪一份是"最后一层过程物"</b>：**链停下来的那一层要解的那个包** =
        /// <see cref="ResolveStoppedLayer"/> 那一层的 <c>ArchivePath</c>（唯一出口，两个消费方共用）。</para>
        ///
        /// <para><b>为什么必须有这一档</b>（2026-10-16 运行期实测，不是推断）：递归路每一层的产物都留在
        /// 这一单的**工作区**里，而"失败 / 部分完成 ⇒ 工作区整份删掉" ⇒ 停下来的那一层要解的包**连同一份
        /// 完整的过程物一起被清掉**了 —— 与用户口径「默认留下」正好相反（实测：一条四层链在第三层失败，
        /// 跑完盘上只剩源包 `outer.7z`，`level3.7z` 一个字节都没留）。</para>
        ///
        /// <para><b>落法</b>（⛔ 只做"留下"这一半，"彻底删除"那一半的主动删还没做，见 §20.4）：
        /// 「删除操作 = 留下」（默认档）= 在这一单的其余物里**给这一份另起一个名字（硬链接）** ——
        /// ⛔ **不是搬走**：原件留在原处（别处还在用它），而工作区收尾把那一棵树整份删掉之后，
        /// 这一份仍然活在其余物那个名字上（硬链接就是"同一份字节的第二个实名"）。
        /// 「彻底删除」档 = **什么都不做**（它随工作区一起清掉，今天就是这个行为），只如实写一行说清。</para>
        ///
        /// <para>⚠ <b>为什么必须是"链接"而不是"搬"</b>（2026-10-16 实测踩到，写死）：第一版用的是
        /// <c>TryMoveIntoRest</c>（搬走），当场把两条既有守门打红 ——
        /// ① `GroupLandingFollowsEntryPackageTests` 两条：「批末收拢 / 接片 / 落地」那套机制要在**原地**
        /// 找到那一片，搬走它 = 那一组永远收不拢（实测日志：`pack3.rar：链在「111_.part3.rar」这一层停下……
        /// 留在其余物里`，随后三条片各自落在自己那一层、没并进入口包那一层）；
        /// ② `WorkspaceRootTests.打开保留开关_失败但有文件_一律保留且说清在哪`：搬走工作区里**唯一那个文件**
        /// 会让工作区变成"零文件空壳"⇒ 空壳无论如何都删 ⇒ 用户要的"留现场"没了。
        /// ⇒ 判据与动作都改层：**原件一律原地不动**，只多一个名字。</para>
        ///
        /// <para>⛔ <b>分卷片不进这一档</b>：那一片归"批末收拢 / 接片 / 落地"那套机制管（它们本来就会在落地层
        /// 留下自己的名字、活得比工作区长），这里再给它起名字只会多一个看不懂的名字。判据 = 唯一出口
        /// <c>VolumePieceFacts</c>（卡说它是某组的一片 **或** 名字读得出卷号 —— 两者取并，宁可少动）。</para>
        ///
        /// <para>⛔ <b>红线</b>：只碰**递归工作区里**的东西（用户拖进来的源包永远不在那儿 —— 与逐层回收
        /// 那条用的是同一道保险）；建不了链接 / 推不出其余物目录 ⇒ 一行 WARN + 什么都不做。
        /// ⛔ 取消这一档不在这里判：取消的判定在 <c>TryPublishPartialProductsAsync</c> 与调用方，
        /// 而"留下"本身是**非破坏性**动作（比删安全），失败/部分完成/取消都该留下（不变量 1）。</para>
        /// </summary>
        private void PreserveStoppedLayerProcessArtifact(ArchiveTask task, RecursionResult? result)
        {
            if (task == null)
            {
                return;
            }

            RecursionLayerReport? stopped = ResolveStoppedLayer(result);

            if (stopped == null || string.IsNullOrWhiteSpace(stopped.ArchivePath))
            {
                return;
            }

            string artifact = stopped.ArchivePath;

            if (!File.Exists(artifact))
            {
                // 已经不在了（没解到那一层 / 前面某一步收走了）⇒ 没有什么可留。
                return;
            }

            if (!IsInsideRecursionWorkspace(artifact))
            {
                AppendLog(
                    "WARN",
                    $"{task.LogName}：停下的那一层要解的「{Path.GetFileName(artifact)}」不在递归工作区里"
                    + "（它可能是用户拖进来的源包）—— 判不出它是不是本链的过程物，所以一个字节都不动。");

                return;
            }

            string fileName = Path.GetFileName(artifact);

            if (string.Equals(_restHandlingThisBatch, RestHandlingModes.Delete, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog(
                    "INFO",
                    $"{task.LogName}：链在「{fileName}」这一层停下，它是这一层的「最后一层过程物」；"
                    + "按③页「删除操作 = 彻底删除」处理 —— 它随这一单的工作区一起清掉（⛔ 不搬进其余物、"
                    + "不另起名字；链上方那些成功过的过程物本来就已经各层当场删掉了）。");

                return;
            }

            FileNatureCard card = FileNature.Inspect(artifact);

            if (VolumePieceFacts.CardSaysItIsPiece(card)
                || VolumePieceFacts.ResolveSlotIndex(artifact, card) is not null
                || VolumeGroupDetector.TryGetVolumeIndex(fileName) is not null)
            {
                AppendLog(
                    "INFO",
                    $"{task.LogName}：链在「{fileName}」这一层停下，但它是「分卷的一片」 —— 这一片归"
                    + "「批末收拢 / 接片 / 落地」那套机制管（它会带着自己的名字落在该落的那一层），"
                    + "所以这一档不动它（⛔ 不给它另起名字）。");

                return;
            }

            string? restDirectory = ResolveRecursionRestDirectory(task, artifact);

            if (string.IsNullOrWhiteSpace(restDirectory))
            {
                AppendLog(
                    "WARN",
                    $"{task.LogName}：链停下的那一层的过程物「{fileName}」本该留下，但推不出这一单的其余物目录"
                    + " —— 它留在原处（工作区收尾会把它清掉）。");

                return;
            }

            try
            {
                SafePathHelper.EnsureDirectoryExists(restDirectory);
            }
            catch (Exception ex)
            {
                AppendLog(
                    "WARN",
                    $"{task.LogName}：链停下的那一层的过程物「{fileName}」本该留下，但其余物目录建不出来 —— {ex.Message}"
                    + "（它留在原处；工作区收尾会把它清掉）。");

                return;
            }

            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string target = Path.Combine(restDirectory, fileName);

            /*
             * ⚠ 撞名才让位（与 <see cref="TryMoveIntoRest"/> 同一个模式，⛔ 不自己另写一套）：
             * `ProcessArtifactLayout.MakeUniqueTarget` **只在"已经有人占了"时才该调** —— 它本身
             * 只会给出 `名字(1).ext` 这种形式（没有"空着就原样返回"那一支）。第一版无条件调它 ⇒
             * 明明空着也写成 `level3(1).7z`（运行期实测逮到的）。
             */
            if (reserved.Contains(target) || File.Exists(target) || Directory.Exists(target))
            {
                target = ProcessArtifactLayout.MakeUniqueTarget(
                    target,
                    isDirectory: false,
                    reserved,
                    FileSystemArtifactTargetProbe.Instance);
            }

            if (!HardLinkHelper.TryCreateHardLink(target, artifact))
            {
                AppendLog(
                    "WARN",
                    $"{task.LogName}：链停下的那一层的过程物「{fileName}」没能留下第二个名字"
                    + "（硬链接建不出来 —— 多半与其余物不在同一个卷）—— 它留在原处（工作区收尾会把它清掉）。");

                return;
            }

            // 其余物记到这一单上（其余物那一档收尾时才有对象可处理）。
            task.RestDirectoryPath = restDirectory;

            AppendLog(
                "INFO",
                $"{task.LogName}：链在「{fileName}」这一层停下 —— 它是这一层的「最后一层过程物」，"
                + $"按③页「删除操作 = 留下」（默认档）在其余物里留了一个名字：{target}"
                + "（硬链接：原件一个字节都没搬、也没改名；链上方成功过的那些过程物已经各层当场删掉了；"
                + "最外层源包一律原地不动）。");
        }

        /// <summary>
        /// **这一单的其余物目录**（递归路：从"这一份在递归工作区里的路径"反推落点那一层）。
        ///
        /// <para>判据只读事实：路径里那一段工作区标记（<see cref="VolumeContentInference.WorkDirectoryName"/>，
        /// ⛔ 这里不另写标记名）**前面的那一层**就是落点，其余物就在它下面。</para>
        ///
        /// <para>推不出（标记那一段找不到 / 前面没有东西 / 路径形态怪）⇒ 返回 null ⇒ 调用方什么都不做 + 一行 WARN。
        /// ⚠ 2026-10-16：这个方法在「逐层回收与档位解绑」那一刀里被删过一次（当时它唯一的调用者
        /// "集中进其余物"那支被删了）；现在「最后一层过程物按档留下」重新需要它 —— 同一份判据，
        /// ⛔ 不另写一份。</para>
        /// </summary>
        private static string? ResolveRecursionRestDirectory(ArchiveTask task, string pathInsideWorkspace)
        {
            if (task != null && !string.IsNullOrWhiteSpace(task.RestDirectoryPath))
            {
                return task.RestDirectoryPath;
            }

            if (string.IsNullOrWhiteSpace(pathInsideWorkspace))
            {
                return null;
            }

            try
            {
                var directory = new DirectoryInfo(Path.GetDirectoryName(pathInsideWorkspace) ?? string.Empty);

                while (directory != null)
                {
                    if (string.Equals(directory.Name, VolumeContentInference.WorkDirectoryName, StringComparison.OrdinalIgnoreCase))
                    {
                        string? landing = directory.Parent?.FullName;

                        return string.IsNullOrWhiteSpace(landing)
                            ? null
                            : Path.Combine(landing, ProcessArtifactLayout.ArtifactDirectoryName);
                    }

                    directory = directory.Parent;
                }
            }
            catch
            {
                // 路径形态怪 / 权限 ⇒ 判不出（⛔ 不猜）。
            }

            return null;
        }

        /// <summary>
        /// **部分完成发布的唯一入口**（收尾处调用）：逐条对账 → 二次空间体检 → 真搬 → 其余物/工作区收尾。
        /// </summary>
        /// <returns>
        /// true = 这一单的工作区**不能清**（有内容没能发布，它们只在那里）；false = 按既有口径清。
        /// </returns>
        /// <param name="cancelled">
        /// 这一单是不是被用户取消的（判据 = **任务自己的取消令牌**，⛔ 不是比对中文文案）。
        ///
        /// <para>为什么必须单独传这一位：单层那条取消路径（<c>ProcessExtractTaskAsync</c> 的
        /// <c>catch (OperationCanceledException)</c>）只落了中文状态、没落机器终态，而收尾那一步会把
        /// `Pending` 兜底改成 `Failed` ⇒ "取消"在终态上与"失败"分不开。拿终态当唯一判据的话，
        /// 用户按下取消之后，半成品照样会被摆进目标目录 —— 那正是他要停下来的东西。</para>
        /// </param>
        private async Task<bool> TryPublishPartialProductsAsync(ArchiveTask task, bool cancelled)
        {
            if (task == null || !_partialPublishCandidates.TryRemove(task, out PartialPublishCandidate? candidate))
            {
                return false;
            }

            if (cancelled)
            {
                return false;
            }

            if (cancelled)
            {
                return false;
            }

            /*
             * ⛔ 只认机器终态（判据是枚举，⛔ 不比中文）：失败 / 部分完成才谈得上"发布已解出的那部分"。
             * 成功走定稿那条路（早就有内容物落地了，不需要这一档）；取消 / 跳过一个字节都不许发布
             * —— 用户按了取消，他要的是"停下来"，不是"把半成品摆进目标目录"。
             */
            if (task.Outcome is not (TaskOutcome.Failed or TaskOutcome.PartiallyCompleted))
            {
                return false;
            }

            /*
             * ⛔ **触发判据 = 结构化事实，不是③页那个开关**（用户 2026-10-14 口径：这个功能淘汰，
             * 做成底层设计 —— 「就是『损坏压缩包的解压情况』，这种情况下会自动开启，
             * **而且用户不能操作**」）。
             *
             * 老实现读 `Settings.PartialPublishEnabled`：那个开关的 UI 在九页签那一刀已经删掉、
             * 三处值都是 false ⇒ **用户再也开不了"把已经解出来的留下"**（这一档等于死掉了）。
             * 现在：只有"引擎明说包损坏"（<see cref="IsDamagedArchive"/>，唯一出口）才自动发布；
             * 别的失败（密码错 / 缺卷 / 取消 / 越界…）照旧一个字节都不发布，并如实写一行为什么。
             */
            if (!candidate.Damaged)
            {
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PartialPublishNotDamagedFormat,
                        task.FileName,
                        string.IsNullOrWhiteSpace(candidate.DetectedErrorType)
                            ? "引擎没给出结构化错误类型"
                            : candidate.DetectedErrorType));

                return false;
            }

            if (_spaceTightThisBatch)
            {
                // 硬互斥（见 PartialPublishActive）。写一行 INFO 说清为什么没生效，⛔ 设置一个字都不改。
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PartialPublishSpaceTightBlockedFormat,
                        task.FileName));

                return false;
            }

            string packageOutputRoot = task.OutputPath;

            if (string.IsNullOrWhiteSpace(packageOutputRoot))
            {
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PartialPublishSkippedFormat,
                        task.FileName,
                        "这一单没有可用的成品目录（拿不到落点），按「判不出 ⇒ 什么都不做」处理"));

                return false;
            }

            IReadOnlyList<(string Path, long Size)> manifestEntries = candidate.ManifestEntries;

            if (manifestEntries.Count == 0 && !string.IsNullOrWhiteSpace(candidate.ArchivePath))
            {
                /*
                 * 手上没有清单，现问一次引擎（**只在这一档**、只问一次）。
                 *
                 * 为什么必须补这一次：真机 `giu910` 那一单是 7z `-mhe`（文件名也加密）——
                 * 前面几个候选连目录都列不出来，而**把包解到 98% 的那个候选**从没列过目录
                 * （解压成功才会走校验、校验里才列目录，而它恰恰没成功）⇒ 不补这一问，
                 * 这一档在最需要它的那个场景上永远拿不到清单、永远不发布。
                 *
                 * 密码用的是**这一趟最后那个候选**的：密码不对时它照样列不出来（返回失败），
                 * 于是下游按"没有清单"办 —— ⛔ 绝不因为"列不出来"就放松任何一道闸门。
                 */
                manifestEntries = await TryListForPartialPublishAsync(task, candidate);
            }

            PartialPublishOutcome outcome = PartialPublishRunner.Run(
                candidate.StagingRoot,
                packageOutputRoot,
                manifestEntries,
                candidate.EngineFailedEntries,
                candidate.EngineReportedErrorCount,
                candidate.EngineReportedFailure);

            if (!outcome.Published)
            {
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        outcome.BlockedBySpace
                            ? StatusText.PartialPublishSpaceBlockedFormat
                            : StatusText.PartialPublishSkippedFormat,
                        task.FileName,
                        outcome.Reason));

                return false;
            }

            AppendLog(
                "INFO",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.PartialPublishedFormat,
                    task.FileName,
                    outcome.PublishedCount,
                    TaskSpaceEstimate.FormatSize(outcome.PublishedBytes),
                    outcome.DestinationDirectory,
                    outcome.Verdict.Entries.Count,
                    TaskSpaceEstimate.FormatSize(outcome.Verdict.ExpectedBytes),
                    outcome.Verdict.Rejected.Count));

            /*
             * ⛔ **必须明说"压缩包损坏"**（用户 2026-10-14 口径：「这个损坏的压缩包我们肯定是要在日志里面
             * 去报一个压缩包损坏的情况，然后不完整的内容物就留下来」）。
             *
             * 为什么写成 WARN 而不是 INFO：成功任务只留一行摘要（第 44 条），而这一单是"失败 + 救回一部分"
             * ⇒ 只写 INFO 的话用户按"失败"去查，看不到"哪些是坏的、哪些已经救出来了"这件事。
             */
            AppendLog(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.PartialPublishDamagedArchiveFormat,
                    task.FileName,
                    outcome.Verdict.Rejected.Count));

            if (outcome.Publish.RenamedCount > 0)
            {
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PartialPublishedRenamedFormat,
                        task.FileName,
                        outcome.Publish.RenamedCount));
            }

            AppendLog(
                "INFO",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.PartialPublishSourceKeptFormat,
                    task.FileName));

            /*
             * 把"这一单救回来多少"记在任务上（批末诊断要用它说清"不是全丢"）。
             * 判据只有写在产生它的那一刻才是事实 —— 批末那一刻发布早就做完了，绕一圈去推必然推歪。
             */
            task.PartialPublishedCount = outcome.PublishedCount;
            task.PartialPublishDirectoryPath = outcome.DestinationDirectory;

            /*
             * ⛔ **机器终态跟着走：损坏 + 已经救回一部分 = 「部分完成」**（用户 2026-10-14 口径逐字：
             * 「这个损坏的压缩包……不完整的内容物就留下来，如果用户选择删除，那就删除……
             * 留下就留下，然后**最后的汇总肯定是报部分完成，橙色**」）。
             *
             * 位置：只能在**真发布成功之后**改（前面每一条 return false 都保持原终态）
             * ⇒ "损坏但一个字节都没救回来"照旧落 `Failed`（红），那是对的。
             * 色带与计数不用另写判据：`BatchSummarySeverity.FromOutcomes` / `BatchOutcomeTally`
             * 读的就是这一位（橙 = 有部分完成）。⚠ 需要先过 `FinalizeOutcomeIfPending`（它在收尾更早处，
             * 已经把 `Pending` 兜底成 `Failed`）—— 这里改的是**已经定下来的失败**，不是绕过兜底。
             */
            if (task.Outcome == TaskOutcome.Failed)
            {
                task.Outcome = TaskOutcome.PartiallyCompleted;
            }

            /*
             * 全部搬成之后才动其余物与工作区（「宁可少删」）：
             * 有文件没能发布时它们还在工作区里，那是这一份唯一的线索 —— 删掉等于丢用户的产物。
             */
            if (!outcome.Publish.AllMoved)
            {
                AppendLog(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PartialPublishRestKeptFormat,
                        task.FileName,
                        $"计划里有 {outcome.Publish.Failures.Count} 个文件没能发布（{DescribeFirstFailures(outcome.Publish.Failures)}），"
                        + "所以其余物与工作区都原样留着"));

                return true;
            }

            PurgeRestItemsExceptChainRootSource(task);

            return false;
        }

        /// <summary>
        /// 懒取清单（**只服务部分完成发布**）：拿最后那个候选的密码问一次引擎。
        /// 列不出来（密码不对 / 加密头 / 引擎不可用）时返回空表 ⇒ 下游按"没有清单"办、什么都不发布。
        /// </summary>
        private async Task<IReadOnlyList<(string Path, long Size)>> TryListForPartialPublishAsync(
            ArchiveTask task,
            PartialPublishCandidate candidate)
        {
            try
            {
                ArchiveListResult list = await _archiveEngine.ListAsync(
                    ArchiveRequest.For(candidate.ArchivePath, candidate.Password),
                    CancellationToken.None);

                return PartialPublishPlanner.ToManifestEntries(list);
            }
            catch (Exception ex)
            {
                AppendLog(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PartialPublishSkippedFormat,
                        task.FileName,
                        $"为了逐条核对清单又列了一次目录，这次列目录出错了（{ex.Message}）—— 判不出 ⇒ 什么都不发布"));

                return Array.Empty<(string, long)>();
            }
        }

        /// <summary>把发布失败的头几条拼成一句话（最多 3 条，详情在日志里，⛔ 不刷屏）。</summary>
        private static string DescribeFirstFailures(IReadOnlyList<(string Path, string Reason)> failures)
        {
            if (failures == null || failures.Count == 0)
            {
                return "原因没有记下来";
            }

            var parts = new List<string>();

            foreach ((string path, string reason) in failures.Take(3))
            {
                parts.Add($"{path}：{reason}");
            }

            if (failures.Count > 3)
            {
                parts.Add($"还有 {failures.Count - 3} 个");
            }

            return string.Join("；", parts);
        }


        /// <summary>
        /// 本批「部分完成发布」生不生效（**唯一判据**）。
        ///
        /// <para>= ③页那个开关打开 **且** 本批不是「空间不足」模式。用户 2026-10-02 顾虑 2 点名的危险组合
        /// （空间不足 + 大容量 + 多份相同文件一起开）就靠这一条**硬互斥**挡住：那一档的全部价值是
        /// "定稿 + 校验通过后当场删源包回收空间"，而部分完成**天然回收不了源包**（缺的那些条目只在源包里）
        /// ⇒ 两者目的正好相反。⛔ 只让行为失效，**绝不去改用户的设置**（勾还勾着，只是这一轮不生效）。</para>
        /// </summary>
        private bool PartialPublishActive => Settings?.PartialPublishEnabled == true && !_spaceTightThisBatch;

        /// <summary>
        /// 部分完成发布的候选：**失败路径**填进来，收尾处（清工作区之前）真发布。
        ///
        /// <para>为什么用"候选"而不是就地发布：发布要读到三样只有收尾那一刻才齐的东西 ——
        /// 机器终态（失败 / 部分完成才算数）、最终输出目录（同名冲突可能刚改过名）、
        /// 以及"这一单到底还剩哪些产物"（候选循环里每个候选都会清一次产物目录）。</para>
        /// </summary>
        private sealed class PartialPublishCandidate
        {
            /// <summary>已解出来的东西在哪（暂存目录 / 这一层的产物目录）。</summary>
            public string StagingRoot { get; init; } = string.Empty;

            /// <summary>交给引擎的那个归档（懒取清单时要用；内嵌归档这一档是抠出来的临时文件）。</summary>
            public string ArchivePath { get; init; } = string.Empty;

            /// <summary>这一层最后用的密码（懒取清单时用；只在内存里，⛔ 永不落日志）。</summary>
            public string Password { get; init; } = string.Empty;

            /// <summary>引擎点名的坏条目 / 自报的出错数 / 这一趟是不是报了失败（三道闸门）。</summary>
            public IReadOnlyList<string> EngineFailedEntries { get; init; } = Array.Empty<string>();

            public int EngineReportedErrorCount { get; init; }

            public bool EngineReportedFailure { get; init; }

            /// <summary>
            /// **这一单的失败是"引擎明说包损坏"吗**（<see cref="IsDamagedArchive"/> 是唯一判据出口）。
            ///
            /// <para>它是这一档的**触发事实**：登记那一刻从结构化结果里读一次、之后不再变
            /// （收尾时才决定发不发，但"坏没坏"这件事在失败那一刻就已经定了）。</para>
            /// </summary>
            public bool Damaged { get; init; }

            /// <summary>
            /// **这一单整体到底有没有内容物解出来**（用户 2026-10-16 口径：「部分完成 = 一部分成功 + 一部分失败」：
            /// 「密码错了但是其他的地方有内容物这个**肯定是判部分完成**，但是如果只有一个压缩包，而且密码错误，
            /// 那么就不可能有内容物，因为密码都没有试出来」）。
            ///
            /// <para>它是**终态分类**那一档的判据输入（与"要不要发布"是两件事：发布只认"损坏"，见
            /// <see cref="Damaged"/>）—— 单层路 = 产物目录里真解出来的非空文件够不够
            /// （唯一出口 <c>ProducedContentGate</c>，与"密码已被证实"用同一把尺子，⛔ 不另定阈值）；
            /// 递归路 = 链上**任何一层**产出过文件（<c>RecursionLayerReport.OutputFileCount</c>）。</para>
            /// </summary>
            public bool ChainProducedContent { get; init; }

            /// <summary>
            /// 引擎给的结构化错误类型（<c>EngineErrorTypes</c> 那一套词表里的名字；空串 = 没给）。
            /// 只为**如实写清"为什么没发布"**：那一行要点名这一单到底属于哪一档。
            /// </summary>
            public string DetectedErrorType { get; init; } = string.Empty;

            /// <summary>
            /// 逐条清单（相对路径 + 解压后字节）。**空表 = 手上没有清单** ⇒ 收尾时用这一层的密码
            /// 现问引擎一次（见 <see cref="TryListForPartialPublishAsync"/>）；再拿不到就什么都不发布。
            /// </summary>
            public IReadOnlyList<(string Path, long Size)> ManifestEntries { get; init; } =
                Array.Empty<(string, long)>();
        }


        /// <summary>
        /// 部分完成发布的候选（**失败路径**填、收尾处发布；见 <see cref="TryPublishPartialProductsAsync"/>）。
        ///
        /// <para>键用任务对象本身（理由与上面两张表一样）；条目在收尾时移除 ——
        /// 不清掉的话同一个任务对象重跑时会把上一轮的清单当成这一轮的（那是"拿旧识别结果解新文件"的翻版）。</para>
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ArchiveTask, PartialPublishCandidate> _partialPublishCandidates = new();

    }
}