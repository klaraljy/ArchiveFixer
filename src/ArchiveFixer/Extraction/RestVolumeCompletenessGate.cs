using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 链尾「按档处理其余物」之前最后一道闸门：**其余物里的分卷不许是"半套"**。
    ///
    /// <para><b>现场（用户 2026-09-30 真机 `一只顶美合集`，25 GB 被永久删除）</b>：
    /// 那一单的内层包是一组 6 片跨盘 ZIP（`一只顶美.z01`…`.z05` + 末片 `一只顶美.z删除ip`）。
    /// 末片当时**没被认出来是归档**（ZIP64 收尾那两条闸门，见 `docs/真机事故复盘.md` §44.2），
    /// 于是被当成**内容物**留在成品目录里，而同组的 5 卷被当成**过程物**收进了其余物。
    /// 链尾这一档只看到"任务成功 + 校验通过 + 链上没人失败" ⇒ 把其余物**整份彻底删除** ——
    /// 结果是一组包被拆开：末片 1.62 GB 留在成品目录、另外 25 GB 永久消失，谁都再也解不开。</para>
    ///
    /// <para><b>判据（只读盘上事实，⛔ 不猜、不调引擎）</b>：候选里的每一个"归档件"
    /// （分卷的一片 / 归档本体 / 名字被改坏的归档本体）算出一个**基名 + 族**；
    /// 只要成品目录这一棵树里（**其余物之外**）还存在**同基名、而且族也相同**的归档件，
    /// 就说明这一组被拆在两边 ⇒ **整份处理其余物等于把这一组毁掉** ⇒ 什么都不做。</para>
    ///
    /// <para>⚠ 2026-10-10（用户拍板）：判据从"只比基名"收紧成"**基名 + 族**" ——
    /// 只比基名会把跨族同基名的无关文件当成"同组的另一片"（真机 `111.rar` / `111.zip`）。
    /// ⛔ 只在**能证明两边不同族**时才放行（唯一出口
    /// <see cref="ArchiveFixer.Detection.VolumeGroupDetector.AreProvablyDifferentFamilies"/>），
    /// **判不出族 ⇒ 照旧拦**。</para>
    ///
    /// <para>⛔ 只认"看起来是归档件"的东西：普通内容文件（`X.mp4` / `X.jpg`）**不算伙伴** ——
    /// 包基名与内容文件名撞车是常态（`111\111\内容物`），拿它当伙伴会把正常的清理全拦死。</para>
    ///
    /// <para>⚠ 保守方向是刻意的：判不出 / 读不动 ⇒ 返回拦下的理由（**什么都不做**），
    /// 而不是放行 —— 删除是不可恢复的，这一档宁可多留一份过程物。</para>
    /// </summary>
    public static class RestVolumeCompletenessGate
    {
        /// <summary>「其余物」那一档的措辞（候选 = 整个其余物目录）。</summary>
        private const string RestDirectoryCandidatePrefix = "其余物里的";

        private const string RestDirectoryBlockerTail =
            "整份处理其余物就等于把这一组拆开，所以这一档什么都不做（删除不可恢复；要清就先确认这一组到底还要不要）";

        /// <summary>
        /// **逐层回收**那一档的措辞（候选 = 这一层要删的那几个内层包文件，不是一个其余物目录）。
        /// </summary>
        private const string LayerReclaimCandidatePrefix = "逐层回收准备删的";

        private const string LayerReclaimBlockerTail =
            "当场删掉这一份就等于把这一组拆开，所以这一层一个字节都不删"
            + "（删除不可恢复；这一份留到链尾按老口径处理 —— 而链尾那一档也要过同一道闸门，半套同样不删）";

        /// <summary>
        /// 其余物能不能整份处理。返回 <c>null</c> = 可以；返回一段话 = **拦下的具体理由**（调用方必须原样写进日志）。
        ///
        /// <para>⚠ 2026-10-03：判据本体搬到了 <see cref="DescribeBlockerForCandidates"/>（逐层回收那一档也要过同一道闸门），
        /// 这里只保留原签名与**逐字相同**的文案 —— 其余物那一档的调用点一个字都不用改。</para>
        /// </summary>
        /// <param name="restDirectory">其余物目录（`<成品目录>\其余物`）。</param>
        public static string? DescribeBlocker(string? restDirectory)
        {
            if (string.IsNullOrWhiteSpace(restDirectory) || !Directory.Exists(restDirectory))
            {
                // 其余物不在 / 拿不到：上游已有的判据会拦住，这里不越权。
                return null;
            }

            string fullRest = SafePathHelper.GetFullPathSafe(restDirectory);

            string? outputRoot = Path.GetDirectoryName(fullRest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            return DescribeBlockerForCandidates(new[] { fullRest }, outputRoot);
        }

        /// <summary>
        /// **逐层回收**那一档：这一层准备删的那几个内层包，跟成品目录树里还留着的同组归档件是不是"半套"。
        ///
        /// <para>与 <see cref="DescribeBlocker"/> **同一份判据**，只是候选从"一个其余物目录"换成
        /// "这一层要删的那几个文件"、措辞换成逐层回收那套（⛔ 不许另写一份判断 —— §44.2 那次
        /// "一组卷被拆在两边、剩下的被整份删掉"正是这道闸门专治的形状）。</para>
        /// </summary>
        /// <param name="candidatePaths">这一层准备删的文件（就是 <c>SourceCleanupService</c> 要删的那一份清单）。</param>
        /// <param name="artifactRoot">成品目录树根（同组的另一片可能还在里面）。</param>
        /// <param name="excludedPaths">
        /// 额外要**排除在扫描之外**的路径（默认没有）。
        ///
        /// <para><b>递归路逐层回收</b>用它排掉**本次递归的工作区**（<c>&lt;目标目录&gt;\.ArchiveFixer.work\…</c>）：
        /// 那条路的候选本来就在工作区里逐层产出，而递归**还没发布** —— 同一条链里别的层产物目录里
        /// 出现同基名的归档件，不是"成品目录里留下的另一片"（"成品目录"在这一刻还是空的），
        /// 拿它当伙伴会把每一次合法的回收全拦死（用户 2026-10-05：递归那 4 层链要逐层回收）。
        /// ⛔ 只排除调用方**明确列出来**的那一棵；别的照旧一律算（宁可多拦，绝不漏拦）。</para>
        /// </param>
        public static string? DescribeLayerReclaimBlocker(
            IReadOnlyList<string>? candidatePaths,
            string? artifactRoot,
            IEnumerable<string>? excludedPaths = null)
        {
            return DescribeBlockerForCandidates(
                candidatePaths,
                artifactRoot,
                LayerReclaimCandidatePrefix,
                LayerReclaimBlockerTail,
                requireRecognizedCandidates: true,
                excludedPaths);
        }

        /// <summary>
        /// **判据本体（唯一出口）**：<paramref name="candidatePaths"/>（准备删的那些文件或目录）
        /// 与 <paramref name="artifactRoot"/>（成品目录树根）是不是**同一组分卷被拆在两边**。
        ///
        /// <para><b>判据（只读盘上事实，⛔ 不猜、不调引擎）</b>：候选里每一个"归档件"
        /// （分卷的一片 / 归档本体 / 名字被改坏的归档本体）算出一个**基名**；
        /// 只要成品目录这一棵树里（**候选自己与候选目录之内除外**）还存在**同基名的归档件**，
        /// 就说明这一组被拆在两边 ⇒ 删掉候选等于把这一组毁掉 ⇒ 什么都不做。</para>
        ///
        /// <para>⛔ 只认"看起来是归档件"的东西：普通内容文件（`X.mp4` / `X.jpg`）**不算伙伴** ——
        /// 包基名与内容文件名撞车是常态（`111\111\内容物`），拿它当伙伴会把正常的清理全拦死。
        /// 目录同理（同名目录不算伙伴）。</para>
        ///
        /// <para>⚠ 保守方向是刻意的：判不出 / 读不动 ⇒ 返回拦下的理由（**什么都不做**），
        /// 而不是放行 —— 删除是不可恢复的，这一档宁可多留一份过程物。</para>
        /// </summary>
        /// <param name="candidatePaths">
        /// 准备删的那些：传**目录**时按"它顶层的文件"算基名、并把整个目录排除在扫描之外
        /// （其余物那一档就是这种用法）；传**文件**时按这些文件自身算基名、只把这几个路径排除。
        /// </param>
        /// <param name="artifactRoot">成品目录树根（扫描范围）。</param>
        /// <param name="candidatePrefix">措辞：候选那一侧怎么称呼（默认是其余物那一档的原话）。</param>
        /// <param name="blockerTail">措辞：结论那半句（默认是其余物那一档的原话）。</param>
        /// <param name="requireRecognizedCandidates">
        /// 候选里**一个归档件都认不出**时算不算拦下。<c>false</c>（默认，其余物那一档）= 不拦；
        /// <c>true</c>（逐层回收那一档）= 拦 —— 那一档要删的是"我们自己解出来的内层包"，
        /// 连它是不是某组卷的一片都判不出就**不许删**（2026-10-03 第二轮复核）。
        /// </param>
        /// <param name="excludedPaths">
        /// 额外要**排除在扫描之外**的路径（默认没有）。
        ///
        /// <para>就地替换那一档用它：撞名时"按相对路径算出来的那个落点"上站的是一份**不是我们搬来的**
        /// 同名残留 —— 我们已经决定一个字节都不动它（用户 2026-10-03 批准的用例 ①），
        /// 那它就不该被读成"同组另一片落在别处"、把这一组的清理整个拦下。
        /// ⛔ 只排除**明确列出来的**那些路径，别的照旧一律算"外面还有一片"。</para>
        /// </param>
        public static string? DescribeBlockerForCandidates(
            IEnumerable<string>? candidatePaths,
            string? artifactRoot,
            string candidatePrefix = RestDirectoryCandidatePrefix,
            string blockerTail = RestDirectoryBlockerTail,
            bool requireRecognizedCandidates = false,
            IEnumerable<string>? excludedPaths = null)
        {
            if (candidatePaths == null ||
                string.IsNullOrWhiteSpace(artifactRoot) ||
                !Directory.Exists(artifactRoot))
            {
                return null;
            }

            // 候选自己产出来的那些卷：基名 -> 第一个见到的名字（用来点名）
            var candidatePieces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 候选自己（或候选目录里的一切）不算"还留在成品目录里"；另外几个明确列出来的路径同样不算。
            var candidateFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidateDirectories = new List<string>();

            foreach (string? excluded in excludedPaths ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(excluded))
                {
                    candidateFiles.Add(SafePathHelper.GetFullPathSafe(excluded));
                }
            }

            foreach (string? candidate in candidatePaths)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                string fullCandidate = SafePathHelper.GetFullPathSafe(candidate);

                if (Directory.Exists(fullCandidate))
                {
                    candidateDirectories.Add(fullCandidate);

                    foreach (string file in EnumerateTopLevelFiles(fullCandidate))
                    {
                        if (TryGetArchivePieceBaseName(file, out string baseName))
                        {
                            candidatePieces.TryAdd(baseName, Path.GetFileName(file));
                        }
                    }

                    continue;
                }

                candidateFiles.Add(fullCandidate);

                if (TryGetArchivePieceBaseName(fullCandidate, out string fileBaseName))
                {
                    candidatePieces.TryAdd(fileBaseName, Path.GetFileName(fullCandidate));
                }
            }

            if (candidatePieces.Count == 0)
            {
                /*
                 * 候选里一个"归档件"都认不出来 —— 这是"确实是别的东西"还是"我们认不出"？
                 *
                 * ⛔ 2026-10-03 第二轮复核：**认不出就拦**（兜底一律落在"什么都不做"），
                 * 但只对**逐层回收**那一档（`requireRecognizedCandidates = true`）：
                 * 那一档要删的是"我们自己解出来的内层包"，连它是不是某组卷的一片都判不出 ⇒ 不许删。
                 * 「其余物」那一档保持既有口径（一个其余物目录里压根没有归档件 ⇒ 不拦，
                 * 这一条从 §44.2 落地起就是如此、有用例钉着）。
                 */
                if (!requireRecognizedCandidates)
                {
                    return null;
                }

                var names = new List<string>();

                foreach (string candidate in candidateFiles.Concat(candidateDirectories))
                {
                    names.Add(Path.GetFileName(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
                }

                return $"{candidatePrefix}「{string.Join("、", names)}」一个都认不出是归档件"
                    + "（既不是分卷的一片、也不是归档本体、名字去杂质之后也不是已知归档后缀）—— "
                    + "判不出它是不是某组卷的一片，所以这一个字节都不删"
                    + "（删除不可恢复；拿不准就先自己看一眼这一份到底是什么）";
            }

            /*
             * ⛔ 2026-10-03 第二轮复核：**这棵树读不动 = 拦下**（原来是 `catch ⇒ 当没有`，
             * 那等于把"判不出"当成了"外面没有同组的片"⇒ 放行）。与类注释那句"读不动就拦"对齐。
             */
            if (!TryEnumerateFilesSafe(artifactRoot, out IReadOnlyList<string> files, out string enumerationError))
            {
                return $"成品目录树读不动（{artifactRoot}：{enumerationError}）—— "
                    + "判不出同组还有没有别的片留在里面，所以这一个字节都不删"
                    + "（删除不可恢复；先解决那个目录的读取问题再来）";
            }

            /*
             * 扫描根本身就在工作区里时（就地替换那一档要扫的就是本任务自己的暂存树 `stage\`），
             * 下面那条"工作区里的东西不算"必须让位 —— 见循环里那一档的说明。
             */
            bool rootInsideWorkspace = IsInsideWorkspace(artifactRoot);

            foreach (string file in files)
            {
                string fullFile = SafePathHelper.GetFullPathSafe(file);

                if (candidateFiles.Contains(fullFile))
                {
                    continue;   // 候选自己
                }

                if (candidateDirectories.Exists(directory => IsInside(directory, file)))
                {
                    continue;   // 候选目录里面的不算"在外面"
                }

                if (IsInsideWorkspace(file) && !rootInsideWorkspace)
                {
                    /*
                     * ⛔ 工作区（`<目标目录>\.ArchiveFixer.work\…`）是我们自己的暂存区、收尾时整份删掉，
                     * 里面的东西**不算"成品目录里的另一片"**。
                     * 2026-10-03 实测踩过：内层包改名（`inner.7删除z` → `inner.7z`）之前的那份**暂存副本**
                     * 就躺在工作区的 `stage\` 里，只按名字判会把同一份包当成"半套"⇒ 把一次合法的
                     * 逐层回收误拦下来（既有用例 `InnerLayerContinuationTests.彻底删除档_链尾把内层包连其余物一起删掉` 当场变红）。
                     *
                     * ⚠ 2026-10-03 第三轮补的一档：**扫描根本身就在工作区里面**时（就地替换那一档要扫的
                     * 就是本任务自己的暂存树 `stage\`）这一条跳过必须让位 —— 那里面的东西正是这一次要判的对象，
                     * 全跳等于把这道闸门整个作废（判据变成恒真）。别的调用点传的都是成品目录树，
                     * `rootInsideWorkspace` 恒为 false，行为一个字没变。
                     */
                    continue;
                }

                /*
                 * ===== **已经在"其余物"里的，不算"成品目录里留着的另一片"** =====
                 *
                 * 真机 CCCC 2026-10-06 22:22：其余物里那一份（入口包 `111.zip`，48.35 MB）删不掉，
                 * 外面被点名的两样 `111.z01`、`111.rar` **全都躺在组层其余物里**
                 * —— 那是**已经收拢好、正等着按档删掉**的过程物，拿它们当"外面还留着一片"
                 * 就把同一次收尾里的删除全拦死（批末重试 4 次全被同一句挡住）。
                 *
                 * ⛔ 红线一个字不放松：25 GB 那一次外面留下的是**成品目录里的内容物**
                 * （不在任何其余物里）⇒ 照旧一律拦下；`.z01..z05` 对末片 `.zip` 那种真被拆开的形状
                 * 只要有一片还躺在成品目录里，这道闸门照样拦。
                 */
                if (ProcessArtifactLayout.IsInsideDeletableProcessFolders(file))
                {
                    continue;
                }

                if (!TryGetArchivePieceBaseName(file, out string baseName)
                    || !candidatePieces.TryGetValue(baseName, out string? candidateName))
                {
                    continue;
                }

                /*
                 * ===== ⛔ 基名相同**不足**以说明它们是同一组（用户 2026-10-10 拍板换判据）=====
                 *
                 * 真机 EEEE 2026-10-10 实测：空间不足模式下 `111.rar` 的源包**没能在定稿那一刻还回去**，
                 * 就是被这一行拦下的，日志逐字：「逐层回收准备删的「111.rar」是一组分卷的一片，
                 * 而同组的另一片「111.zip」还在成品目录里（两边基名都是「111」）」。
                 * 可 `111.rar` 是 **RarOld 族**（归档本体）、`111.zip` 是 **ZipSpanned 族**（跨盘 ZIP 末片）
                 * —— 按项目唯一那把"同一组 = 族 + 基名"的尺子（`VolumeGroupDetector.BelongsToSameGroup`，
                 * 守门 `StalledGroupRegistrationTests` 逐字钉着这对名字不同族）它们**不是一组**，
                 * 拦下的理由是假的 ⇒ 那 48 MB 白等了一整条链（盘真紧时就是成败之差）。
                 *
                 * ⛔ 保守方向一个字不松：只在**两边都解析得出来、而且族确实不同**时才放行
                 * （唯一出口 `VolumeGroupDetector.AreProvablyDifferentFamilies`）；
                 * **判不出族 ⇒ 照旧拦**。25 GB 那次事故的形状（同族同基名、末片名字还被改坏）
                 * 两边同族 / 或者有一边解析不出来 ⇒ 照旧拦得住。
                 */
                if (Detection.VolumeGroupDetector.AreProvablyDifferentFamilies(
                        Path.GetFileName(file),
                        candidateName))
                {
                    continue;
                }

                return $"{candidatePrefix}「{candidateName}」是一组分卷的一片，而同组的另一片「{Path.GetFileName(file)}」"
                    + $"还在成品目录里（两边基名都是「{baseName}」、同一族）—— {blockerTail}";
            }

            return null;
        }

        /// <summary>
        /// 这个文件名像不像"归档件"（分卷的一片 / 归档本体 / 名字被改坏的归档本体）？
        /// 像 ⇒ 给出它的**包基名**（<c>222.z01</c> → <c>222</c>、<c>222.zscip</c> → <c>222</c>）。
        ///
        /// <para>⛔ 判据全部转调既有唯一出口：分卷标记 <see cref="FileNameHelper.IsVolumePartFileName"/>、
        /// 归档后缀 <see cref="ExtensionHelper.IsKnownArchiveExtension"/>、
        /// 去杂质 <see cref="ExtensionHelper.TryRecoverDisguisedArchiveBody"/>、基名
        /// <see cref="FileNameHelper.GetArchiveBaseName"/> —— 这里不另写一套名字规则。</para>
        /// </summary>
        private static bool TryGetArchivePieceBaseName(string path, out string baseName)
        {
            baseName = string.Empty;

            string fileName = Path.GetFileName(path);

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            bool looksLikeArchivePiece =
                FileNameHelper.IsVolumePartFileName(fileName)
                || ExtensionHelper.IsKnownArchiveExtension(Path.GetExtension(fileName));

            if (!looksLikeArchivePiece)
            {
                // `一只顶美.z删除ip` 这种：最后一段去杂质之后是已知归档后缀。
                string lastSegment = fileName[(fileName.LastIndexOf('.') + 1)..];

                looksLikeArchivePiece = ExtensionHelper.TryRecoverDisguisedArchiveBody(
                    lastSegment,
                    out _,
                    out _);
            }

            if (!looksLikeArchivePiece)
            {
                return false;
            }

            baseName = FileNameHelper.GetArchiveBaseName(fileName);

            return !string.IsNullOrWhiteSpace(baseName);
        }

        /// <summary>其余物**顶层**的文件（卷都是摆在这一层的，⛔ 不递归下去翻用户的东西）。</summary>
        private static IEnumerable<string> EnumerateTopLevelFiles(string directory)
        {
            string[] files;

            try
            {
                files = Directory.GetFiles(directory);
            }
            catch
            {
                yield break;
            }

            foreach (string file in files)
            {
                yield return file;
            }
        }

        /// <summary>
        /// 列一棵树的文件清单。
        ///
        /// <para>⛔ <b>读不动 = 返回 false</b>（2026-10-03 第二轮复核改的）：原来是 <c>catch ⇒ yield break</c>，
        /// 那等于把"这棵树读不动"当成了"外面没有同组的片" ⇒ **放行** —— 与"兜底一律落在什么都不做"相反。
        /// 现在由调用方返回一句拦下的理由（写清是**哪一棵树**读不动、所以一个字节都不删）。</para>
        /// </summary>
        private static bool TryEnumerateFilesSafe(string directory, out IReadOnlyList<string> files, out string error)
        {
            try
            {
                files = EnumerateFilesForTest?.Invoke(directory)
                        ?? Directory.GetFiles(directory, "*", SearchOption.AllDirectories);

                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                files = Array.Empty<string>();
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// **只为单测留的缝**：造"这棵树读不动"那一档（⛔ 绝不去改用户目录的权限 / ACL）。
        ///
        /// <para>默认 null = 走真实文件系统。注入的假实现**必须对其它路径原样转调真实实现** ——
        /// 否则会串到并发跑的别的用例上去（这个静态缝是进程级的）。</para>
        /// </summary>
        internal static Func<string, IReadOnlyList<string>>? EnumerateFilesForTest { get; set; }

        /// <summary>这个路径是不是在那个目录之下（含相等判定用不上：这里比的是文件与目录）。</summary>
        private static bool IsInside(string directory, string path)
        {
            string full = SafePathHelper.GetFullPathSafe(path);
            string root = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 这个文件是不是落在**我们自己的工作区**里（`&lt;目标目录&gt;\.ArchiveFixer.work\…`，含它的任一层子目录）。
        /// 工作区是程序自己的暂存区、收尾时整份删掉 ⇒ 里面的东西不算"成品目录里的另一片"
        /// （判据唯一出口 = <see cref="VolumeContentInference.WorkDirectoryName"/>，⛔ 不另写一个字面量）。
        /// </summary>
        private static bool IsInsideWorkspace(string path)
        {
            string marker = Path.DirectorySeparatorChar
                + Detection.VolumeContentInference.WorkDirectoryName
                + Path.DirectorySeparatorChar;

            return path.Contains(marker, StringComparison.OrdinalIgnoreCase);
        }
    }
}
