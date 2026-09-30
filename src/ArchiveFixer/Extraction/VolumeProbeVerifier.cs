using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;

namespace ArchiveFixer.Extraction
{
    /// <summary>试开一次分卷组的结论。</summary>
    public sealed class VolumeProbeOutcome
    {
        /// <summary>顺序成立（引擎真的读到了里面的条目）。</summary>
        public bool Confirmed { get; init; }

        /// <summary>
        /// 成立，但引擎**读不出清单**：这一组是"文件名也加密"的归档（7z <c>-mhe</c> / RAR <c>-hp</c>），
        /// 要正确密码才列得出条目。
        ///
        /// <para>它不影响"成立"这个结论（引擎按第一张卷声明的偏移真读到了那一份加密头），
        /// 只影响**怎么说这句话** —— 用户 2026-09-29 真机就是这一类包。</para>
        /// </summary>
        public bool NeedsPassword { get; init; }

        /// <summary>成立时的完整卷序（第 1 卷在第一位）。</summary>
        public IReadOnlyList<VolumeCandidate> OrderedVolumes { get; init; } = Array.Empty<VolumeCandidate>();

        /// <summary>
        /// **真的试过**。false = 没试成（没有可用引擎 / 拿不到卷根 / 建不出试开目录 / **跨盘**）——
        /// 这一档是"没试"，⛔ 不是"不成立"；消费方（<c>VolumeGroupResolver</c>）必须分开算。
        /// </summary>
        public bool Attempted { get; init; }

        /// <summary>
        /// 没试的**结构化**原因（⛔ 消费方不许去比 <see cref="Reason"/> 的中文）。
        /// 只有 <see cref="VolumeTrialSkipReason.CrossVolume"/> 会改变结论：跨盘 ⇒ 判不出。
        /// </summary>
        public VolumeTrialSkipReason SkipReason { get; init; } = VolumeTrialSkipReason.None;

        /// <summary>试了几种排列。</summary>
        public int Attempts { get; init; }

        /// <summary>给人看的一句话（成立 / 不成立都说清为什么）。</summary>
        public string Reason { get; init; } = string.Empty;
    }

    /// <summary>
    /// **试开验证**：把候选按假设名做成硬链接，让既有引擎真的列一次目录 —— 列出来才算数。
    ///
    /// <para><b>为什么必须真的试开</b>：7z 的分卷内容里没有卷号（见
    /// <see cref="VolumeContentInference"/> 的注释），"按尺寸猜顺序"只是**假设**；
    /// 假设错了去改名，就是把一组本来能解的包改成解不开的（改名不可逆、用户还得自己改回来）。
    /// 引擎列一次目录的证据强度，比任何名字/尺寸规律都高。</para>
    ///
    /// <para><b>⛔ 绝不复制文件</b>：一卷 2 GiB，复制一组要几十 GiB 和几分钟。
    /// 这里一律用 <c>CreateHardLinkW</c> 在**同一卷**上建第二个名字（零字节、瞬时）。
    /// 由此推出两条硬规矩（用户 2026-09-30 红线：「工作区就设在解压的地方，这就完全不存在跨盘的操作」）：
    /// <b>① 没有工作区根（拿不到这一单的目标目录）⇒ 一次都不试</b>；<b>② 跨卷 ⇒ 一次都不试</b>。
    /// ⛔ 不许退到源卷根 / 程序目录 / 临时目录另开一个工作区 —— 判不出就如实报"无法确认"。</para>
    ///
    /// <para><b>本类不引用 WPF</b>，只依赖 <see cref="IArchiveEngine"/> 与文件系统。</para>
    /// </summary>
    public sealed class VolumeProbeVerifier
    {
        private readonly IArchiveEngine _engine;

