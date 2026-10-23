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
    internal sealed partial class ExtractionCoordinator
    {





        ///
        /// <para>事实来源只有一处：<see cref="RecursionResult.FinalOutputPath"/> 指向的那一层产物目录
        /// —— 引擎解出来的东西就是它们，别的一律是让这一单能开工的过程物（源包 / 分卷 / 内层包）。</para>
        ///
        /// <para>⛔ 为什么必须问这一句：外层跨盘 zip 解出来的 <c>222.zip</c> 后缀就是归档后缀，
        /// 老判据当场把它当"待续解的过程物"，随后那道分卷组闸门看见同目录还有一卷名字不标准
        /// ⇒ 整层定稿作废 ⇒ 用户的 678 MB 产物一个字节都没落地（日志还写着"解压成功 ｜ 校验通过"）。
        /// 后缀骗人这件事在别处已经吃过很多次亏了，这里按**产物清单**判。</para>
        ///
        /// <para>拿不到递归结果（预检那一档、或引擎没报产物目录）⇒ 一律 false：退回老判据，
        /// 一个字都不变（兜底落在"什么都不做"那一档）。</para>
        /// </summary>
        private static bool IsEngineOutputFile(string filePath, string stageRoot, RecursionResult? engineOutput)
        {
            if (engineOutput == null)
            {
                return false;
            }

            // ① 这条链最后交出来的那一层（老口径，逐字保留）。
            if (MatchesEngineOutputRoot(filePath, stageRoot, engineOutput.FinalOutputPath))
            {
                return true;
            }

            /*
             * ② ⚠ 2026-10-09：**这条链每一层**的产物目录也算数（真机 EEEE 排障行实测）。
             *
             * 现场：`111.rar` 那条链**中途停在缺卷**（第 2 层什么都没产出）⇒ `FinalOutputPath` 指向
             * 那最后一个空层目录 ⇒ 第 **1** 层解出来的 `111.zip` 按同名相对路径对不上 ⇒
             * 被判成「过程物」收进其余物 —— 可它明明是本任务链产出的**内容物**
             * （用户原话：「他就是最初 111.rar 经过两轮解压产出的内容物而已，为什么会进其余物里面」）。
             * ⇒ 判据从"这一趟最后那一层"改成"**本任务链产出的东西**"。
             * ⛔ 老口径那一条（①）保留：它覆盖"跑完了、FinalOutputPath = 入仓 stage"那一档。
             */
            foreach (RecursionLayerReport layer in engineOutput.Layers)
            {
                if (layer != null && MatchesEngineOutputRoot(filePath, stageRoot, layer.OutputPath))
                {
                    return true;
                }
            }

            /*
             * ③ ⚠ 2026-10-09（EEEE ② 的真根因，上面 ①② 都盖不住）：**问这一层自己那份清单**。
             *
             * 为什么 ①② 会漏（排障行实测：`FinalOutputPath=…\layer-002\output`、
             * `在产物清单里=False`，而那个文件就在暂存目录里）：**发布是"就地替换 + 移动"**
             * （`ExtractionWorkspace.Publish`）—— 产物**搬走之后，原来那一层目录里就没有它了**，
             * 于是 `MatchesEngineOutputRoot` 要的那句"两边同一个相对路径都在盘上"永远不成立。
             * ⇒ 判据不能读"层目录现在还剩什么"（那个事实已被发布本身抹掉），只能读**当场记下来的账**。
             *
             * 记在哪：`RecursionLayerReport.ManifestEntries` —— 每层**解压前列目录那一次**的逐条清单
             * （相对路径 + 解压后字节，`PartialPublishPlanner.ToManifestEntries` 归一过：`/` → `\`、
             * 去掉开头 `./`），与暂存目录那边算相对路径的写法同源。它是"这一层的引擎写出来的东西"
             * 的直接证据，而且**不受发布搬运影响**。
             *
             * ⛔ 判不出来路时（清单没有 / 引擎没报）**一个字都不改**：`engineOutput == null` 那几档
             * 仍然一律 false ⇒ 退回老判据 + 分卷组完整性闸门（`VolumeGroupDeletionSafetyTests`
             * 那一族钉的就是这条兜底，⛔ 不许放宽）。
             */
            foreach (RecursionLayerReport layer in engineOutput.Layers)
            {
                if (layer != null && MatchesEngineManifestEntry(filePath, stageRoot, layer.ManifestEntries))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 暂存里这个文件，是不是<paramref name="manifestEntries"/>（某一层列目录的逐条清单）
        /// 里那一条 —— 判据 = 两边**归一后的相对路径逐字相等**（事实；⛔ 不比中文、⛔ 不看后缀）。
        ///
        /// <para>存在的理由见 <see cref="IsEngineOutputFile"/> ③：发布是移动 ⇒ 层目录里那份没了，
        /// 只有这份**当场记下来的清单**还能回答"这一层到底产出过这个路径没有"。</para>
        /// </summary>
        private static bool MatchesEngineManifestEntry(
            string filePath,
            string stageRoot,
            IReadOnlyList<(string Path, long Size)> manifestEntries)
        {
            if (manifestEntries == null || manifestEntries.Count == 0)
            {
                return false;
            }

            if (!TryGetStageRelativePath(filePath, stageRoot, out string relative))
            {
                return false;
            }

            foreach ((string path, long _) in manifestEntries)
            {
                if (!string.IsNullOrWhiteSpace(path)
                    && string.Equals(
                        path.TrimStart('.', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        relative,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 暂存里这个文件相对暂存根的路径（<c>\</c> 分隔、不带前导分隔符）。
        /// <c>false</c> = 它根本不在暂存根底下（⛔ 那种情形一律不认）。
        /// </summary>
        private static bool TryGetStageRelativePath(string filePath, string stageRoot, out string relative)
        {
            relative = string.Empty;

            string full = SafePathHelper.GetFullPathSafe(filePath);
            string root = SafePathHelper.GetFullPathSafe(stageRoot);

            if (full.Length == 0 || root.Length == 0 || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            relative = full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return relative.Length > 0;
        }

        /// <summary>
        /// 暂存里这个文件，是不是<paramref name="outputRoot"/>那一层产物目录里同一个相对路径的那一份
        /// （判据 = 事实：两边都存在、路径逐字相等；⛔ 不比中文、⛔ 不看后缀）。
        /// </summary>
        private static bool MatchesEngineOutputRoot(string filePath, string stageRoot, string outputRoot)
        {
            if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
            {
                return false;
            }

            string full = SafePathHelper.GetFullPathSafe(filePath);
            string root = SafePathHelper.GetFullPathSafe(stageRoot);

            if (full.Length == 0 || root.Length == 0 || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string relative = full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (relative.Length == 0)
            {
                return false;
            }

            string candidate = SafePathHelper.GetFullPathSafe(Path.Combine(outputRoot, relative));

            return candidate.Length > 0
                   && File.Exists(candidate)
                   && string.Equals(candidate, full, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// **解前预检用的那一问**：这一单所在的目录里，围绕它到底组成了哪一组、齐不齐。
        ///
        /// <para>试开那一档在这里做（有可用引擎时才做）：临时物落
        /// <see cref="PathService.WorkDirectory"/>（= <c>&lt;目标目录&gt;\.ArchiveFixer.work</c>，
        /// 用户 2026-09-30 口径）；它和第一卷不在同一个卷上时试开器**如实退回**卷根那一档
        /// （硬链接不能跨卷，⛔ 绝不复制大文件），退化原因写在证据里。</para>
        ///
        /// <para>⛔ 只读：硬链接 + 引擎列目录，源文件一个字节都不动；试开目录收工即删。</para>
        /// </summary>
        private async Task<VolumeGroupResolution> ResolveVolumeGroupAsync(
            ArchiveTask task,
            CancellationToken cancellationToken)
        {
            try
            {
                bool engineReady = _archiveEngine != null && _archiveEngine.IsAvailable;

                var verifier = engineReady ? new VolumeProbeVerifier(_archiveEngine!) : null;

                VolumeGroupResolver resolver = verifier == null
                    ? new VolumeGroupResolver()
                    : new VolumeGroupResolver(verifier.TryOpenAsync);

                return await resolver.ResolveAsync(
                        new VolumeGroupQuery
                        {
                            AnchorPath = task.CurrentPath,
                            WorkRootDirectory = _pathService.WorkDirectory,
                            AllowTrialOpen = verifier != null
                        },
                        cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                /*
                 * 判定器本身出错**不许改变"开不开"**（那不变量由上面那段负责），也不许把整单拖垮：
                 * 退回老口径的那句话，并把出错写清楚（判不出就是判不出）。
                 */
                AppendLog("WARN", $"{task.LogName}：分卷组装判定没能做完（{ex.GetType().Name}），按老口径报缺卷。");

                return new VolumeGroupResolution
                {
                    Verdict = VolumeGroupVerdict.Undetermined,
                    AnchorPath = task.CurrentPath,
                    Reason = "判不出：分卷组装判定没能做完（不删、不移动、不改名）"
                };
            }
        }

        /// <summary>把判定结论拼成给用户看的那一句话（判不出时如实说"判不出" + 依据）。</summary>
        private static string BuildVolumeVerdictMessage(ArchiveTask task, VolumeGroupResolution resolution)
        {
            string baseline = string.IsNullOrWhiteSpace(task.VolumeInfoText)
                ? "分卷不完整，缺少分卷"
                : task.VolumeInfoText;

            if (resolution == null || resolution.Verdict == VolumeGroupVerdict.Undetermined)
            {
                string why = resolution == null || string.IsNullOrWhiteSpace(resolution.Reason)
                    ? string.Empty
                    : $"（{resolution.Reason}）";

                return baseline + why;
            }

            if (resolution.Verdict == VolumeGroupVerdict.Complete)
            {
                /*
                 * ⛔ "完整"有两档来源，**不许混着说**：
                 *   ① 试开真的跑过并成立（决定性证据）→ 才敢说"按试开验证"；
                 *   ② 只有名字 / 体积 / 位置 / 物理同一性（前五条）说完整 → 那是"看不出缺哪一卷"，
                 *      不是"验证过"。老写法在这里一律写"按试开验证这一组是齐的"，
                 *      而试开这一档**在这一路上根本没跑**（没引擎 / 跨卷 / 顺序凑不齐时都是"没试"）——
                 *      那是把"没试"说成"验过"（AGENTS：⛔ 不许假装做到了）。
                 */
                bool trialConfirmed = resolution.Evidence.Any(row =>
                    row.Kind == VolumeEvidenceKind.TrialOpen
                    && row.Outcome == VolumeEvidenceOutcome.Satisfied);

                if (trialConfirmed)
                {
                    /*
                     * 试开证明这一组是**齐的**，只是有卷的名字不是 7-Zip 认的标准名 ——
                     * 老判据在这里只会说「分卷缺失」，把用户指去满盘找一卷根本不缺的卷。
                     */
                    return $"{baseline}；但按试开验证这一组是齐的（{resolution.Reason}）";
                }

                return $"{baseline}；按名字与体积这一组看不出缺哪一卷（{resolution.Reason}）"
                    + " —— 这一条没有经过试开确认，所以只当参考，缺卷以任务自己那份识别结果为准"
                    + "（⛔ 程序不会去改你的文件名，也不会动你的文件）";
            }

            if (resolution.MissingVolumeNames.Count > 0)
            {
                return $"{string.Join("、", resolution.MissingVolumeNames)}：{resolution.Reason}";
            }

            return $"{baseline}；{resolution.Reason}";
        }

        /// <summary>证据表进日志（判不出时也要写：用户与排障都要看得到"依据是什么"）。</summary>
        private void LogVolumeGroupEvidence(ArchiveTask task, VolumeGroupResolution resolution)
        {
            if (resolution == null || resolution.Evidence.Count == 0)
            {
                return;
            }

            AppendLog("INFO", $"{task.LogName}：分卷组装判定 = {resolution.Reason}");

            foreach (VolumeEvidence row in resolution.Evidence)
            {
                AppendLog(
                    "INFO",
                    $"    · 证据「{row.Title}」（{WeightText(row.Weight)}，{OutcomeText(row.Outcome)}）：{row.Detail}");
            }
        }

        private static string WeightText(VolumeEvidenceWeight weight) => weight switch
        {
            VolumeEvidenceWeight.Decisive => "决定性",
            VolumeEvidenceWeight.Strong => "强",
            _ => "弱"
        };

        private static string OutcomeText(VolumeEvidenceOutcome outcome) => outcome switch
        {
            VolumeEvidenceOutcome.Satisfied => "成立",
            VolumeEvidenceOutcome.NotSatisfied => "不成立",
            _ => "没取到"
        };





        /// <summary>
        /// 递归解压一个任务，并把结果落到任务状态上。
        ///
        /// <para>
        /// 多分支（一层里有多个内层包）默认**必须问用户**（不变量 8）。但**一键处理期间一个字都不问**：
        /// 用户 2026-09-27 原话 —— "什么叫做一键解压，就是说用户选中你来，然后自己去忙了，
        /// 根本就没有空管你，以后不要出现弹窗"。一键档下按**保守档**办：只保留当前这一层的结果
        /// （等于用户在那种框里点"取消"），并在日志与汇总里写明"还剩 N 个分支没展开"。
        /// </para>
        /// </summary>
        /// <param name="engineArchivePath">
        /// 真正交给引擎的归档路径。内嵌归档时它是工作区里抠出来的那个文件（不是 <c>task.CurrentPath</c>）。
        /// </param>
        /// <param name="stageDirectory">
        /// 暂存目录（入仓阶段）。递归核心把叶子层产物发布到**这里**，不直接写最终目录 ——
        /// 定稿那一步才把它们搬进 <see cref="ArchiveTask.OutputPath"/>。
        /// </param>
        /// <param name="oneClickRun">
        /// 这一批是不是「一键处理」/「继续解」发起的（**只决定"要不要弹框"**，⛔ 不再决定展开策略 ——
        /// 展开一律按②页所选档位，见方法说明与 <c>expandAll</c> 那一段）。
        /// </param>
        /// <returns>
        /// 递归结论（调用方要用它判断"展开了几层"——结果校验拿什么当预期全靠这个，见
        /// <see cref="PostProcessSuccessAsync"/> 的 <c>recursion</c> 参数）。
        /// </returns>
        private async Task<RecursionResult?> RunRecursiveAsync(
            ArchiveTask task,
            string engineArchivePath,
            string stageDirectory,
            bool oneClickRun,
            CancellationToken cancellationToken)
        {
            /*
             * ②页「嵌套压缩包」那三档 → 递归核心的模式。**三档三义，一档都不许合并**。
             *
             * ⛔ 2026-10-01 修：这里原来是 `AllBranches ? AllBranches : SingleChain` ——
             * 把 **「只解当前这一层」也当成「单链自动展开」**跑了：界面上写着"只解当前这一层"、
             * `docs/使用说明.md` §10.1 写着"里面的包原样留着、你自己决定下一步"、人工测试清单 C35 也写着
             * "默认档下只解一层"，而程序实际上一条链往下解到底（并且顺带把单层档承诺的"快路"
             * ——第 0 层走 ZIP 直读、每层交给一键处理的一轮——整个绕过去了，见 `DirectReadAppliesTo`）。
             * 用户 2026-10-01 追问"开了续解和不开续解会不会有 bug"，指的就是这个：两档当时**行为完全相同**。
             *
             * ⚠ 2026-10-04（用户当场推翻默认档）：出厂默认改成 **`AllBranches`**（"有压缩包就解压"），
             * 所以走到这一段的**常态**就是全部分支。三档语义（与②页逐字对齐）：
             * · `SingleLayer` = 只解当前这一层，内层包**原样留着**（继续解交给一键处理的轮次 / 手动「继续解」）
             *   —— 上游那道闸门（`递归档位（已删除） != "SingleLayer"`）就把它挡在递归核心之外，到不了这里；
             * · `SingleChain` = 只跟一条链；一层里出现 **≥2 个真归档** ⇒ 第 0 层问用户，
             *   更深的层停在那一层并**如实报停因**（旁边是什么文件都不影响这条判据）；
             * · `AllBranches`（出厂默认）= 内层有多个包也全部解开（仍受每层内层包数量上限约束）。
             *
             * ⚠ 2026-10-01 核查记录（用户问"开了续解和不开续解会不会有 bug"）：下面这条 switch 曾经写成
             * `AllBranches ? AllBranches : SingleChain`，看上去像"把 SingleLayer 也当 SingleChain 跑"，
             * 但**实际不是缺陷** —— 真的闸门在上游（`递归档位（已删除） != "SingleLayer"` 才进递归核心），
             * 撤掉这一处的改动跑守门用例照旧全绿（红检否掉了那个怀疑）。把它写成显式三档只是为了
             * "以后谁动了那道闸门，这里不会静默降级成别的档"。
             */

            // 上限（层数 / 每层密码尝试次数）当场从设置里取，并交给**本次**的递归核心：
            // 构造时固定一份的话，用户改完设置不重启就不生效（见字段上的说明）。
            RecursionLimits limits = BuildRecursionLimits();
            RecursiveExtractor recursiveExtractor = CreateRecursiveExtractor();
            // 「先测试再解压」：用户开了这一档就必须交给递归核心（它才有 TestAsync 那一支）。
            recursiveExtractor.TestBeforeExtract = Settings.TestBeforeExtract;

            /*
             * **这一趟打不开的那几片，抢救到哪儿**（真机 2026-10-06）：
             * 那一片（`111.zip`）是下一轮那一组的入口包，而递归核心收尾会清掉它自己的工作区
             * ⇒ 协调器拿到结论时它已经不在了（排障日志逐字：`那一片已经不在盘上（…\output\111.zip）`）
             * ⇒ "入口包留在它自己那条链里"整条路断掉 ⇒ 结果校验看到空目录 ⇒ 列表显示「解压失败」+ 源包不处理。
             * ⇒ 把这一单的**暂存目录**给它（收尾链读的就是它），它在记下那片的同时复制一份过去。
             */
            recursiveExtractor.UnresolvedPieceRescueDirectory = stageDirectory;

            /*
             * 「可疑条目提示」的回传口（2026-10-05 只读审计）：递归核心在拿到本层清单时算出那句话，
             * 这里把它写进**与单层路径同一个字段**、走**同一个出口**（PublishDangerousEntriesHint）——
             * 于是默认档下内层包里的 `.exe / .bat / .ps1` 也会像源包那样被点名。
             *
             * ⚠ 写的是**协调器手上这个 task**（递归核心拿到的可能是内嵌归档的探针任务），
             * 提示属于用户看到的那一行。
             */
            recursiveExtractor.DangerousEntriesReported = hint => PublishDangerousEntriesHint(task, hint, "INFO");

            /*
             * 「这一层的密码被验证成功」⇒ 记进**本批已成功的密码**（用户 2026-09-26 第 45 条）。
             *
             * ⛔ 为什么必须补这一处：默认档是「有压缩包就解压」，解压交给递归核心做 ⇒
             * 递归路不报这一声的话，`PasswordService.RecordPasswordSuccess` 在**默认档下一次都不会被调到**
             * ⇒ 后面每一个包的候选表里都没有"复用本批已成功的密码" ⇒ 每个包都从空密码开始把整张表重试一遍
             * （用户原话：「不就是在放屁吗，每次都是重新一轮密码检测」）。
             * 2026-10-16 实测（`PasswordReuseTests.第一包第五候选成功…`）：同一批第二个包拿到的候选表
             * 逐字是 ` | <示例密码1> | <示例密码2> | <示例密码3> | <示例密码4> | <示例密码5>` —— 与第一个包**一模一样**。
             *
             * 挂源文件（`task.CurrentPath`）而不是工作区里那一层：与单层路那三处**逐字同一口径**
             * （"密码成功记录仍然挂在**源文件**上：下次用户再导入这个文件时要能直接命中，
             * 而工作区里抠出来的临时文件活不过这次任务"）。
             */
            recursiveExtractor.PasswordVerified = password =>
                _passwordService.RecordPasswordSuccess(task.CurrentPath, password);

            /*
             * 记下"这一单用的是哪个递归核心"：它手上的逐层工作区要在任务收尾时按结论处理
             * （成功由它自己清；失败 / 取消 / 部分完成由 CleanupFailedTaskWorkspace 按设置决定，见那里的说明）。
             * 取消那一条路上 ExtractAsync 直接抛异常，只有在这里留着实例，收尾才找得到那份工作区。
             */
            _taskRecursiveExtractors[task] = recursiveExtractor;

            task.Status = StatusText.Extracting;
            task.ProgressText = StatusText.ProgressProcessing;
            task.LastUpdatedTime = DateTime.Now;

            /*
             * 递归核心（RecursiveExtractor）从 task.CurrentPath 取"第 0 层要解哪个归档"。
             * 内嵌归档真正的归档是抠出来的那个文件，所以这里给它一个只带正确路径的**探针任务**，
             * 而不是把 task.CurrentPath 临时改掉 —— 源文件路径是改名 / 清理 / 统计的依据，
             * 任何"改了再改回来"的写法都会在这些地方留下一个窗口期（异常时更是永远改不回来）。
             * 递归结论仍然应用在真正的 task 上（ApplyRecursionResult(task, result)）。
             */
            ArchiveTask recursionTask = string.Equals(
                    engineArchivePath,
                    task.CurrentPath,
                    StringComparison.OrdinalIgnoreCase)
                ? task
                : new ArchiveTask(engineArchivePath);

            /*
             * ⚠ 2026-10-15：这一行原来打印**用户设置里那一档**（真机日志逐字：
             * `002.7z：开始递归解压（模式 SingleChain，最大 5 层）。`）—— 屏幕上不该再出现档位名
             * （用户口径：「我不是将单层路给删掉了吗，现在就是全面解压所有分路」）。
             * 现在如实写"有压缩包就解压"，层数上限照旧是用户设置。
             */
            AppendLog("INFO", $"{task.LogName}：开始递归解压（有压缩包就解压，最大 {limits.MaxDepth} 层）。");

            /*
             * 递归内层的进度（2026-09-22 补）：复用**同一个** TaskProgressSink，
             * 于是界面上的百分比 / 当前条目、详情窗口的耗时、以及"跨 10% 写一行"的日志
             * 与单层路径完全是同一套行为 —— 递归核心只负责把 sink 转发给引擎。
             *
             * 卡住提示同理：一层套一层时最容易出现"几十秒没有任何输出"，
             * 那一段过去在界面上就是死等（整条递归连一条 WARN 都不会有）。
             */
            ProgressHeartbeatStep.Sink recursionProgressSink = _progressHeartbeat.CreateSink(task);

            RecursionResult result = await recursiveExtractor.ExtractAsync(
                recursionTask,
                stageDirectory,
                previousDecision: null,
                cancellationToken,
                recursionProgressSink,
                notice => _progressHeartbeat.HandleStalled(task, notice));

            /*
             * ===== 跨链收卷（2026-10-05 真机第九批 CCCC）=====
             *
             * 位置必须在**工作区被清掉之前**：这一趟没走完时，工作区里那一片（真机上是末片 `111.zip`）
             * 可能正是"同一分卷组"里别单缺的那一片，而链一收尾工作区就整份删掉 ⇒ 批末那一站到盘上一看，
             * 缺的东西刚刚被自己扔掉（真机 22:06:02「已清理工作区：2 个文件 / 48.35 MiB」就是这么把末片带走的）。
             */
            /*
             * ⛔ **落地不能放在这里**（2026-10-06 真机当场踩到）：`PublishUnresolvedVolumePieces`
             * 把入口包（`111.zip`）从这一层的产物目录里**搬走**了，而紧接着的结果校验看的正是那个目录
             * ⇒ 「校验未通过：输出目录是空目录，没有产物」⇒ ① 列表里显示「解压失败」② 「完整性：可证不完整」
             * ⇒ 源包一个字节都不处理。落地挪到 `PostProcessSuccessAsync` 里**校验与定稿都过了之后**做
             * （那时 `task.CurrentPath` 已改到入口、也不再有任何校验读那个目录）。
             */
            AdoptUnresolvedVolumePieces(result, task);

            if (result.StopReason == RecursionStopReason.NeedsDecision && result.Decision != null)
            {
                /*
                 * 递归核心在问"这些内层归档要不要也解开"（只有②页「单链自动展开」那一档会问）。
                 *
                 * ⛔ 2026-10-04（用户当场推翻旧口径）：一键档**不再**走"保守不展开、只留当前这一层"。
                 * 老写法是 `bool expandAll = !oneClickRun && await ShowConfirmOnUiThreadAsync(…)` ——
                 * 一键档不看用户在②页选了哪一档，永远只保留当前这一层，日志还写着"按保守档"：
                 * 一键处理因此成了比手动档**更弱**的档位（真机 `第6集.7z` 就是这么停的；
                 * 用户原话「就应该有压缩包就解压啊」）。
                 *
                 * 现在一键档与手动档的差别**只剩"问不问"**（红线：批中间零弹窗，见
                 * `SuppressDecisionPromptsForOneClickRun`），展开策略一律按**用户所选档位**：
                 * · `AllBranches`（出厂默认）：递归核心根本不会给出这个询问（它已经把每个分支都入队了），
                 *   下面那一支只是"哪天门开了"的兜底 —— 全展开，与用户选的那一档一致；
                 * · `SingleChain`：那一档的语义就是"一层里出现 **≥2 个**真归档就停"，
                 *   一键档没法问 ⇒ 与手动档里用户点「取消」同一结果：保留当前这一层 + 如实写日志。
                 */
                bool expandAll = oneClickRun
                    ? true
                    : await ShowConfirmOnUiThreadAsync(
                        result.Decision.Prompt + Environment.NewLine + Environment.NewLine +
                        "选“确定”：把这些内层归档也解开。选“取消”：只保留当前这一层的结果。");

                if (oneClickRun)
                {
                    /*
                     * ⚠ 2026-10-04（真机）：这一行过去写死"（多分支）… 按保守档" ——
                     * 措辞与档位两处都不准：
                     * ① 判据只看**内层归档的数量**（≥2 才停；1 个 + 旁边一个 `.mp4` 会被"这是压缩包吗，
                     *    不是那你停什么"顶回来），所以数量要按真实的说；
                     * ② 停下来的理由不是"一键档保守"，而是**用户在②页选的那一档**如此。
                     */
                    int candidateCount = result.Decision.CandidateArchives.Count;

                    AppendLog(
                        "WARN",
                        candidateCount > 1
                            ? string.Format(
                                CultureInfo.CurrentCulture,
                                StatusText.RecursionBranchHeldBackMultipleFormat,
                                task.FileName,
                                candidateCount,
                                RecursiveExtractor.DescribeUnexpandedNames(result.Decision.CandidateArchives))
                            : string.Format(
                                CultureInfo.CurrentCulture,
                                StatusText.RecursionBranchHeldBackSingleFormat,
                                task.FileName));
                }

                if (expandAll)
                {
                    // 同一个实例接着跑（上限不变）：续跑用的是用户刚确认的那批候选，不是重新扫一遍。
                    result = await recursiveExtractor.ExtractAsync(
                        recursionTask,
                        stageDirectory,
                        result.Decision,
                        cancellationToken,
                        recursionProgressSink,
                        notice => _progressHeartbeat.HandleStalled(task, notice));
                }
                else
                {
                    /*
                     * 用户只想保留当前这一层。
                     *
                     * 这里**不能**就这样返回 NeedsDecision：那会让用户以为"东西已经解到输出目录了"，
                     * 而按不变量 12，需要决定时产物还留在工作区里 —— 文案与实现不符等于骗人。
                     *
                     * 做法：把已经解好的第 0 层产物从递归工作区搬进**暂存目录**（它照样是入仓阶段的一部分），
                     * 并把它当成 Completed 继续走"校验 → 定稿 → 归集 → 清理"。
                     * 不重新解压一遍：大包重解代价太大，而产物本来就在工作区里。
                     */
                    string? layerZeroOutput = result.Layers
                        .FirstOrDefault(l => l.Depth == 0 && l.Success)?.OutputPath;

                    if (!string.IsNullOrWhiteSpace(layerZeroOutput) && Directory.Exists(layerZeroOutput))
                    {
                        /*
                         * 搬运是同步磁盘活（递归枚举 + File.Move），同样必须离开 UI 线程 ——
                         * 理由与收尾那一段完全相同（见 PostProcessSuccessAsync 的线程规则）。
                         */
                        int moved = await Task.Run(
                            () => MoveDirectoryContent(layerZeroOutput, stageDirectory),
                            cancellationToken);

                        result = new RecursionResult
                        {
                            StopReason = RecursionStopReason.Completed,
                            Completed = true,
                            PartiallyCompleted = false,
                            Layers = result.Layers,
                            FinalOutputPath = stageDirectory,
                            Summary = $"按你的选择只解开了当前这一层，已取回 {moved} 个文件（定稿后进输出目录）。"
                        };

                        /*
                         * 产物已经取回暂存目录 → 那份递归工作区（含 report.json）从此是纯垃圾，
                         * 留着还会在下次启动时被算进"未完成的工作区"报告。
                         *
                         * 为什么得在这里补一刀：递归核心是以 NeedsDecision 收的尾，按规则**没有**清工作区
                         * （"等你决定"属于部分完成）；结论是在**这里**被改写成 Completed 的 ——
                         * 谁改写结论，谁负责把"这次算成功"的收尾补上（递归成功那一支的清理在
                         * RecursiveExtractor.FinalizeRun 里，走的是同一个 Cleanup()）。
                         *
                         * ⚠ 只有"一个文件都没漏下"才清：MoveDirectoryContent 是尽力而为
                         * （单个文件搬不动就跳过、只记进返回的文件数），漏下的那些还在工作区里，
                         * 删掉它们等于丢用户的产物（不变量 1 的精神）。
                         */
                        if (HasNoFiles(layerZeroOutput))
                        {
                            recursiveExtractor.TryCleanupCurrentWorkspace(
                                task.FileName,
                                "用户选择只保留当前这一层，第 0 层产物已取回暂存目录");
                        }
                    }
                    else
                    {
                        // 第 0 层本身就没成功：保持原结论（部分完成），让它如实报告。
                        AppendLog("WARN", $"{task.LogName}：当前这一层没有可用产物，保留原结论。");
                    }
                }
            }

            ApplyRecursionResult(task, result);

            /*
             * ===== 停因是「分卷缺失」时，把话说对（用户 2026-10-06 口径）=====
             *
             * 用户原话：「111.rar 是「部分完成」…… **这个根本就不是部分完成，对于 111.rar 这个就是成功了**，
             * 只不过这个内容物又是下一轮解压需要的东西」「没有完整的**不是缺卷**，因为你现在还没有将所有的
             * 东西统统解压完，还没有到最后 … 你就**只能显示跳过**」。
             *
             * ⛔ 这里**只改措辞**，⛔ 一个字都不动机器终态与状态常量：`task.Status` 是"装箱单"
             * （`PostProcessSuccessAsync` 那一整条发布链 / 定稿 / 源包处理都读它），在这里改它
             * 会连带改掉"已解出来的东西要不要落地"（实测踩到过一次，整组连入口都不再落地）。
             * 终态那一档的改口径要单独拍板（见交接说明里的"未完成"）。
             */
            if (result.StopReason == RecursionStopReason.MissingVolume)
            {
                bool deferredHere = IsDeferredVolumeDeficit(task);

                /*
                 * ⚠ 2026-10-08（用户口径「没有到最后一步都是先跳过」）：递归**中途**撞上缺卷这一档，
                 * 批中间不许把「分卷缺失」当结论摆出来 —— 缺的那几片完全可能被同批别的包补上
                 * （真机 `111.z01` 就是这么补上的）。这里只记一个"待批末判"的**显示/统计事实位**：
                 * ⛔ `Status` / `Outcome` 一个字不动（它们是"装箱单"，发布链 / 定稿 / 源包处理都读，
                 * 见上面那段说明），⛔ 也不改判据。批末定稿时清掉它，那一格随即回到真结论。
                 */
                if (!_volumeDeficitFinalPass.Contains(task))
                {
                    // 显示/统计那一格照旧点亮（批中间不许顶着红色「分卷缺失」）—— 与改动前逐字相同。
                    task.IsVolumeDeficitDeferred = true;

                    /*
                     * ⚠ 2026-10-10 **修**：老写法**只点亮显示位、不进名单** ⇒ 批末那一站
                     * （<see cref="RecheckDeferredVolumeDeficits"/>）读那份名单时是**空的**，
                     * 于是一个字都不做 ⇒ 「等批末再判」这句话对**中途**撞上缺卷的单是句空话。
                     *
                     * <para>真机实测（EEEE，2026-10-10）：`111.rar` 停在第 3 层（要开的正是分卷组 `111`
                     * 的入口 `111.zip`，入口已由它自己解出来、散着的片还差 `.z03`），界面上也写着
                     * 「跳过（缺卷，等批末再判）」，而批末名单 **0 单** ⇒ 收片那一档
                     * （`TryGatherGroupPiecesInto`）从没跑过 ⇒ `111(4)\111.z03` 那一片永远接不进这一组
                     * （走的是"目标层算不出来 ⇒ 静默什么都不做"那条支）⇒ 整组永远不解、一次引擎调用都不做
                     * —— 这就是"第一大步不闭环"。</para>
                     *
                     * <para>⛔ 进名单**只进"停下来的那一组就是我这一组"的那一单**
                     * （<see cref="IsStalledGroupOwnGroup"/>）：批末那一站是拿**这一单自己的包基名**
                     * 去问"这一组齐了没"的，名字对不上的单进去只会被扣一句不相干的「分卷缺失」。</para>
                     */
                    if (IsStalledGroupOwnGroup(task, result))
                    {
                        RememberDeferredVolumeDeficit(task);
                    }
                    else if (FindStalledVolumePiece(result) is { Length: > 0 } stalledPiece
                             && !AnyTaskRepresentsGroup(stalledPiece))
                    {
                        /*
                         * ⚠ 2026-10-13 困难批 `11111111` 新开的一格：停下来的**是别人那一组**的片。
                         *
                         * <para>现场（运行期实测）：三片 `222-L2.part1/2/3.rar` 各自被 `222-W0-1.zip` /
                         * `222-W1-1.7z` / `222-W2-0.zip` 三条链解出来，每一条链都停在"这一组的入口包不在旁边"
                         * （`Missing volume`）；而这三单的包基名（`222-W0-1` 等）与那一组（`222-L2`）
                         * **族与基名都对不上** ⇒ 老闸门一单都不登记 ⇒ 批末名单 0 条
                         * ⇒ 那一组永远没人收拢、也永远不解开 ⇒ ①红。</para>
                         *
                         * <para>⛔ 安全性没放宽：登记的仍然是**这一单真的产出过的那一片**（`UnresolvedVolumePieces`
                         * 里自报是分卷片的那一个，唯一出口 `VolumeGroupDetector.TryGetVolumeIndex`），
                         * 批末只是拿**它**去问对的那个组；仍然"补齐才跑、仍然缺就如实报缺卷"。</para>
                         */
                        /*
                         * ⚠ 2026-10-14（插桩实测 `10101010`）：登记时**必须把"参照名"当场定下来** ——
                         * 这一片随后会随它那条链的工作区被清掉，到批末再读它的卡已经读不出来
                         * （逐字：批末那一行仍写 `引用名=「pack111-L2.7z.zip」` ⇒ 一个成员都匹配不上
                         * ⇒ 收拢收不到任何一片）。⇒ 现在（文件还在）就用**卡**算出它的规范名当参照：
                         * `pack111-L2.7z.txt`（卡说 `VolumeFirstPiece`）⇒ `pack111-L2.7z.001` ✓。
                         * ⛔ 判不出（卡读不出 / 名字也推不出）⇒ 照旧用原来那个路径（老口径不放宽）。
                         */
                        string reference = VolumePieceFacts.ResolveCanonicalName(
                            stalledPiece,
                            FileNature.Inspect(stalledPiece));

                        RememberDeferredVolumeDeficit(task, reference.Length > 0 ? reference : stalledPiece);
                    }
                    else
                    {
                        /*
                         * ⚠ 2026-10-16（困难批 `21212121`）：**这一档原来一个字都不写** —— 批末名单里没有它、
                         * 日志里也看不出为什么 ⇒ 只能读码猜。缺卷的判据输入必须留痕（同"收卷判据输入"那一档）。
                         * ⛔ 只写事实，⛔ 不加判据、⛔ 不改任何行为。
                         */
                        AppendLog(
                            "WARN",
                            $"{task.LogName}：缺卷登记判据输入：停下的那一组是不是自己这一组={IsStalledGroupOwnGroup(task, result)}｜"
                            + $"这一趟自报的分卷片 {result.UnresolvedVolumePieces.Count} 个｜"
                            + $"停下的那一片里的参照名=「{(FindStalledVolumePiece(result) is { Length: > 0 } stalledForLog ? FileNameHelper.GetFileName(stalledForLog) : "（空）")}」｜"
                            + $"任务表里已有别单代表这一组={(FindStalledVolumePiece(result) is { Length: > 0 } represented ? AnyTaskRepresentsGroup(represented) : false)}｜"
                            + "⇒ 不进批末名单（批末那一站不会为这一组补判）");
                    }
                }

                AppendLog(
                    "INFO",
                    deferredHere
                        ? $"{task.LogName}：这一单该解的都解出来了，停下的那一层要开的是这一组的分卷片，"
                          + "缺的是同一组别的片 —— 这一组先记着（跳过），等这一批的解压都跑完再判一次（⛔ 不是失败）。"
                        : $"{task.LogName}：这一单该解的都解出来了；停下的那一层要开的是这一组的分卷片，"
                          + "而这一组到最后一刻仍然没凑齐（缺哪几片见下面的缺卷结论）。");
            }

            /*
             * 部分完成发布（用户 2026-10-02）：递归这条路上"解了一半的现场"是**最深那一层失败时**
             * 留在它自己产物目录里的东西。登记在这里、真发布在任务收尾（清工作区之前）——
             * 那之前还有链尾的定稿与清理，现场随时可能变。
             */
            RecordRecursivePartialPublishCandidate(task, result);

            /*
             * 「最后一层过程物」按③页「删除操作」那一档处置（用户 2026-10-14 口径 §2.1/§2.2）——
             * **必须在这一刻做**：它的默认档动作是"把那一份收进其余物"，而这一单的工作区随后
             * （失败 / 部分完成收尾）会被整份清掉 ⇒ 晚一步就再也没有那一份了。
             */
            PreserveStoppedLayerProcessArtifact(task, result);

            return result;
        }

    }
}