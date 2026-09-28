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

        /// <summary>成立时的完整卷序（第 1 卷在第一位）。</summary>
        public IReadOnlyList<VolumeCandidate> OrderedVolumes { get; init; } = Array.Empty<VolumeCandidate>();

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
    /// 这里一律用 <c>CreateHardLinkW</c> 在**同一卷**上建第二个名字（零字节、瞬时）；
    /// 因此工作目录必须建在第一卷**所在卷根**下（跨卷的硬链接不存在，<c>Path.GetTempPath()</c> 可能跨卷）。</para>
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
        public async Task<VolumeProbeOutcome> VerifyAsync(
            string? firstVolumePath,
            IReadOnlyList<IReadOnlyList<VolumeCandidate>>? orderings,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(firstVolumePath) || !File.Exists(firstVolumePath))
            {
                return Refuse(0, "第一卷不在了");
            }

            if (orderings == null || orderings.Count == 0)
            {
                return Refuse(0, "同目录里没有可当后续卷的候选文件");
            }

            if (_engine == null || !_engine.IsAvailable)
            {
                return Refuse(0, "当前没有可用的解压引擎，没法试开验证（宁可不改，也不猜）");
            }

            string probeRoot = VolumeContentInference.BuildProbeRoot(firstVolumePath);

            if (probeRoot.Length == 0)
            {
                return Refuse(0, "拿不到第一卷所在的卷根，做不了硬链接（不复制大文件，所以不试）");
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
                    return Refuse(0, $"试开目录建不出来（{ex.Message}）");
                }

                // ① 单独一卷能不能完整打开。能 → 它本来就是个完整包，改名只会把它弄坏。
                VolumeProbeOutcome? solo = await TrySoloAsync(firstVolumePath, probeRoot, cancellationToken)
                    .ConfigureAwait(false);

                if (solo != null)
                {
                    return solo;
                }

                // ② 逐个假设顺序试开：第一个"列得出来"的顺序就是它。
                foreach (IReadOnlyList<VolumeCandidate> ordering in orderings)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string trialDirectory = Path.Combine(probeRoot, "try-" + attempts.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    attempts++;

                    var volumes = new List<VolumeCandidate> { new() { Path = firstVolumePath, Size = SizeOf(firstVolumePath) } };
                    volumes.AddRange(ordering);

                    string? failure = await TryLinkAndListAsync(volumes, trialDirectory, cancellationToken).ConfigureAwait(false);

                    if (failure == null)
                    {
                        return new VolumeProbeOutcome
                        {
                            Confirmed = true,
                            OrderedVolumes = volumes,
                            Attempts = attempts,
                            Reason = $"按试开验证的顺序成立了（试了 {attempts} 种排列）"
                        };
                    }

                    LastFailure = failure;
                }

                return Refuse(attempts, $"试了 {attempts} 种排列都不成立（最后一次：{LastFailure}）");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Refuse(attempts, $"试开过程中出错：{ex.Message}");
            }
            finally
            {
                TryDelete(probeRoot);
            }
        }

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

            string? failure = await TryLinkAndListAsync(solo, soloDirectory, cancellationToken).ConfigureAwait(false);

            if (failure != null)
            {
                // 打不开 → 正是"后面还有卷"的样子，可以接着试排列。
                return null;
            }

            return Refuse(0, "第一卷自己就能完整打开 —— 它本身就是个完整的压缩包，不是分卷的第一卷（不动它）");
        }

        /// <summary>
        /// 按假设名把这几卷硬链接进 <paramref name="directory"/> 并让引擎列一次；
        /// 返回 null = 列出成功（顺序成立），否则返回失败原因。
        /// </summary>
        private async Task<string?> TryLinkAndListAsync(
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
                return $"试开目录建不出来（{ex.Message}）";
            }

            string firstLink = string.Empty;

            for (int i = 0; i < volumes.Count; i++)
            {
                string linkPath = Path.Combine(directory, VolumeContentInference.ProbeFileName(i + 1));

                if (!CreateHardLinkW(linkPath, volumes[i].Path, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();

                    return $"做不了硬链接（Win32 错误 {error}）—— 源目录与试开目录必须在同一个卷上，且文件系统要支持硬链接";
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
                return string.IsNullOrWhiteSpace(list.Message) ? "引擎列不出来" : list.Message;
            }

            /*
             * ⛔ `IsRawSplitStream` 必须当成**失败**：那是 7-Zip 说"这不是归档，是一段通用分片"
             * —— 也就是"后续卷没找到、只看到裸字节流"。它退出码是 0，不判这一位就会把
             * "什么都没读到"当成"顺序成立"（同一个坑在管线上踩过，见 ArchiveListResult 的注释）。
             */
            if (list.IsRawSplitStream)
            {
                return "引擎只看到一段裸分片流（后续卷没接上）";
            }

            return null;
        }

        private static VolumeProbeOutcome Refuse(int attempts, string reason) => new()
        {
            Confirmed = false,
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