        public VolumeProbeVerifier(IArchiveEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        /// <summary>
        /// 先确认"第一卷自己打不开"（打得开 = 它本身就是个完整压缩包，不是分卷的第一卷，那就什么都别做），
        /// 再逐个假设顺序试开。
        /// </summary>
        /// <param name="firstVolumePath">第一卷。</param>
        /// <param name="orderings">后续卷的候选排列（每一组都不含第一卷）。</param>
        /// <param name="cancellationToken">取消。</param>
        /// <param name="preferredWorkRoot">
        /// 试开临时物的落点（<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>，用户 2026-09-30 口径）。
        /// <b>没有它 / 它跨卷 ⇒ 一次都不试</b>：⛔ 不退到"源卷根下的 <c>.ArchiveFixer.work</c>"
        /// （工作区只准设在解压的地方，在源盘开目录正是不变量 12 禁止的形态；本机 C:\ 与 E:\ 根上
        /// 那两个空壳就是这条老路留下的），⛔ 也不改用程序目录 / 临时目录。
        /// 两档都如实报"无法确认"（<see cref="VolumeProbeOutcome.SkipReason"/> =
        /// <see cref="VolumeTrialSkipReason.NoProbeRoot"/> / <see cref="VolumeTrialSkipReason.CrossVolume"/>）。
        /// </param>
        public async Task<VolumeProbeOutcome> VerifyAsync(
            string? firstVolumePath,
            IReadOnlyList<IReadOnlyList<VolumeCandidate>>? orderings,
            CancellationToken cancellationToken = default,
            string? preferredWorkRoot = null)
        {
            if (string.IsNullOrWhiteSpace(firstVolumePath) || !File.Exists(firstVolumePath))
            {
                return Refuse(0, "第一卷不在了", attempted: false, VolumeTrialSkipReason.Other);
            }

            if (orderings == null || orderings.Count == 0)
            {
                return Refuse(
                    0,
                    "同目录里没有可当后续卷的候选文件",
                    attempted: false,
                    VolumeTrialSkipReason.NoOrderings);
            }

            if (_engine == null || !_engine.IsAvailable)
            {
                return Refuse(
                    0,
                    "当前没有可用的解压引擎，没法试开验证（宁可不改，也不猜）",
                    attempted: false,
                    VolumeTrialSkipReason.NoEngine);
            }

            bool hasPreferredRoot = !string.IsNullOrWhiteSpace(preferredWorkRoot);

            /*
             * ⛔ 没有工作区根 ⇒ **一次都不试**（用户 2026-09-30 红线：「工作区就设在解压的地方，
             * 这就完全不存在跨盘的操作」）。
             *
             * 以前这一档会退到"第一卷所在卷根下的 .ArchiveFixer.work"—— 那就是"专门在用户盘上开一个
             * 工作区"，本机实测 C:\ 与 E:\ 根上那两个空壳就是它留下的。整条删掉，⛔ 也不许改退到
             * 程序目录 / 临时目录：证不出来就如实说"无法确认"，宁可什么都不做。
             */
            if (!hasPreferredRoot)
            {
                return Refuse(
                    0,
                    "这条路没有工作区根（拿不到这一单的目标目录）⇒ 不许试开：工作区只准设在解压的地方，"
                    + "⛔ 不在源卷根 / 程序目录 / 临时目录另开一个 —— 证不出整组是齐的，只能如实报「无法确认」",
                    attempted: false,
                    VolumeTrialSkipReason.NoProbeRoot);
            }

            /*
             * ⛔ 跨盘 = 一次都不试（用户 2026-09-30 决定，见 <paramref name="preferredWorkRoot"/> 的说明）。
             * 判据是"两个路径在不在同一个卷"，⛔ 不去比文案、也不试一次看看行不行 ——
             * 试开的第一步就是在那个目录里建东西，那正是被否决的动作。
             */
            if (!VolumeContentInference.IsSameVolumeRoot(firstVolumePath, preferredWorkRoot))
            {
                return Refuse(
                    0,
                    "跨盘无法试开，整卷是否齐全无法确认 —— 硬链接不能跨卷（源文件与目标工作区不在同一个卷上），"
                    + "⛔ 不复制大文件、也不在源盘上开工作区，所以这一档只能判「无法确认」",
                    attempted: false,
                    VolumeTrialSkipReason.CrossVolume);
            }

            string probeRoot = VolumeContentInference.BuildProbeRoot(firstVolumePath, preferredWorkRoot);

            if (probeRoot.Length == 0)
            {
                return Refuse(
                    0,
                    "拿不到可用的试开目录（工作区根给得不合法）—— 不复制大文件，所以不试",
                    attempted: false,
                    VolumeTrialSkipReason.NoProbeRoot);
            }

            int attempts = 0;

            try
            {
                try
                {
                    Directory.CreateDirectory(probeRoot);
                }
                catch (Exception ex)
                {
                    return Refuse(
                        0,
                        $"试开目录建不出来（{ex.Message}）",
                        attempted: false,
                        VolumeTrialSkipReason.NoProbeRoot);
                }

                // ① 单独一卷能不能完整打开。能 → 它本来就是个完整包，改名只会把它弄坏。
                VolumeProbeOutcome? solo = await TrySoloAsync(firstVolumePath, probeRoot, cancellationToken)
                    .ConfigureAwait(false);

                if (solo != null)
                {
                    return WithAttempted(solo, attempted: true);
                }

                // ② 逐个假设顺序试开：第一个"列得出来"的顺序就是它。
                foreach (IReadOnlyList<VolumeCandidate> ordering in orderings)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string trialDirectory = Path.Combine(probeRoot, "try-" + attempts.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    attempts++;

                    var volumes = new List<VolumeCandidate> { new() { Path = firstVolumePath, Size = SizeOf(firstVolumePath) } };
                    volumes.AddRange(ordering);

                    TrialResult trial = await TryLinkAndListAsync(volumes, trialDirectory, cancellationToken).ConfigureAwait(false);

                    if (trial.Failure == null)
                    {
                        return new VolumeProbeOutcome
                        {
                            Confirmed = true,
                            Attempted = true,
                            NeedsPassword = trial.EncryptedArchive,
                            OrderedVolumes = volumes,
                            Attempts = attempts,
                            Reason = trial.EncryptedArchive
                                ? $"按试开验证的顺序成立了（试了 {attempts} 种排列；引擎认出了这一组是一份"
                                  + "「文件名也加密」的归档，给不出密码就读不出清单）"
                                : $"按试开验证的顺序成立了（试了 {attempts} 种排列）"
                        };
                    }

                    LastFailure = trial.Failure;
                }

                return Refuse(attempts, $"试了 {attempts} 种排列都不成立（最后一次：{LastFailure}）", attempted: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Refuse(attempts, $"试开过程中出错：{ex.Message}", attempted: attempts > 0);
            }
            finally
            {
                TryDelete(probeRoot);
            }
        }

        /// <summary>
        /// <see cref="VolumeGroupResolver"/> 要的那一个试开出口：把请求翻译成本类的硬链接试开，
        /// 再把结论折成 <see cref="VolumeTrialOutcome"/>。
        ///
        /// <para>⛔ <b>只读</b>：源文件一个字节都不动 —— 只在试开目录里建**硬链接**（零字节、瞬时），
        /// 收工把整棵试开目录删掉。这是"试开确认"能当最高权重证据的前提。</para>
        /// </summary>
        public async Task<VolumeTrialOutcome> TryOpenAsync(
            VolumeTrialRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                return new VolumeTrialOutcome { Attempted = false, Reason = "试开请求为空" };
            }

            VolumeProbeOutcome outcome = await VerifyAsync(
                    request.FirstVolumePath,
                    request.Orderings,
                    cancellationToken,
                    request.PreferredWorkRootDirectory)
                .ConfigureAwait(false);

            var ordered = new List<string>();

            foreach (VolumeCandidate volume in outcome.OrderedVolumes)
            {
                ordered.Add(volume.Path);
            }

            return new VolumeTrialOutcome
            {
                Confirmed = outcome.Confirmed,
                Attempted = outcome.Attempted,
                SkipReason = outcome.SkipReason,
                NeedsPassword = outcome.NeedsPassword,
                OrderedVolumePaths = ordered,
                Attempts = outcome.Attempts,
                Reason = outcome.Reason
            };
        }

        /// <summary>补一个"真的试过"的标记（<see cref="TrySoloAsync"/> 的结论是确定的，但它自己不知道试没试）。</summary>
        private static VolumeProbeOutcome WithAttempted(VolumeProbeOutcome outcome, bool attempted) => new()
        {
            Confirmed = outcome.Confirmed,
            Attempted = attempted,
            SkipReason = outcome.SkipReason,
            NeedsPassword = outcome.NeedsPassword,
            OrderedVolumes = outcome.OrderedVolumes,
            Attempts = outcome.Attempts,
            Reason = outcome.Reason
        };

        /// <summary>最后一次试开失败的原因（只为把话说清，不参与判定）。</summary>
        private string LastFailure { get; set; } = "没有可用信息";

        /// <summary>单独一卷试开：返回非 null = **已经可以下结论了**（自己就是完整包 → 不该改名）。</summary>
        private async Task<VolumeProbeOutcome?> TrySoloAsync(
            string firstVolumePath,
            string probeRoot,
            CancellationToken cancellationToken)
        {
            string soloDirectory = Path.Combine(probeRoot, "solo");
            var solo = new List<VolumeCandidate> { new() { Path = firstVolumePath, Size = SizeOf(firstVolumePath) } };

            TrialResult trial = await TryLinkAndListAsync(solo, soloDirectory, cancellationToken).ConfigureAwait(false);

            /*
             * ⛔ 「单卷就报加密归档」必须**拒绝**（用户 2026-09-29 真机副本取证）：
             * 7z 的"下一份头"写在**最后一卷**里，位置由第一张卷第 0 字节的 Start Header 给出；
             * 单卷试开就报"头加密"，说明那一份头**就在这个文件里面** —— 它本身就是个**完整**的加密包，
             * 不是分卷的第一卷（把它当第一卷改名只会把它弄坏）。
             * 反过来，"分卷的第一卷独自一人"读不到头（头在后面的卷里），报的是"打不开 / 通用分片"。
             */
            if (trial.EncryptedArchive)
            {
                return Refuse(
                    0,
                    "第一卷自己就能完整打开（文件名也加密，没有密码读不出清单）"
                    + " —— 它本身就是个完整的压缩包，不是分卷的第一卷（不动它）",
                    attempted: true);
            }

            if (trial.Failure != null)
            {
                // 打不开 → 正是"后面还有卷"的样子，可以接着试排列。
                return null;
            }

            return Refuse(
                0,
                "第一卷自己就能完整打开 —— 它本身就是个完整的压缩包，不是分卷的第一卷（不动它）",
                attempted: true);
        }

        /// <summary>一次试开的结论（<see cref="Failure"/> 为 null = 成立）。</summary>
        private readonly struct TrialResult
        {
            public TrialResult(string? failure, bool encryptedArchive)
            {
                Failure = failure;
                EncryptedArchive = encryptedArchive;
            }

            /// <summary>失败原因；null = 这一次成立。</summary>
            public string? Failure { get; }

            /// <summary>引擎的结论是"这一组是一份**文件名也加密**的归档（7z <c>-mhe</c> / RAR <c>-hp</c>）"。</summary>
            public bool EncryptedArchive { get; }
        }

        /// <summary>
        /// 按假设名把这几卷硬链接进 <paramref name="directory"/> 并让引擎列一次；
        /// 返回 <c>Failure == null</c> = 成立（顺序成立，或引擎认出了"这一组是加密归档"）。
        /// </summary>
        private async Task<TrialResult> TryLinkAndListAsync(
            IReadOnlyList<VolumeCandidate> volumes,
            string directory,
            CancellationToken cancellationToken)
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                return new TrialResult($"试开目录建不出来（{ex.Message}）", false);
            }

