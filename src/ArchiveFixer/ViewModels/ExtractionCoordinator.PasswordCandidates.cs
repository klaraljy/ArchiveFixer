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
    /// <summary>落点/层计算编排（第 4 块，2026-10-23 从主分片搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// 给递归层提供密码候选（只给值，不给来源说明）。
        /// 顺序与单层解压完全一致 —— 递归的内层包同样是"用户的包"，不该用另一套规则。
        ///
        /// <para>⚠ 2026-10-05（只读审计）这一处改了两件事，两件都关系到"用户刚输的密码到底试没试"：</para>
        /// <list type="number">
        /// <item><b>本批手动输入的密码也要进来</b>：它过去只插进单层路径的候选表
        /// （<c>_manualBatchPasswords</c> 的唯一使用点），递归这条路拿不到 ⇒ 用户刚输对密码、
        /// 内层包照样报「密码错误」。做法是**转调同一个** <see cref="InsertManualPasswordCandidates"/>
        /// （插在空密码之后 + 去重，⛔ 不抄第二份判据）。</item>
        /// <item><b>这里不再截断</b>：老写法 <c>.Take(MaxPasswordAttemptsPerLayer)</c> 在递归内部
        /// "跳过空密码"那一步**之前**执行，而跳空密码只减账不减表 ⇒ 真实候选最多只试到 9 个
        /// （每层上限 10）。截断改由各调用方自己按同一个上限做：递归核心的层循环本来就有
        /// <c>attempts &gt;= MaxPasswordAttemptsPerLayer</c> 这一道，而"分卷试开"那三条调用点
        /// 必须自己截（见 <see cref="BuildVolumeProbePasswordCandidates"/>）——
        /// 否则 <c>VolumeProbeVerifier</c> 会对"每种排列 × 整份候选表"跑一遍试开，工作量放大几十倍。</item>
        /// </list>
        /// </summary>
        /// <param name="sourceSink">
        /// 可空：算完之后把这一层的「候选值 → 来源」写进去（先清空再写）。
        /// 递归层的日志只能靠它说清来源（见 <see cref="ResolveCandidateSourceForLog"/>）——
        /// 「本批已成功 / 密码本命中 / 说明文件旁路 / 手动输入」这些按值回查密码列表是答不出来的。
        /// </param>
        internal IReadOnlyList<string> BuildRecursionPasswordCandidates(
            string archivePath,
            IDictionary<string, string>? sourceSink = null)
        {
            var probe = new ArchiveTask(archivePath);

            List<PasswordItem> candidates = _passwordService.GetPasswordCandidates(
                probe,
                Settings.UseGlobalPasswordForAllTasks ? GlobalPassword : string.Empty,
                _passwordService.Passwords,
                Settings.TryEmptyPasswordFirst,
                Settings.EnableSidecarPassword);

            InsertManualPasswordCandidates(candidates, _manualBatchPasswords);

            /*
             * 顺手记一份「值 → 来源」：来源本来就由 PasswordService 给（它的候选表里带着 Source），
             * 这里只是**搬一次**，⛔ 不重新推、也不猜。每次算候选都整份重建 ——
             * 上一层（或上一个包）的来源不许留到这一层来（旁路说明文件就是按归档路径算的）。
             */
            if (sourceSink != null)
            {
                sourceSink.Clear();

                foreach (PasswordItem item in candidates)
                {
                    sourceSink[item.Value ?? string.Empty] = item.Source ?? string.Empty;
                }
            }

            return candidates
                .Select(p => p.Value ?? string.Empty)
                .ToList();
        }

        /// <summary>
        /// 「分卷试开」（<c>VolumeNameRepair.PlanByContentAsync</c> 系列）用的候选表：
        /// **同一个 provider，但在这里截到每层上限**。
        ///
        /// <para>为什么要单独一个出口：<see cref="BuildRecursionPasswordCandidates"/> 现在给的是
        /// **不截断**的完整候选表（递归核心内部自己按上限停手），而试开那一步的对账方式是
        /// "每种卷序 × 每个候选都真跑一次 <c>TestAsync</c>" —— 几百个候选进去就是几十倍的无谓引擎调用。
        /// 三条调用点（同目录 / 无名那两档 + 跨目录放宽那一档）全部走这一个方法，
        /// ⛔ 不许各自 <c>.Take</c> 一遍（那正是"同一件事两个出口"的起点）。</para>
        /// </summary>
        internal IReadOnlyList<string> BuildVolumeProbePasswordCandidates(string archivePath) =>
            BuildRecursionPasswordCandidates(archivePath)
                .Take(MaxPasswordAttemptsPerLayer)
                .ToList();

        /// <summary>
        /// 手动密码的候选位置：插在**空密码之后**（<paramref name="insertAfterEmpty"/> 为 true 时），
        /// 否则追加到末尾。
        ///
        /// <para>
        /// 为什么不是最前：空密码是"无密码包"的必经一步，而且它是零成本的一次尝试（不进密码循环也不亏）。
        /// 为什么排在密码本之前：手动输入是用户**刚刚**给出的信息，比密码本里的历史条目更"新"；
        /// 而且它通常就是那个包真正需要的密码，早点试能省掉一大圈无谓试错。
        /// </para>
        /// <para>
        /// 传 null / 空串时什么都不做（空密码已经由调用方自己保证了）。
        /// </para>
        /// </summary>
        private static void InsertManualPasswordCandidates(
            List<PasswordItem> candidates,
            IReadOnlyList<string>? manualPasswords,
            string remark = "本次运行手动输入（不落盘）")
        {
            if (candidates == null || manualPasswords == null || manualPasswords.Count == 0)
            {
                return;
            }

            var additions = new List<PasswordItem>();

            foreach (string manual in manualPasswords)
            {
                if (string.IsNullOrEmpty(manual))
                {
                    continue;
                }

                if (candidates.Any(item => string.Equals(item?.Value, manual, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (additions.Any(item => string.Equals(item.Value, manual, StringComparison.Ordinal)))
                {
                    continue;
                }

                additions.Add(new PasswordItem
                {
                    Value = manual,
                    Source = ManualPasswordSource,
                    IsEnabled = true,
                    Remark = remark
                });
            }

            if (additions.Count == 0)
            {
                return;
            }

            /*
             * 整批插在**空密码之后**：空密码是"无密码包"的必经一步、零成本；
             * 手动输入排在密码本之前 —— 它是用户刚刚给出的信息，比历史条目更"新"。
             * ⛔ 用 InsertRange 一次插完（逐条插会把先插的挤到后面，顺序就反了）。
             */
            int index = candidates.FindIndex(
                item => string.Equals(item?.Source, "Empty", StringComparison.Ordinal));

            if (index >= 0 && index < candidates.Count - 1)
            {
                candidates.InsertRange(index + 1, additions);
                return;
            }

            candidates.AddRange(additions);
        }

        /// <summary>
        /// 把"该不该换引擎再试一遍"要的事实凑齐（⛔ 判据本身不在这里，见
        /// <see cref="PasswordEngineFallback.ShouldFallBack"/>）。
        ///
        /// <para>每一条都只读**这一条路上已有的事实**（结构化错误类型、清单里的加密位、循环自己的计数），
        /// ⛔ 不比中文文案、⛔ 不新加计数器：</para>
        /// <list type="bullet">
        /// <item><description><b>密码类失败</b> = 最后一次引擎结果落在 <c>WrongPassword</c> /
        /// <c>NeedPassword</c> / <c>PasswordOrCorrupted</c>，**或**"引擎说成功、产物没通过校验"
        /// （那一次的结论同样是"所有候选都没解开"）；⛔ 排除"密码已证实、坏在数据上"
        /// （{@code passwordProvenDataFailed}）与"与密码无关"（{@code passwordIrrelevant}）两档 ——
        /// 它们的结论**不是**密码类，换引擎也不该改口。</description></item>
        /// <item><description><b>归档加密</b> = 识别阶段判过加密 / 预检清单里有加密条目 / 引擎报过加密头，
        /// 三条任一条成立（都读结构化事实）。</description></item>
        /// <item><description><b>候选跑完</b> = 循环把该试的都试了（{@code attemptedCandidates >= maxPasswordAttempts}，
        /// 且上限大于 0）—— ⛔ 不是"循环还在跑"、也⛔ 不是"失败就换引擎"。</description></item>
        /// </list>
        /// </summary>
        private PasswordFallbackTriggerFacts BuildPasswordFallbackFacts(
            bool extractSuccess,
            bool hasWrongPassword,
            bool sawAmbiguousPasswordFailure,
            bool passwordProvenDataFailed,
            bool passwordIrrelevant,
            bool stoppedByStopRequest,
            ArchiveOperationResult? lastResult,
            int attemptedCandidates,
            int maxPasswordAttempts,
            int candidateCount,
            bool archiveEncrypted,
            bool anyCandidateWroteBytes)
        {
            bool lastResultPasswordClass =
                lastResult != null &&
                (lastResult.IsWrongPassword || lastResult.IsNeedPassword || lastResult.IsPasswordOrCorrupted);

            /*
             * "引擎说成功、产物却没通过校验"那一档也算：候选循环跑完时它的结论同样是「密码错误」
             * （试过密码都不对 / 产物始终不完整），而**最后一次引擎结果**不是密码类错误 ——
             * 只看最后那次结果就会漏掉这一档。⛔ 但硬失败（退出码类错误）不算：那说明停因另有其人。
             */
            bool passwordConclusion =
                !passwordProvenDataFailed &&
                !passwordIrrelevant &&
                !stoppedByStopRequest &&
                (hasWrongPassword || sawAmbiguousPasswordFailure) &&
                (lastResultPasswordClass || lastResult is { Success: true });

            return new PasswordFallbackTriggerFacts
            {
                AlreadySucceeded = extractSuccess,
                PasswordClassFailure = passwordConclusion,

                // 候选循环跑完 = 该试的都试了（上限内）。⛔ 一条都没试（上限 0 / 没有候选）不算。
                CandidatesFinished = maxPasswordAttempts > 0 && attemptedCandidates >= maxPasswordAttempts,

                ArchiveEncrypted = archiveEncrypted,
                UserAskedToStop = stoppedByStopRequest || IsStopping,
                HasCandidates = candidateCount > 0,
                EngineAvailable = PasswordEngineFallback.IsEngineAvailable,

                /*
                 * 主引擎在这一层的候选上写出过非零字节 ⇒ **进得去这一份的数据流** ⇒ 不换引擎
                 * （WinRAR 用错密码时也会按原大小把乱码写进同一个目录，两个引擎给不出可分辨的证据）。
                 * 真机那一档（引擎吃不下这一份、一个字节都没出来）照旧允许换引擎。
                 * 详见 `PasswordFallbackTriggerFacts.MainEngineWroteBytes`。
                 */
                MainEngineWroteBytes = anyCandidateWroteBytes
            };
        }

        /// <summary>
        /// 兜底要试的那一批候选（**同一批**：候选循环刚才在这个上限内试过的那些，保序）。
        /// 值 + 描述器一起给出，⛔ 描述器仍然只有既有那一个（<c>PasswordService.BuildTryPasswordLogText</c>）。
        /// </summary>
        private FallbackCandidateSlice BuildFallbackCandidateSlice(List<PasswordItem> candidates, int maxPasswordAttempts)
        {
            int take = Math.Max(0, Math.Min(maxPasswordAttempts, candidates.Count));

            var values = new List<string>(take);

            for (int index = 0; index < take; index++)
            {
                values.Add(candidates[index].Value ?? string.Empty);
            }

            return new FallbackCandidateSlice(candidates, values, _passwordService);
        }

        /// <summary>
        /// 用"解密后的开头 64 字节"给候选密码**排序**（用户 2026-09-25 第 38 条）。
        ///
        /// <para>形状对得上的（看得出文件魔数 / 像文本）排前面；读不出来的（引擎不支持、超时）保持原位；
        /// 读出**随机字节**的排最后 —— 那基本就是"密码不对"，但**一个候选都不丢**：
        /// 万一那个密码其实是对的、而这个文件恰好没有魔数，它仍然会被试到（只是排在后面）。
        /// 检查的候选数封顶在"每层尝试上限"个（每个约 0.1–0.3 秒，十来个也就一两秒）。</para>
        /// </summary>
        private async Task<List<PasswordItem>> RankCandidatesByDecryptedPrefixAsync(
            ArchiveTask task,
            string archivePath,
            SevenZipEngine engine,
            string entryPath,
            List<PasswordItem> candidates,
            int maxPasswordAttempts,
            CancellationToken cancellationToken)
        {
            var plausible = new List<PasswordItem>();
            var unknown = new List<PasswordItem>();
            var implausible = new List<PasswordItem>();
            var uncheckedCandidates = new List<PasswordItem>();

            int limit = Math.Min(candidates.Count, Math.Max(1, maxPasswordAttempts));

            for (int i = 0; i < candidates.Count; i++)
            {
                if (i >= limit)
                {
                    uncheckedCandidates.Add(candidates[i]);
                    continue;
                }

                PasswordItem candidate = candidates[i];

                byte[]? prefix = await engine.TryReadDecryptedPrefixAsync(
                    archivePath,
                    entryPath,
                    candidate.Value ?? string.Empty,
                    PasswordProbe.PrefixProbeBytes,
                    cancellationToken);

                if (prefix == null)
                {
                    unknown.Add(candidate);
                }
                else if (PasswordProbe.LooksLikeFileStart(prefix))
                {
                    plausible.Add(candidate);
                }
                else
                {
                    implausible.Add(candidate);
                }
            }

            var ordered = new List<PasswordItem>(candidates.Count);
            ordered.AddRange(plausible);
            ordered.AddRange(unknown);
            ordered.AddRange(implausible);
            ordered.AddRange(uncheckedCandidates);

            AppendLog(
                "INFO",
                $"{task.LogName}：密码预检 —— 只读「{entryPath}」解密后的开头 {PasswordProbe.PrefixProbeBytes} 字节"
                + $"（与包多大无关，一个字节都不落盘）：看着像正常文件开头 {plausible.Count} 个"
                + $"（先试），说不准 {unknown.Count} 个，看不出是文件开头 {implausible.Count} 个（排到最后再试）。"
                + "⛔ 一个候选都不会被丢掉。");

            return ordered;
        }

        /// <summary>
        /// 用候选密码**只解一个条目**（清单里最小的那个），验证这个密码对不对（用户 2026-09-25 第 37 条）。
        ///
        /// <para>真机代价对比：他那个 12.22 GiB 的包（名字可见、条目加密）用空密码解整包花了 **10 分 18 秒**
        /// 并写出 12 GiB 垃圾，最后才报密码错误；本方法解一个 1 KiB 的说明文件，**毫秒级**就能否掉同一个候选。</para>
        ///
        /// <para>落点：探针产物写在**产物（暂存）目录外面的兄弟位置**，判据只有一处
        /// （<see cref="PasswordProbe.ResolveProbeDirectory"/>，递归那条路用同一个出口）——
        /// 产物目录里的东西要参与校验与发布，探针文件一个字节都不许混进去；
        /// 无论成败都在 finally 里删掉（不变量 12：绝不写工作区之外）。</para>
        ///
        /// <para>三种结论的边界刻意保守：**只有引擎明说"密码错误"才判 Rejected**；
        /// 别的错误（不支持单条目解、读不了、超时）一律 Inconclusive，退回整包试解 ——
        /// 预检是省时间的手段，⛔ 不许因为它的误判把一个对的密码判死。</para>
        /// </summary>
        private async Task<PasswordProbeOutcome> ProbePasswordAsync(
            string archivePath,
            ExtractOptions extractOptions,
            string stageDirectory,
            string entryPath,
            string password,
            TaskProgressSink progressSink,
            CancellationToken cancellationToken)
        {
            /*
             * ⚠ 2026-10-05：落点与递归那条路**统一成一个出口**（`PasswordProbe.ResolveProbeDirectory`）——
             * 以前单层路把它建在**暂存（产物）目录里面**，而暂存目录里的东西要参与结果校验与发布；
             * 万一 finally 那次删除失败（占用 / 权限），探针文件会被当成本层的产物一起数、一起搬。
             * 现在两条路都是"产物目录的**兄弟位置**"（还在本任务的工作区里，不变量 12 不变）。
             */
            string probeDirectory = PasswordProbe.ResolveProbeDirectory(stageDirectory);

            try
            {
                Directory.CreateDirectory(probeDirectory);
            }
            catch
            {
                // 建不出目录（权限 / 盘）：预检做不了，让调用方走老路。
                return PasswordProbeOutcome.Inconclusive;
            }

            IReadOnlyList<string> previousEntries = extractOptions.IncludeEntries;

            try
            {
                extractOptions.IncludeEntries = new[] { entryPath };

                ArchiveOperationResult result = await _archiveEngine.ExtractAsync(
                    BuildTrackedRequest(archivePath, password, probeDirectory, progressSink),
                    extractOptions,
                    cancellationToken);

                if (result.DetectedErrorType == "Cancelled" || result.Status == StatusText.Cancelled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (result.Success)
                {
                    return PasswordProbeOutcome.Verified;
                }

                if (result.DetectedErrorType == "WrongPassword" || result.Status == StatusText.WrongPassword)
                {
                    return PasswordProbeOutcome.Rejected;
                }

                return PasswordProbeOutcome.Inconclusive;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 预检本身出错不是"这个包失败"：退回整包试解，结论仍然由整包那一步说了算。
                return PasswordProbeOutcome.Inconclusive;
            }
            finally
            {
                extractOptions.IncludeEntries = previousEntries;

                try
                {
                    if (Directory.Exists(probeDirectory))
                    {
                        Directory.Delete(probeDirectory, recursive: true);
                    }
                }
                catch
                {
                    // 删不掉只影响暂存目录的整洁（那一份马上会被 DiscardStageProductsAsync 整份清掉）。
                }
            }
        }

        /// <summary>把"密码没通过"的任务登记到本批（只登记，不弹窗）。</summary>
        private void RecordPasswordFailure(ArchiveTask task)
        {
            if (task == null)
            {
                return;
            }

            /*
             * 三档（2026-10-01）：`PasswordOrCorrupted`（RAR 那句"文件已损坏或密码错误"）也算**密码类失败**。
             *
             * 为什么必须算：那一档恰恰是"最可能缺密码"的一档 —— 用户真机那批 RAR `-p` 包走的全是它，
             * 而原来它既不被登记、批末那条提示（"下一步去密码页导入 / 在确认框里填"）也就**一次都不出现**，
             * 用户只看到一行红字，不知道下一步该干什么。
             *
             * ⛔ 但**不许**因此把它说成确认的密码问题：引擎在那条路上本来就没法区分"密码错"与"数据坏"
             *（§11.4 的 RAR 两义句），批末文案里必须带着"可能"。
             */
            if (task.Status != StatusText.WrongPassword &&
                task.Status != StatusText.PasswordAttemptLimitReached &&
                task.Status != StatusText.PasswordOrCorrupted)
            {
                return;
            }

            // 本批出现过"密码类失败"这个事实：批次结束后据此在合并提示里带上"手动输入密码"的出口。
            _batchHadPasswordFailures = true;

            string fileName = string.IsNullOrWhiteSpace(task.FileName)
                ? Path.GetFileName(task.CurrentPath)
                : task.FileName;

            lock (_passwordFailuresLock)
            {
                _passwordFailures.Add((fileName, task.Status));
            }
        }

        private void ClearPasswordFailures()
        {
            lock (_passwordFailuresLock)
            {
                _passwordFailures.Clear();
            }
        }

        /// <summary>
        /// 批开始时把手动密码清干净：**只对本次运行有效**（不变量 5）。
        ///
        /// 不是"记住上次输的密码"—— 那是持久化，红线。上一批输过什么，这一批重新问。
        /// </summary>
        private void ResetManualBatchPassword()
        {
            _manualBatchPasswords.Clear();
            _manualPasswordPrompted = false;
            _batchHadPasswordFailures = false;
        }

        /// <summary>
        /// B 段：需要密码、但当前**一个可用候选都没有**的包（"即使密码本已经预加载"的那种）。
        ///
        /// <para>判据与真正解压时**完全同一套参数**（同一个 <c>GetPasswordCandidates</c>、同一个统一密码开关、
        /// 同一个旁路说明文件开关）：判据不一样就会出现"提醒说没有密码、结果它解开了"这种自相矛盾。</para>
        ///
        /// <para>⚠ 只判"有没有候选"，不判"到底加没加密" —— 后者是 <see cref="ArchiveTask.IsEncrypted"/>
        /// （识别阶段的结论）。没有密码的普通包不在这一段范围内。</para>
        /// </summary>
        private List<ArchiveTask> FindTasksWithoutUsablePassword(IReadOnlyList<ArchiveTask> tasks)
        {
            var result = new List<ArchiveTask>();

            foreach (ArchiveTask? task in tasks)
            {
                if (task == null || !(task.IsEncrypted ||
                    string.Equals(task.PasswordStatus, StatusText.PasswordNeed, StringComparison.Ordinal)))
                {
                    continue;
                }

                List<PasswordItem> candidates = _passwordService.GetPasswordCandidates(
                    task,
                    Settings.UseGlobalPasswordForAllTasks ? GlobalPassword : string.Empty,
                    _passwordService.Passwords,
                    Settings.TryEmptyPasswordFirst,
                    Settings.EnableSidecarPassword);

                if (!SourceJunkScanner.HasUsablePasswordCandidate(candidates))
                {
                    result.Add(task);
                }
            }

            return result;
        }

        /// <summary>
        /// 「看起来要密码」**并且**用与解压同一套参数算下来**一个可用候选都没有**的那些包
        /// —— 手动密码弹窗只在这些包存在时才问（2026-09-29 用户提问后收紧）。
        ///
        /// <para>⚠ 与 <c>FindTasksWithoutUsablePassword</c>（确认框那一段）的区别只有一个：
        /// 这里把"引擎已经说过需要密码"（<see cref="StatusText.PasswordNeed"/>）也算嫌疑人 ——
        /// RAR 这类容器的加密在识别阶段读不出来，只能等引擎列目录时才知道。</para>
        /// </summary>
        private List<ArchiveTask> FindSuspectsWithoutUsablePassword(IReadOnlyList<ArchiveTask> tasks)
        {
            var result = new List<ArchiveTask>();

            foreach (ArchiveTask? task in tasks)
            {
                if (task == null)
                {
                    continue;
                }

                bool suspect = task.IsEncrypted ||
                    string.Equals(task.PasswordStatus, StatusText.PasswordNeed, StringComparison.Ordinal);

                if (!suspect)
                {
                    continue;
                }

                List<PasswordItem> candidates = _passwordService.GetPasswordCandidates(
                    task,
                    Settings.UseGlobalPasswordForAllTasks ? GlobalPassword : string.Empty,
                    _passwordService.Passwords,
                    Settings.TryEmptyPasswordFirst,
                    Settings.EnableSidecarPassword);

                if (!SourceJunkScanner.HasUsablePasswordCandidate(candidates))
                {
                    result.Add(task);
                }
            }

            return result;
        }


        /// <summary>兜底候选的**值 + 来源说明**（说明只走既有描述器，⛔ 绝不出明文）。</summary>
        private readonly struct FallbackCandidateSlice
        {
            private readonly IReadOnlyList<PasswordItem> _candidates;
            private readonly PasswordService _passwordService;

            public FallbackCandidateSlice(
                IReadOnlyList<PasswordItem> candidates,
                IReadOnlyList<string> values,
                PasswordService passwordService)
            {
                _candidates = candidates;
                Values = values;
                _passwordService = passwordService;
            }

            /// <summary>交给 WinRAR 的候选值（保序）。</summary>
            public IReadOnlyList<string> Values { get; }

            /// <summary>第 <paramref name="ordinal"/> 项（1 起）的来源说明。</summary>
            public string Describe(int ordinal)
            {
                if (ordinal >= 1 && ordinal <= _candidates.Count)
                {
                    return _passwordService.BuildTryPasswordLogText(_candidates[ordinal - 1], ordinal);
                }

                return $"尝试密码候选第 {ordinal} 项：******";
            }
        }

        /// <summary>
        /// 密码预检的三种结论（用户 2026-09-25 第 37 条）。
        /// </summary>
        private enum PasswordProbeOutcome
        {
            /// <summary>说不准（引擎给了别的错误 / 超时 / 探针目录写不进去）—— 调用方按老路解整包。</summary>
            Inconclusive,

            /// <summary>探针解开了：这个候选是对的，可以去解整包。</summary>
            Verified,

            /// <summary>探针明说密码不对：这个候选作废，整包一个字节都不用动。</summary>
            Rejected
        }


        /// <summary>
        /// 每个归档（**每一层**）最多真的试几个密码候选（AGENTS.md §9.2：每层、每任务、每批次都要有尝试上限）。
        ///
        /// 为什么必须有：解压模式下一个候选 = **一次完整解压**。加密分卷的第二层，
        /// 几百条密码本的包会被逐个候选整包重解一遍（几百 MB 起、磁盘一直在写），
        /// 表现就是"几十分钟不动"，最后还可能把"没试完"报成"密码错误"。
        ///
        /// 到上限时的状态是 <see cref="StatusText.PasswordAttemptLimitReached"/>，**不是**"密码错误"。
        ///
        /// 值取自设置项 <see cref="AppSettings.MaxPasswordAttemptsPerLayer"/>（界面可改、能落盘）。
        /// 这里再夹一次只是兜底：设置对象不一定走过 <see cref="AppSettings.Normalize"/>（例如测试里直接
        /// <c>new AppSettings()</c>），而 0 或负数会让"一个候选都不试"直接变成密码错误 —— 那是最误导人的结论。
        /// （旧写法：这里是个常量 10，用户在设置界面改了不生效。）
        /// </summary>
        private int MaxPasswordAttemptsPerLayer =>
            Math.Clamp(Settings.MaxPasswordAttemptsPerLayer, 1, 1000);

        internal readonly List<string> _manualBatchPasswords = new();

    }
}