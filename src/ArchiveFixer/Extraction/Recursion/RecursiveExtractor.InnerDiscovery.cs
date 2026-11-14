using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction
{
    public sealed partial class RecursiveExtractor
    {        /// <summary>
             /// 探测本层产物里的内层归档（**递归**找所有文件，跳过 0 字节与工作区自身）。
             /// </summary>
             /// <param name="outputDirectory">这一层的产物目录（**我们自己解出来的副本**）。</param>
             /// <param name="layerLabel">日志行首那个"第 N 层：包名"标签（与这一层其它日志同形）。</param>
             /// <param name="cancellationToken">取消令牌。</param>
        private async Task<IReadOnlyList<string>> ProbeInnerArchivesAsync(
            string outputDirectory,
            string layerLabel,
            CancellationToken cancellationToken)
        {
            var found = new List<string>();
            var pending = new Stack<string>();

            pending.Push(outputDirectory);

            /*
             * 「名字像分卷的一片、但认不出魔数」的候选（用户 2026-10-08）。
             *
             * 7z 的续卷是**裸切字节流**、跨盘 ZIP 的中间片也没有文件头 ⇒ 探测器一律认不出
             * （`RecursiveExtractor.cs` 那一步只把 `IsArchiveAsync` 为真的收进候选）⇒
             * 真机 `111(3)\111(3)\111.z0删除2` 永远进不了「还原」名单、名字一直脏着。
             *
             * ⛔ 它们**只参与「还原」改名**（删掉名字里塞的中文杂质、换回规范卷标记），
             * **不进这一层的续解名单** —— 用户 2026-10-08 口径：第一大步只做"删除中文字符"这种简单改名，
             * 缺卷结论与组卷归位一律留到批末（那一组的入口本来就在归档候选里，引擎会按同目录的标准卷名找到它们）。
             */
            var renameOnlyPieces = new List<string>();

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string currentDirectory = pending.Pop();
                string[] entries;

                try
                {
                    entries = Directory.GetFileSystemEntries(currentDirectory);
                }
                catch
                {
                    // 单个目录读不了就跳过；漏判只是少展开一层，不该让整次递归失败。
                    continue;
                }

                foreach (string entry in entries)
                {
                    /*
                     * ⛔ 名字叫工作区目录的那一项**永远不是内层归档的候选**（用户 2026-09-30）。
                     *
                     * 工作区默认落在目标目录里面（<目标目录>\.ArchiveFixer.work），而本方法扫的是
                     * "这一层的产物目录" —— 万一哪条岔路上产物目录把工作区套了进去，里面的
                     * 抠取副本 / 逐层中间包**个个都能被识别成归档**，于是被接着解、被当内容物搬出去。
                     *
                     * ⚠ 这里**只按名字排，绝不按"在不在工作区根之下"排**：
                     * 本层的产物目录本身就在工作区里（<根>\recursive\<id>\layer-NNN\output），
                     * 用"根之下"那一条会把本层所有产物一次性全排掉 —— 递归当场变成"什么都没找到"
                     * （2026-09-30 自查实测：两个内层包全被跳过，多分支确认整条路都不再触发）。
                     */
                    if (WorkspaceTree.IsWorkspaceDirectoryName(entry))
                    {
                        continue;
                    }

                    if (!TryGetAttributes(entry, out FileAttributes attributes))
                    {
                        continue;
                    }

                    // 不跟随符号链接 / 联接点：跟着走既可能绕圈，也会把工作区外的文件算成"内层归档"。
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    if (IsZeroByteFile(entry))
                    {
                        // 0 字节文件不可能是归档，却能被某些"看后缀"的探测器误判，直接排除。
                        continue;
                    }

                    /*
                     * ⛔ **第一大步闸门 = 读底层那张卡，不看"引擎认不认"**（用户 2026-10-22 口径：
                     * 「不能仅仅靠引擎能不能反应，而是要靠底层识别的情况」；「闸门留下的是单一完整压缩包，
                     * 其余的非单一压缩包留给第二大步」）。
                     *
                     * <para>判据唯一出口 = `FileNature.Inspect` 的 `BodyKind`：
                     * 只有 `SingleComplete`（含媒体前缀、含"头完好的损坏包"）才收进续解；
                     * `VolumeFirstPiece` / `VolumePiece` / `Unknown`（7z 中间片、ZIP 中间片、RAR5 加密头、
                     * 头坏/截断）一律**不收** —— 它们通过闸门、原地不动，是第二大步的原包。</para>
                     *
                     * <para>⚠ 这里**不再调 `_prober.IsArchiveAsync`**：那个判据问的是"魔数认不认得出归档格式"，
                     * 分卷片同样认得出 ⇒ 第一大步会越界把整组开掉（实测 `24242424` 两份内容物的机制）。</para>
                     */
                    FileNatureCard nature = FileNature.Inspect(entry);

                    /*
                     * ⛔ 这里不做逐条内容识别（2026-11-11）：解压出来的普通图片/视频/文档
                     * 数量可以上千，逐个 `FileContentProbe(deep:true)` 会把解压线程拖死。
                     * 内容容器保护由紧随其后的 `ApplyContentContainerProtection` 按既有唯一出口做；
                     * APK 由下面 `AndroidPackageDetector` 拦；媒体/文档本来就不是 `SingleComplete`。
                     */
                    bool isSingleComplete = nature.BodyKind == ArchiveBodyKind.SingleComplete;

                    /*
                     * ⛔ **真正的 APK 不当"内层包"**（用户 2026-10-10 拍板：「禁止解压真正的 apk 文件，
                     * 因为有些是用户真的要转移到手机上进行安装的」）。
                     *
                     * 它与"是不是归档"无关：APK 按魔数就是 ZIP ⇒ `IsArchiveAsync` 必然为真，
                     * 但它**不是我们要继续往里解的东西**（真机 `魔方.apk` 就是这样被一层层解开、
                     * 还在它内部建工作区、把 `.so` 当内层包继续探的）。
                     *
                     * 判据转调**唯一出口** `AndroidPackageDetector`（内容证据，⛔ 不看后缀）；
                     * ⛔ **判不出 ⇒ 照旧收进候选**（不拦）—— 这是补充闸门，不许因为判据本身出错把正常内层包漏掉。
                     * 三条路的另外两处：单层入口 `ExtractSingleTaskAsync` 开头、一键处理的产物扫描
                     * （`OneClickCoordinator`）。
                     */
                    if (isSingleComplete && !AndroidPackageDetector.IsAndroidPackage(entry))
                    {
                        found.Add(entry);
                    }
                    else if (LegacyVolumeNames.IsVolumePart(Path.GetFileName(entry))
                             || IsArchiveByMagicWithoutVolumeName(entry))
                    {
                        // 见 renameOnlyPieces 的说明：只让它过「还原」改名，⛔ 不进续解名单。
                        renameOnlyPieces.Add(entry);
                    }
                }
            }

            /*
             * ===== 「内容物保留关键词」：命中就**不当内层归档**（用户 2026-10-04 的新功能）=====
             *
             * 用户原话：「只要文件名里面包含着这个字符就不能动……这些压缩文件碰都不要碰」。
             * 所以这一步要**排在还原改名那一步之前**：命中它的既不解开、也不改名 ——
             * 只把它当一件普通内容物留在这一层的结果里（定稿归位到目标目录不算碰）。
             *
             * ⛔ 判据只有一处（<see cref="ContentKeepRules"/>，包含即命中 / 大小写不敏感 / 不做通配正则）；
             * 这里只做两件事：把命中的摘出去、并如实说一句"因为哪个词"。
             * 空关键词列表 ⇒ 一个都不摘（行为与以前逐字相同）。
             *
             * ⚠ 连**同组的兄弟卷**一起摘：一组分卷（`小明.part1.rar` + 后续卷）是**一个**归档，
             * 只摘其中一卷会让引擎拿剩下的残卷去试开（既打不开，还会在日志里留下一条看不懂的失败）。
             */
            found = ApplyContentKeepRules(found, layerLabel);
            found = ApplyContentContainerProtection(found, layerLabel);

            // 排序让候选清单在 UI 与测试里都是稳定顺序（不同文件系统返回顺序不一致）。
            found.Sort(StringComparer.OrdinalIgnoreCase);

            /*
             * 「还原」的入参 = 归档候选 ∪ 那些"只来改名"的卷片。
             * ⛔ 「内容物保留关键词」对后者同样有效（一个都不许动）；⛔ 已在候选里的不重复加。
             */
            var archiveCandidates = new HashSet<string>(found, StringComparer.OrdinalIgnoreCase);
            var renameOnlySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var restoreInput = new List<string>(found);

            if (renameOnlyPieces.Count > 0)
            {
                List<string> keptRenameOnly = ApplyContentKeepRules(renameOnlyPieces, layerLabel);
                keptRenameOnly = ApplyContentContainerProtection(keptRenameOnly, layerLabel);

                foreach (string piece in keptRenameOnly)
                {
                    if (archiveCandidates.Contains(piece) || !renameOnlySet.Add(piece))
                    {
                        continue;
                    }

                    restoreInput.Add(piece);
                }

                restoreInput.Sort(StringComparer.OrdinalIgnoreCase);
            }

            /*
             * ===== 「还原」工序：先擦掉伪装尾巴，再回到第 1 层重判一次（方案 §2.1 挂点②）=====
             *
             * 用户 2026-10-03 的口径：「首先第一步就是识别底层文件找出伪装文件，然后还原，再接着匹配」。
             * 顺序不可颠倒：**① 按魔数认出底层 → ② 还原名字 → ③ 才进族骨架匹配（组卷 / 定序 / 试开）**。
             *
             * 为什么挂在这里：包**里面**解出来的这一层，过去没有任何一步先擦伪装尾巴 ——
             * 一组 `风景01.part1.rar删除` / `.part2.rar删除` 解出来之后名字还是脏的，引擎按标准名
             * 找不到兄弟卷，只报「分卷缺失」，里面的内容永远出不来（AGENTS.md §11.4 §51 的现场）。
             *
             * ⛔ 只碰**我们自己产出的副本**：这里的路径全部来自本层产物目录（外层包刚解出来的东西），
             * 用户源目录里那一档归批首的「修正后缀」管，一步都不越界。
             * ⛔ 阶段 2 口径：自动还原本层伪装内层包名整条下线；名字只作为候选，
             * 不在这里改盘。原路径原样进入 `CollapseSameGroupVolumes`，由组事实/族算法判。
             */
            IReadOnlyList<string> restoredAll = restoreInput;

            /*
             * ⛔ 只把**归档候选**带进这一层的续解名单：
             * "只来改名"的卷片在这一步已经改完了（盘上名字变了），它们**不**变成续解入口 ——
             * 那一组的入口本来就在候选里，引擎会按同目录的标准卷名去找兄弟片。
             * （判据 = 入参身份，⛔ 不比中文文案、⛔ 不看后缀。）
             */
            var restored = new List<string>(found.Count);

            for (int index = 0; index < restoreInput.Count; index++)
            {
                if (!renameOnlySet.Contains(restoreInput[index]))
                {
                    restored.Add(restoredAll[index]);
                }
            }

            return CollapseSameGroupVolumes(restored);
        }

        /// <summary>
        /// 一层成功之后判断是否还要继续，以及继续哪些。
        /// 返回 false 表示停止，并通过 out 参数说明是"要问用户"还是"命中上限"。
        ///
        /// 判断顺序是固定的：模式（SingleLayer 直接收尾）→ 用户点名的清单过滤 → 数量上限 →
        /// 单链启发式 / 要不要问用户 → 层数上限后入队。
        /// 这个顺序本身就是规则（规则 4 与规则 5 的交界），改任何一步之前先读那一步的注释。
        /// </summary>
        private bool TryEnqueueNextLayers(
            WorkItem item,
            RecursionLayerReport report,
            ExtractionWorkspace workspace,
            EnqueueState state,
            out RecursionDecisionRequest? decision,
            out RecursionStopReason stopReason)
        {
            decision = null;
            stopReason = RecursionStopReason.None;

            IReadOnlyList<string> innerArchives = report.InnerArchives;

            if (innerArchives.Count == 0)
            {
                // 这一层里没有可继续的东西，队列自然消耗干净；停因在循环结束时统一判定为 Completed。
                return true;
            }

            /*
             * 空间不足：最后一层过程物必须在**它被逐层回收 / 消费之前**留下。
             * 这里只是把"这一层发现的内层包"复制到最终其余物，真正的"删不删/留哪个"
             * 仍由收尾那一档按 RetainedProcessArtifactPath 决定；⛔ 不搬原件、不改名。
             */
            if (_limits.KeepDeepestProcessArtifact)
            {
                foreach (string innerArchivePath in innerArchives)
                {
                    RetainProcessArtifactBeforeConsumption(innerArchivePath);
                }
            }

            /*
             * 手动单层：第 0 层成功就是终点。内层归档原样留在这层内容物里，
             * ⛔ 不标记为"未展开"（否则收尾会判部分完成、留工作区）。
             */
            if (_limits.ManualSingleLayer)
            {
                stopReason = RecursionStopReason.Completed;
                return false;
            }

            List<string> toProcess = innerArchives.ToList();

            /*
             * ⛔ **不是这一组的开启者，就不许在本单的递归里开它**（2026-10-22，`24242424` 现场）：
             * 判据由调用方给（组事实 + 本批已开者账）。没注入 ⇒ 一个字不改。
             */
            if (ShouldStopAtVolumeGroupMember != null)
            {
                toProcess = toProcess
                    .Where(path => !ShouldStopAtVolumeGroupMember(path))
                    .ToList();
            }

            /*
             * previousDecision 的语义（规则 4）：只处理用户点名的那几个归档。
             * 按**文件名**比对（大小写不敏感，见 DecisionNames 的注释），**不重新全盘扫描** ——
             * 用户点头的是他看到的那个清单，不是"这一层里现在有什么"。
             */
            bool decidedByUser = item.DecisionNames != null;

            if (decidedByUser)
            {
                var chosen = new HashSet<string>(item.DecisionNames!, StringComparer.OrdinalIgnoreCase);

                toProcess = toProcess
                    .Where(path => chosen.Contains(Path.GetFileName(path)))
                    .ToList();
            }

            /*
             * 数量上限的位置是刻意的（规则 4 与规则 5 的交界）：
             * ① 放在"用户点名的清单"过滤**之后**：他点的就是他要的，先查上限会让"只选 3 个"
             *    也被拦下，与 previousDecision 的语义直接冲突；
             * ② 放在"要不要问用户"**之前**：一层里冒出几十个内层归档，说明这不是套娃包，
             *    而是一个归档集合 —— 该给的结论是 TooManyInnerArchives（去逐个单独处理），
             *    而不是把同一个事实包装成"要不要继续展开"再问一遍，那个问题对用户没有意义。
             */
            if (toProcess.Count > _limits.MaxInnerArchivesPerLayer)
            {
                state.MarkUnexpanded(toProcess);
                stopReason = RecursionStopReason.TooManyInnerArchives;
                return false;
            }

            foreach (string innerArchivePath in toProcess)
            {
                /*
                 * 层数上限在入队前查（规则 5）：下一层已经到 MaxDepth 就不再入队，
                 * 已经排在前面的项照常解完 —— 那些产物是有效的，扔掉它们没有意义。
                 *
                 * 判据是**层号** item.Depth + 1，不是"已经解了几层"（或者队列长度）：
                 * 分支模式下同一层会排好几项，拿已解层数当层号会把合法的宽树误拦
                 * （两个分支、MaxDepth = 3 时，第二个分支会被算成"第 3 层"而拦掉）。
                 */
                if (item.Depth + 1 >= _limits.MaxDepth)
                {
                    state.MarkUnexpanded(toProcess);
                    stopReason = RecursionStopReason.MaxDepthReached;
                    return false;
                }

                state.Pending.Enqueue(new WorkItem
                {
                    Depth = item.Depth + 1,
                    ArchivePath = innerArchivePath,
                    Layer = workspace.CreateNextLayer(innerArchivePath),
                    IsRoot = false,

                    /*
                     * 递归路逐层回收：这一项开工时该当场还回去的那一份（= 它要解的那个内层包，
                     * 分卷组则整组）。**只有在真入队的分支上才建项** —— 上面四条提前返回
                     * （多分支不展开 / 到层数上限 / 超过每层数量上限 / 用户只点名了几个）
                     * 各自 `return false`，那几种情况下没入队的归档要原样留着当内容物。
                     */
                    Reclaims = new[] { BuildLayerReclaim(item, innerArchivePath, workspace, report) }
                });
            }

            return true;
        }

        /// <summary>
        /// 给"刚刚入队的那一项"备一份**进场时要还回去的过程物**（递归路逐层回收的候选）。
        ///
        /// <para><b>这一层自己能不能被证明完整</b>（L4）：判据与单层路径**同一套**（AGENTS.md §11.6）——
        /// 拿**这一层的清单**（<see cref="RecursionLayerReport.Manifest"/>，解压前那次列目录的结论）
        /// 过 <see cref="OutputVerifier.Verify"/>，再过 <c>ResultCompletenessClassifier</c>。
        /// ⛔ 这里不另写一份"完整不完整"的判据；拿不到清单 / 没能逐条核对 ⇒ 判「判不出」⇒
        /// 调用方什么都不做（红线：判不出 ⇒ 一个字节都不动）。</para>
        ///
        /// <para><b>要还哪几个文件</b>：入队的那一个内层归档 + **同组的其余卷**
        /// （组员判据转调既有那一把折叠尺 <see cref="IsSameGroupContinuationVolume"/>，
        /// ⛔ 不在这里另写一套名字规则）。为什么要连整组：只删首卷会留下再也拼不起来的碎片，
        /// 而它们本来就被同一次解压消费掉了。判不出组员 ⇒ 少删几个（宁可少删，绝不误删）。</para>
        ///
        /// <para><b>拿不到层号 / 不是在入队那一刻</b>都不会走到这里：本方法是入队的**从属动作**，
        /// 读的全是调用方刚拿在手上的事实（<paramref name="layerReport"/> 是这一层的报告、
        /// <paramref name="workspace"/> 是本次运行的工作区）。</para>
        /// </summary>
        private static RecursiveLayerReclaimRequest BuildLayerReclaim(
            WorkItem item,
            string innerArchivePath,
            ExtractionWorkspace workspace,
            RecursionLayerReport layerReport)
        {
            var groupFiles = new List<string> { innerArchivePath };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { innerArchivePath };

            /*
             * 同组的其余卷：**只在那一层里认**（引擎找兄弟卷也只看入口文件旁边那一层）。
             * 读不动 / 一个都认不出 ⇒ 就只还首卷这一份（少删是安全的，误删不是）。
             */
            string? directory = Path.GetDirectoryName(innerArchivePath);

            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                string[] siblings;

                try
                {
                    siblings = Directory.GetFiles(directory!);
                }
                catch
                {
                    siblings = Array.Empty<string>();
                }

                foreach (string sibling in siblings)
                {
                    if (seen.Add(sibling) && IsSameGroupContinuationVolume(sibling, innerArchivePath))
                    {
                        groupFiles.Add(sibling);
                    }
                }
            }

            /*
             * 这一层的完整性结论（唯一判据出口与单层路径同一个）：
             * 有可信清单（`LayerManifest.Available`）才拿它当预期，否则照旧喂"没有清单"那一档
             * —— `OutputVerifier.Verify` 对 null 与"空清单"的处理就是"只做非空底线校验"，
             * 而 `ResultCompletenessClassifier` 把那一档判成「判不出」。
             */
            OutputVerificationResult verification = OutputVerifier.Verify(
                item.Layer.OutputPath,
                layerReport.Manifest.Available ? layerReport.Manifest.ToExpected() : null);

            return new RecursiveLayerReclaimRequest
            {
                ChildLayerLabel = DescribeProbeLabel(item),
                GroupFiles = groupFiles,
                Verdict = ResultCompletenessClassifier.Classify(verification),
                WorkspaceTaskDirectory = workspace.TaskDirectory
            };
        }
        ///
        /// <para>⚠ <b>2026-10-04 起它不再是"单链判定"</b>（用户当场推翻旧口径：「这是压缩包吗，
        /// 不是那你停什么」）：停 / 不停的判据已经改成**只看这一层里有几个真归档**
        /// （见 <see cref="TryEnqueueNextLayers"/> 里 <c>isSingleChain</c> 那一段）。
        /// 它现在的唯一用途是：那一层**只有 1 个内层归档**时，决定要不要多写一句
        /// "旁边还有别的文件也照旧继续解" —— 说明类后缀表因此**一个字都没删**。</para>
        ///
        /// <para>⚠ 2026-10-02 修（用户真机 `1-6 电磁感应定律（1）`，见 `docs/真机事故复盘.md` §47.3）：
        /// **同一组的后续卷算那一份归档的一部分，不算"别的文件"**。现场 = 这一层里是
        /// <c>51658213.7z.001</c>（**唯一**的内层归档）+ <c>51658213.7z.002</c>（同组后续卷），
        /// 老判据只跳过"内层归档那一个文件"，于是 <c>.002</c> 被算成另一个分支 ⇒ 单链不成立 ⇒
        /// 日志写着「这一层里有 **1 个**内层归档（多分支）」并保守停在那一层：一键档白白多跑一轮
        /// （真机上第 2 轮才解出来），手动档还会拿"要不要展开多分支"去问用户 —— 而他看到的只有一个包。</para>
        ///
        /// <para>⛔ 认的只有**同目录 + 同包基名 + 是后续卷**三条同时成立的文件
        /// （<see cref="IsSameGroupContinuationVolume"/>）：别的目录、别的基名的 <c>.002</c>
        /// 照旧算"别的文件"，宁可多写一句也不当作没看见。</para>
        /// </summary>
        private static bool HasOnlyInformationalSiblings(RecursionLayerReport report, string innerArchivePath)
        {
            string outputRoot = SafePathHelper.GetFullPathSafe(report.OutputPath);
            string archivePath = SafePathHelper.GetFullPathSafe(innerArchivePath);

            if (outputRoot.Length == 0 || archivePath.Length == 0)
            {
                return false;
            }

            var pending = new Stack<string>();
            pending.Push(outputRoot);

            while (pending.Count > 0)
            {
                string currentDirectory = pending.Pop();
                string[] entries;

                try
                {
                    entries = Directory.GetFileSystemEntries(currentDirectory);
                }
                catch
                {
                    // 读不了就当作"有疑问"，走需要询问的一侧更安全。
                    return false;
                }

                foreach (string entry in entries)
                {
                    if (!TryGetAttributes(entry, out FileAttributes attributes))
                    {
                        return false;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        // 链接指到哪我们不知道；算作"非说明类"，宁可问用户。
                        return false;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    if (string.Equals(
                            SafePathHelper.GetFullPathSafe(entry),
                            archivePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (IsSameGroupContinuationVolume(entry, archivePath))
                    {
                        // 同目录、同包基名的后续卷 = 这一份归档自己的另一片，不是另一个分支。
                        continue;
                    }

                    if (!IsInformationalFile(entry))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

    }
}