            string firstLink = string.Empty;

            for (int i = 0; i < volumes.Count; i++)
            {
                string linkPath = Path.Combine(directory, VolumeContentInference.ProbeFileName(i + 1));

                if (!CreateHardLinkW(linkPath, volumes[i].Path, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();

                    return new TrialResult(
                        $"做不了硬链接（Win32 错误 {error}）—— 源目录与试开目录必须在同一个卷上，且文件系统要支持硬链接",
                        false);
                }

                if (i == 0)
                {
                    firstLink = linkPath;
                }
            }

            ArchiveListResult list = await _engine
                .ListAsync(ArchiveRequest.For(firstLink, null), cancellationToken)
                .ConfigureAwait(false);

            if (!list.Success)
            {
                /*
                 * ===== 「引擎认得出这一组，只是头加密读不出清单」= **肯定**回答（用户 2026-09-29 真机副本取证）=====
                 *
                 * 现场：`amb909.7.01`（2 GiB，带 7z 魔数）+ `amb909.z.2`（1.89 GB 裸续卷）本来就是一组**完整**的两卷包，
                 * 只是当年造包时开了 `-mhe`（文件名也加密）。对**卷齐**的这一组，7-Zip 报的是
                 * `Cannot open encrypted archive. Wrong password?`（引擎的结构化结论 = `EncryptedHeaders`）；
                 * 而"卷不齐 / 拿别的文件顶替"报的是 `Cannot open the file as [7z] archive` + `Unexpected end of archive`
                 * （两条都在本地副本上实测过，见 AGENTS.md §11）。
                 *
                 * 老写法只认 `list.Success` → 这一组**永远**"试不成立" → 一个字节都不动 →
                 * 管线随后如实报「分卷缺失」。可真正的原因是「缺密码」：**诊断说错了**，
                 * 用户被指去满盘找卷，而卷就躺在同一个目录里。
                 *
                 * 凭什么敢把它当肯定回答：7z 的"下一份头"写在**最后一卷**里，位置由第一张卷第 0 字节的 Start Header
                 * 给出（现场 `NextHeaderOffset = 4038274896`、头长 66 → 头正好落在第二卷的最后一字节上）。
                 * 引擎能报出"这是加密归档"，就说明它按第一张卷声明的偏移**真读到了那一份头** —— 这一组成立；
                 * 它给不出的只是条目的名字。⛔ 判据仍然只由**这一次硬链接试开**回答（与"列得出清单"同一类证据），
                 * 不是新造的第二套"这是不是分卷"判据；试不出（打不开 / 通用分片）照旧一个字节都不动。
                 */
                if (string.Equals(list.ErrorType, EngineErrorTypes.EncryptedHeaders, StringComparison.OrdinalIgnoreCase))
                {
                    return new TrialResult(null, encryptedArchive: true);
                }

                return new TrialResult(
                    string.IsNullOrWhiteSpace(list.Message) ? "引擎列不出来" : list.Message,
                    false);
            }

            /*
             * ⛔ `IsRawSplitStream` 必须当成**失败**：那是 7-Zip 说"这不是归档，是一段通用分片"
             * —— 也就是"后续卷没找到、只看到裸字节流"。它退出码是 0，不判这一位就会把
             * "什么都没读到"当成"顺序成立"（同一个坑在管线上踩过，见 ArchiveListResult 的注释）。
             */
            if (list.IsRawSplitStream)
            {
                return new TrialResult("引擎只看到一段裸分片流（后续卷没接上）", false);
            }

            return new TrialResult(null, false);
        }

        private static VolumeProbeOutcome Refuse(
            int attempts,
            string reason,
            bool attempted,
            VolumeTrialSkipReason skipReason = VolumeTrialSkipReason.None) => new()
        {
            Confirmed = false,
            Attempted = attempted,
            SkipReason = skipReason,
            Attempts = attempts,
            Reason = reason
        };

        private static long SizeOf(string? path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? 0 : new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>删试开目录（含里面所有硬链接）。⛔ 删的是链接，源文件一个字节不动。</summary>
        private static void TryDelete(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // 留在盘上只是脏一点，绝不能因为清理失败把结论吞掉。
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
    }
}
