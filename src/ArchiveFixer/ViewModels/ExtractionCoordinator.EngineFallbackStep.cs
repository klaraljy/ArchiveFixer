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
    /// <summary>引擎回退与暂存校验（第 12 块，同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// 结论里点名"是哪个引擎报的密码错"。
        /// 优先取**引擎自己盖的戳**（<see cref="ArchiveOperationResult.EngineDisplayName"/>，不变量 14），
        /// 取不到才退回注入的那个引擎实例的显示名。
        /// </summary>
        private string ResolveFailedEngineName(ArchiveOperationResult? lastResult)
        {
            if (!string.IsNullOrWhiteSpace(lastResult?.EngineDisplayName))
            {
                return lastResult!.EngineDisplayName;
            }

            return string.IsNullOrWhiteSpace(_archiveEngine.DisplayName)
                ? EngineIds.SevenZip
                : _archiveEngine.DisplayName;
        }

        /// <summary>
        /// 兜底命中之后的收尾：**与候选循环里成功那一支逐字同一条路**
        /// （<see cref="VerifyStageProductsAsync"/> → <see cref="PostProcessSuccessAsync"/>）。
        ///
        /// <para>⛔ 这里不许另造一套"成功"的口径：产物此刻已经落在**同一个暂存目录**里
        /// （WinRAR 直接写进去的），所以校验、定稿、发布、源包处理全部照旧。</para>
        ///
        /// <para>⚠ 与循环里那一支的唯一差别：清单**没有**跟着 WinRAR 的密码重列一遍 ——
        /// 列目录本来就是与密码无关的一步（能列出来的包，任何候选都列得出来；
        /// <c>-mhe</c> 那种列不出来的包，我们手上那份就是 <c>Unavailable</c>）。
        /// 判"可证完整"的那一步因此照旧落在"判不出 ⇒ 什么都不做"上（不变量 12 的收尾口径一个字没放宽）。</para>
        ///
        /// <para><b>⛔ 顺序：先校验，再说话</b>（2026-10-05）。兜底那一句"命中"只说明
        /// <b>WinRAR 退出码 0 且产物目录里多出了东西</b>（见 <see cref="PasswordEngineFallback.TryAsync"/>），
        /// **不说明那些东西就是这一层的产物**。与候选循环里"引擎说成功也得先过校验"那一支逐字同一处置：
        /// 校验判否 ⇒ **这个候选不算数** —— 不落「解压成功」、不定稿发布、不改结论，
        /// 现场（这一趟写下的东西）原样留着交给调用方那段失败处理（③页「工作区残留」那一档看的就是它）。</para>
        /// </summary>
        /// <returns>true = 校验通过、已经按成功收尾；false = 这个候选不算数（调用方必须保持"没成功"）。</returns>
        private async Task<bool> FinishFallbackSuccessAsync(
            ArchiveTask task,
            string engineArchivePath,
            string password,
            string engineOutputPath,
            string outputRedirectNote,
            bool sharedOutputRoot,
            bool oneClickRun,
            CancellationToken cancellationToken)
        {
            /*
             * 先校验（⚠ 这里**还不许**动 task.Status / PasswordStatus / ErrorMessage）：
             * 判否时那些字段必须保持调用方原来那条密码类结论，一个字都不许被"成功"盖掉。
             */
            StageProductVerification stage = await VerifyStageProductsAsync(
                    task,
                    engineArchivePath,
                    password,
                    engineOutputPath,
                    cancellationToken)
                .ConfigureAwait(true);

            if (!stage.Verification.Verified)
            {
                /*
                 * 这个候选不算数（⛔ 不发布、⛔ 不翻结论）。实测会走到这里的形状：
                 * 换引擎兜底"解开"的东西与这一层的清单对不上（数目 / 字节对不上，或者干脆是空的）。
                 * 现场保留：这是最后一次尝试，不该再清一次盘（那是循环里"换下一个候选"那一支的事）。
                 */
                AppendLog(
                    "WARN",
                    $"{task.LogName}：换引擎兜底解出来的产物没有通过结果校验 —— {stage.Verification.Message}"
                    + "（这个候选不算数：不发布、结论照旧）。");

                return false;
            }

            task.Status = StatusText.ExtractSuccess;
            task.PasswordStatus = string.IsNullOrEmpty(password)
                ? StatusText.PasswordNotNeeded
                : StatusText.PasswordCorrect;
            task.ErrorMessage = string.Empty;

            // 与循环里同一处：密码成功记录挂源文件，不挂工作区里的临时文件。
            _passwordService.RecordPasswordSuccess(task.CurrentPath, password);

            bool conclusionStands = await PostProcessSuccessAsync(
                    task,
                    engineArchivePath,
                    password,
                    engineOutputPath,
                    outputRedirectNote,
                    sharedOutputRoot,
                    oneClickRun,
                    cancellationToken,
                    stage.List)
                .ConfigureAwait(true);

            if (conclusionStands && task.Status == StatusText.ExtractSuccess)
            {
                AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
            }

            return true;
        }

        /// <summary>
        /// 一个候选密码解出来的**产物校验**（进入定稿之前的那一道门）。
        ///
        /// <para>
        /// 用户 2026-09-24 的真机铁证（本案的核心修复）：7z 用候选 2 解出来 **0 字节**的
        /// <c>*.7z.001/.002</c>、校验当场判否，程序却①落「解压成功」②把 0 字节文件当内层包继续解
        /// ③把它们搬进 <c>其余物</c> ④**候选循环就此收场**（候选 3–10 一个都没试）。
        /// </para>
        /// <para>
        /// 所以校验必须在**候选循环内部**做，而且结论只有两种用法：
        /// <list type="bullet">
        /// <item><description>通过 → 才进定稿 / 归集 / 源包处理（<see cref="PostProcessSuccessAsync"/>）；</description></item>
        /// <item><description>判否 → **这个候选不算数**：清掉它的垃圾产物、继续试下一个候选。</description></item>
        /// </list>
        /// 清单（<see cref="ArchiveListResult"/>）跟着一起回传：定稿那一步要用**同一份**清单再校验一次，
        /// 不能让两条路各列一次目录各说各话（重复列目录还会成倍放大加密包的失败概率）。
        /// </para>
        ///
        /// <para><b>只允许在后台线程上跑</b>：列目录是引擎进程调用，量产物是整棵目录树的磁盘活。</para>
        /// </summary>
        private async Task<StageProductVerification> VerifyStageProductsAsync(
            ArchiveTask task,
            string engineArchivePath,
            string password,
            string stageDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ArchiveListResult list = await _archiveEngine.ListAsync(
                ArchiveRequest.For(engineArchivePath, password),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            /*
             * ⚠ 2026-09-30：与 RunPostProcessWork 同一口径 —— **不把"取不到清单"折成 null**
             * （null 会把"为什么取不到"一起丢掉；Verify 内部对 null 与 Success=false 的处理逐字相同）。
             */
            OutputVerificationResult verification = await Task.Run(
                () => OutputVerifier.Verify(stageDirectory, list),
                cancellationToken);

            return new StageProductVerification(list, verification);
        }

        /// <summary>
        /// 把上一轮候选留在暂存目录里的产物清干净，让下一个候选从零开始。
        ///
        /// <para>
        /// 为什么必须清：不清的话下一轮的校验会把**上一个候选的残留**一起算进去
        /// （0 字节桩 + 这一轮的产物 → 数字对不上，判出来的结论是错的），
        /// 而定稿也可能把两份候选的东西一起搬进最终目录。
        /// </para>
        /// <para>
        /// 清不掉时只写一行 WARN，**不打断候选循环**：最坏的结果是下一个候选校验不过，
        /// 那也会被如实报出来；为了"清残留失败"把整单判死只会让用户更难办。
        /// </para>
        /// </summary>
        private async Task DiscardStageProductsAsync(
            ArchiveTask task,
            string stageDirectory,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Run(
                    () =>
                    {
                        if (Directory.Exists(stageDirectory))
                        {
                            Directory.Delete(stageDirectory, recursive: true);
                        }

                        Directory.CreateDirectory(stageDirectory);
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"{task.LogName}：清理候选产物失败（{ex.Message}），继续尝试下一个候选。");
            }
        }

        /// <summary>
        /// 准备本任务的暂存目录（入仓的第一步）：建出来，并把**上一次留下的残留**清掉。
        ///
        /// 为什么要清残留：暂存目录的 taskId 带源路径哈希，所以同一个包重试时落回同一个目录。
        /// 上一次失败/取消留下的产物如果不清，这一次的定稿会把它们一起当成"本次产物"搬进最终目录 ——
        /// 用户会看到上一轮的垃圾文件混进结果里（比"多几个文件夹"更难查）。
        ///
        /// 为什么必须在后台线程：<see cref="Directory.Delete(string, bool)"/> 删的是几十万个文件的树时
        /// 是秒级到分钟级的同步磁盘活，放在 UI 线程上就是"点了没反应"。
        ///
        /// 返回 null = 准备好了；返回非空字符串 = 失败原因（调用方落成失败状态并把原因写给用户）。
        /// </summary>
        private async Task<string?> PrepareStageDirectoryAsync(
            ArchiveTask task,
            string stageDirectory,
            CancellationToken cancellationToken)
        {
            try
            {
                return await Task.Run(
                    () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (Directory.Exists(stageDirectory))
                        {
                            bool hasLeftover = false;

                            try
                            {
                                hasLeftover = Directory.EnumerateFileSystemEntries(stageDirectory).Any();
                            }
                            catch
                            {
                                hasLeftover = true;
                            }

                            if (hasLeftover)
                            {
                                // 上一轮的残留：先清掉再解。清不掉就如实失败，绝不"接着往里解"。
                                Directory.Delete(stageDirectory, recursive: true);
                            }
                        }

                        return SafePathHelper.EnsureDirectoryExists(stageDirectory)
                            ? null
                            : $"暂存目录不存在且创建失败：{stageDirectory}";
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"准备暂存目录失败（{ex.Message}）：{stageDirectory}";
            }
        }

        /// <summary>
        /// 直读路线开跑之前，把**上一次回落**留下的抠取副本清掉。
        ///
        /// <para><b>为什么必须有这一条</b>：抠取副本的落点是
        /// <c>&lt;work&gt;\&lt;taskId&gt;\&lt;包基名&gt;.zip</c>，而 taskId 只取决于**源文件全路径** ——
        /// 所以"上一次走到回落、抠出来了一份，这一次直读成功"时，那份**等大**的旧副本还留在工作区里。
        /// 定稿那一步会把它当成本任务的其余物搬进用户目录，等于把这次省下来的空间又还回去了
        /// （用户要的正是"别再有这份副本"）。</para>
        ///
        /// <para>它是本任务**私有工作区**里的派生过程物（不是源文件，也不是别人的东西），
        /// 而本次已经确定不用它 —— 删掉是唯一不误导的做法。边界与
        /// <see cref="CleanupTaskWorkspaceDirectory"/> 同一口径：只在"确实在工作区根之下"时动手，
        /// 删不掉只记 WARN（绝不让一次清理失败影响解压结论）。</para>
        /// </summary>
        private void DiscardStaleCarvedArtifact(ArchiveTask task)
        {
            /*
             * 候选名字 = 任务上记着的那一个 + 内容的三种标准后缀各一个
             * （`AlignCarvedArchiveExtension` 只可能把名字改成 `.7z` / `.rar` / `.zip` 之一）。
             *
             * 为什么要都试：这一份是**上一次进程**留下的，那时任务上记的还不是这次的结论；
             * 只按 `.zip` 找 ⇒ 上一次被按内容改名成 `.rar` 的那一份永远清不掉，
             * 定稿时会被当成本任务的其余物搬进用户目录（正是这个方法要防的那件事）。
             */
            var candidates = new List<string> { BuildEmbeddedArchivePath(task) };

            foreach (string extension in new[] { "7z", "rar", "zip" })
            {
                string candidate = Path.ChangeExtension(candidates[0], extension);

                if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(candidate);
                }
            }

            try
            {
                foreach (string carvedPath in candidates)
                {
                    if (string.IsNullOrWhiteSpace(carvedPath) || !File.Exists(carvedPath))
                    {
                        continue;
                    }

                    if (!ArchivePathGuard.IsInsideRoot(_pathService.WorkDirectory, carvedPath, out string reason))
                    {
                        AppendLog("WARN", $"{task.LogName}：上次留下的抠取副本不在工作区根之下，已跳过清理 —— {reason}");
                        continue;
                    }

                    File.Delete(carvedPath);

                    AppendLog(
                        "INFO",
                        $"{task.LogName}：已清掉上一次回落留下的抠取副本（本次走直读，不需要它）：{Path.GetFileName(carvedPath)}");
                }
            }
            catch (Exception ex)
            {
                AppendLog(
                    "WARN",
                    $"{task.LogName}：上次留下的抠取副本没清掉（{ex.Message}），它会在定稿时被当成其余物搬走。");
            }
        }


        /// <summary>一个候选的产物校验结论（清单 + 校验，见 <see cref="VerifyStageProductsAsync"/>）。</summary>
        private sealed class StageProductVerification
        {
            public StageProductVerification(ArchiveListResult list, OutputVerificationResult verification)
            {
                List = list;
                Verification = verification;
            }

            /// <summary>这一轮列目录拿到的清单（定稿那一步复用，避免再列一次）。</summary>
            public ArchiveListResult List { get; }

            /// <summary>产物校验结论（判否时 <c>FailureMessage</c> 里带着预期与实际的三个数字）。</summary>
            public OutputVerificationResult Verification { get; }
        }


        /// <summary>定稿搬运的一句话结论（给日志与任务字段用）。</summary>
        internal sealed class StageCommitResult
        {
            public bool Attempted { get; init; }

            /// <summary>搬运过程中被取消（已把搬过去的搬回来，最终目录不留半成品）。</summary>
            public bool Cancelled { get; init; }

            /// <summary>计划里的内容物搬运条数（一条可能是一整个目录）。</summary>
            public int PlannedContentCount { get; init; }

            /// <summary>真的搬过去的内容物条数。</summary>
            public int MovedContentCount { get; init; }

            /// <summary>真的搬过去的其余物条数。</summary>
            public int MovedProcessCount { get; init; }

            /// <summary>
            /// 因为同名冲突被**跳过**的条数（用户选了「跳过」/「跳过全部」）。
            ///
            /// 与"没能搬运"分开记：跳过是用户的选择，不是失败；但"一条内容物都没落位"时
            /// 必须靠它把任务落成「已跳过」而不是「解压成功」（不变量 6 的反面同样成立）。
            /// </summary>
            public int SkippedCount { get; init; }

            /// <summary>其中被跳过的是内容物的条数。</summary>
            public int SkippedContentCount { get; init; }

            /// <summary>因为用户选了「覆盖」而被顶掉的同名条目数。</summary>
            public int OverwrittenCount { get; init; }

            /// <summary>被顶掉的落点（写日志用：**哪些文件被覆盖**必须留痕）。</summary>
            public IReadOnlyList<string> OverwrittenPaths { get; init; } = Array.Empty<string>();

            /// <summary>内容物文件数（来自定稿计划，不是"搬了几条"）。</summary>
            public int ContentFileCount { get; init; }

            /// <summary>其余物目录（定稿计划算出来的那个）。源包搬运要用它当目标根（决策 D-10/D-12）。</summary>
            public string ProcessArtifactDirectory { get; init; } = string.Empty;

            /// <summary>
            /// 内容物实际落地的那一层目录（＝定稿计划的 <c>DestinationDirectory</c>）。
            ///
            /// <para>第 35 条：续解产物必须落进**父任务内容物所在的那一层**，而不是父任务的 OutputPath ——
            /// 共用输出根模式下两者不是一个目录（前者是 <c>BBB\111\2222\</c>，后者是 <c>BBB\111</c>）。</para>
            /// </summary>
            public string ContentDirectory { get; init; } = string.Empty;

            public long ProcessArtifactBytes { get; init; }

            public int RenamedCount { get; init; }

            public int FailedCount { get; init; }

            public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();

            public string Message { get; init; } = string.Empty;

            /// <summary>要带回 UI 线程写日志的行（后台线程不许直接写界面集合）。</summary>
            public List<(string Level, string Message)> LogEntries { get; init; } = new();
        }

    }
}