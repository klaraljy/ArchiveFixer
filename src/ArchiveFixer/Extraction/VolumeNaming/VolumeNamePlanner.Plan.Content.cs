using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static ArchiveFixer.Extraction.VolumeNameRepair;
using static ArchiveFixer.Extraction.VolumeNameRestorer;

namespace ArchiveFixer.Extraction
{
    internal static partial class VolumeNamePlanner
    {

        /// <summary>
        /// 「**名字完全靠不住**」那一档：内容能回答多少就按内容办 —— 判据与执行体都只有这一份计划
        /// （用户 2026-09-28 三层方案的第 1 步 + 2026-09-29 的 RAR / ZIP 内容级卷号）。
        ///
        /// <para><b>三种格式走三条</b>（按内容挑，⛔ 不按后缀）：</para>
        /// <list type="number">
        /// <item><description><b>7z</b>：内容里**没有卷号**（只有第一卷有魔数），所以只能
        /// "同目录尺寸排候选顺序 + 硬链接试开验证"（<see cref="VolumeProbeVerifier"/>）。</description></item>
        /// <item><description><b>RAR</b>：卷号**写在内容里**（RAR5 在主归档头、RAR 1.5–4.x/RAR4 在卷尾归档结尾块）→
        /// 直接按卷号归组，不需要试开（<see cref="Detection.VolumeNumberFromContent"/>）。</description></item>
        /// <item><description><b>跨盘 ZIP</b>：末片的 EOCD 里有盘号（= 总片数），非末片只有结尾的跨盘标记 →
        /// 只有 2 片时能用消去法定序。</description></item>
        /// </list>
        ///
        /// <para><b>改名永远先过三道</b>：整组卷号必须连成 1..N（内容自洽）、目标名没被占用（⛔ 绝不覆盖）、
        /// 至少有一卷的名字真的要改。任何一道不过 → <c>CanRepair = false</c> + 写明为什么，**一个字节都不动**。</para>
        ///
        /// <para><b>⛔ 工作区根必须由调用方传进来</b>（不变量 12：需要临时物的地方一律由调用方把工作区根传进去，
        /// 传不进来就不做那件事）。7z 那条路要"硬链接 + 引擎列目录"才算得出顺序，而硬链接**不能跨卷** ——
        /// 所以：<b>同盘</b>（工作区根与第一卷在同一个卷）⇒ 探针落在目标工作区里做，这条路照旧可用；
        /// <b>跨盘 / 没传</b> ⇒ **不试开**，如实报"无法确认"，**不出改名计划**（⛔ 不退源卷根、⛔ 不复制、
        /// ⛔ 不往程序目录或 <c>%TEMP%</c> 写）。参数**没有默认值**就是这个意思：每个调用点都必须显式表态，
        /// 不许留下"不传 ⇒ 悄悄退到卷根"的老路。</para>
        /// </summary>
        /// <param name="currentPath">要修的那一卷（组里任意一卷）。</param>
        /// <param name="filesInDirectory">同目录候选（拿不到就传 null）。</param>
        /// <param name="engine">试开用的引擎。</param>
        /// <param name="workRootDirectory">
        /// 这一单的目标工作区根（<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>）。空 = 调用方拿不到工作区根
        /// ⇒ 不做试开（如实报"无法确认"）。
        /// </param>
        /// <param name="passwordCandidates">
        /// 密码候选（**只给值、只活在内存里**）。跨盘 zip 那一档要用它：加密的包只有用对的密码
        /// 才验得出"这几片的先后对不对"（顺序错与密码错在引擎那儿是两句话，但没密码就两句话都听不到）。
        /// 空 = 手上没有密码 ⇒ 加密的包只能如实报"顺序没法验证"。
        /// </param>
        /// <param name="cancellationToken">取消。</param>
        public static async Task<VolumeNameRepairPlan> PlanByContentAsync(
            string? currentPath,
            IEnumerable<VolumeCandidate>? filesInDirectory,
            Engines.IArchiveEngine engine,
            string? workRootDirectory,
            IReadOnlyList<string>? passwordCandidates = null,
            CancellationToken cancellationToken = default)
        {
            return await PlanByContentCoreAsync(
                    currentPath,
                    filesInDirectory,
                    engine,
                    workRootDirectory,
                    passwordCandidates,
                    allowNearbyDirectories: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// 与 <see cref="PlanByContentAsync"/> 同一份实现，只是**允许候选来自邻近目录**
        /// （调用方用 <see cref="EnumerateVolumeCandidatesNearby"/> 收的池）。
        ///
        /// <para>⛔ 只有**内容能自证身份**的两条路（跨盘 ZIP 的索引、RAR 的内容卷号）才吃放宽的池：
        /// 那两条路上"这一份是不是这一组的"由文件自己的字节回答，邻近目录里别的包的卷**冒充不了**。
        /// <b>7z 不吃</b> —— 它的中间片内容里没有任何身份信息，"尺寸排序 + 试开"在放宽的池子里会把
        /// 别的包的卷一起排进候选排列，既白烧引擎调用、又可能把真正那一组的顺序挤出排列上限
        /// （真机夹具上实测：放宽后两组直接解不出来）。所以 7z 这一档**只用同目录候选**，
        /// 判不出来就如实说判不出来。</para>
        /// </summary>
        public static async Task<VolumeNameRepairPlan> PlanByContentWithNearbyCandidatesAsync(
            string? currentPath,
            IEnumerable<VolumeCandidate>? filesInDirectory,
            Engines.IArchiveEngine engine,
            string? workRootDirectory,
            IReadOnlyList<string>? passwordCandidates = null,
            CancellationToken cancellationToken = default)
        {
            return await PlanByContentCoreAsync(
                    currentPath,
                    filesInDirectory,
                    engine,
                    workRootDirectory,
                    passwordCandidates,
                    allowNearbyDirectories: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static async Task<VolumeNameRepairPlan> PlanByContentCoreAsync(
            string? currentPath,
            IEnumerable<VolumeCandidate>? filesInDirectory,
            Engines.IArchiveEngine engine,
            string? workRootDirectory,
            IReadOnlyList<string>? passwordCandidates,
            bool allowNearbyDirectories,
            CancellationToken cancellationToken)
        {
            string path = currentPath ?? string.Empty;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Cannot(path, StatusText.VolumeRepairSourceMissing);
            }

            string fileName = Path.GetFileName(path);

            /*
             * ① **专属算法**：跨盘 zip 的"按归档自己的索引定盘"（用户 2026-10-01 真机 `FFF` 那一组 7 片）。
             *
             * 为什么它必须排在下面那三条统一算法的**前面**：跨盘 zip 的末片里有中央目录，
             * 而中央目录的每一条都写着"这个文件的本地头在**第几盘**、离那一盘开头多少字节" ——
             * 那是归档自己说的话，比"名字里的卷号""尺寸规律""试开"都硬，而且**不需要名字、不需要密码**。
             * 名字路/内容路都判不出来的那一档（名字里一个卷号都没有），只有它能判。
             *
             * ⚠ 它**不抢**统一算法的活：这一档不适用（不是跨盘 zip / 片数 < 3 / 剩的候选对不上）时返回 null，
             * 一律原样往下走 —— 老路子（RAR 内容卷号、跨盘 zip 两片消去法、7z 试开）一个字都没动。
             */
            VolumeNameRepairPlan? byIndex = await PlanSpannedZipByIndexAsync(
                path,
                filesInDirectory,
                engine,
                workRootDirectory,
                passwordCandidates,
                cancellationToken).ConfigureAwait(false);

            if (byIndex != null)
            {
                return byIndex;
            }

            /*
             * 按**内容**分派，⛔ 不按后缀、也不按"开头那几个字节"：跨盘 zip 的末片是从数据中间切出来的，
             * 开头根本没有本地文件头（7z 的 -v 切出来就是这样），只有 EOCD 说得清它是什么。
             */
            Detection.VolumeNumberReading self = Detection.VolumeNumberFromContent.Read(path);

            /*
             * ⛔ 7z **不吃放宽的候选池**（理由写在 PlanByContentWithNearbyCandidatesAsync 的注释里）：
             * 它的中间片内容里没有任何身份信息，邻近目录里别的包的卷和它无法区分 ——
             * 放宽只会把别的包拖进"尺寸排序 + 试开"的排列里。这一档**只用同目录候选**。
             */
            IEnumerable<VolumeCandidate>? pool = allowNearbyDirectories
                ? OnlySameDirectory(filesInDirectory, path)
                : filesInDirectory;

            switch (self.Format)
            {
                case Detection.VolumeContentFormat.SevenZip:
                    {
                        /*
                         * **入口判据换对象**（2026-10-03 阶段 B 的 (a)，方案 §0 结论 3）。
                         *
                         * 老口径问的是"**手上这一卷**的名字里有没有卷号"：

                         *     if (LegacyVolumeNames.TryGetIndex(fileName) != null) return Cannot(AlreadyStandard);
                         *
                         * 于是真机那一档当场被拒 —— `111.7z.001`（标准名第 1 卷）+ `111`（名字整个丢了，
                         * 本该叫 `111.7z.002`）+ `111.7z.003`：手上这一卷的名字**本来就是标准的**，
                         * 而"要改的那一卷"是它旁边那个丢了名字的兄弟，这条路连内容都没看就返回了。
                         *
                         * 新口径问的是"**这一组的名字自不自洽**"（判据只有一处：
                         * VolumeContentInference.ReadSiblingShape 的三条 —— 基名逐字相同 / 卷标记连续 /
                         * 除末片外等大），而且**这一组里得真有一片名字丢了**（NamelessFillers > 0）
                         * ⇒ 这时"缺的那一卷叫什么"是**已知的**，才值得进内容路去定序 + 试开。
                         *
                         * ⛔ 两者都不成立时照旧返回老结论（名字本来就标准、没有要补的名字）——
                         * 这样"一组名字齐全的标准分卷"不会被拖去白试开一次（第 45 条：成功的任务只留一行）。
                         */
                        Detection.SiblingVolumeShape shape = Detection.VolumeContentInference.ReadSiblingShape(
                            path,
                            pool,
                            Detection.VolumeContentInference.ExtensionFor(Detection.VolumeContentFormat.SevenZip));

                        if (LegacyVolumeNames.TryGetIndex(fileName) != null)
                        {
                            if (!shape.SelfConsistent || shape.NamelessFillers.Count == 0)
                            {
                                return shape.NamelessFillers.Count > 0 && !shape.SelfConsistent
                                    ? Cannot(
                                        path,
                                        string.Format(StatusText.VolumeRepairGroupShapeUnclearFormat, shape.Blocker))
                                    : Cannot(path, StatusText.VolumeRepairAlreadyStandard);
                            }
                        }

                        return await PlanSevenZipByContentAsync(
                                path,
                                path,
                                pool,
                                engine,
                                workRootDirectory,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                case Detection.VolumeContentFormat.Rar:
                    return PlanByNumberedContent(
                        path, filesInDirectory, VolumeNamingFamily.RarPart, Detection.VolumeContentFormat.Rar);

                case Detection.VolumeContentFormat.Zip:
                    /*
                     * zip 有**两族**，⛔ 不能一律按跨盘 zip 处理（用户 2026-10-17 现场，新样本 `1111`：
                     * `-tzip -v` 切出来的 `222-L1.zip.001/.002/.003` 被改成 `222-L1.z01/.z02/.zip`，
                     * 引擎按 `.zip` 读 EOCD、盘号是 0 ⇒ 认成单盘 zip ⇒ 整组解不出来）。
                     * 判据：名字形如 `<基名>.zip.<带杂质的数字>` ⇒ **通用分片族**（规范名照它自己，
                     * 杂质由容忍档剥掉；现场 `6666` 的 `打包111-L2.zip.删除001`）；
                     * 否则照旧按跨盘 zip 族（由内容证据/名字那一档决定）。
                     */
                    return PlanByNumberedContent(
                        path,
                        filesInDirectory,
                        System.Text.RegularExpressions.Regex.IsMatch(
                            System.IO.Path.GetFileName(path),
                            @"(?i)\.zip\.[^\\/]*\d{1,3}$")
                            ? VolumeNamingFamily.ZipNumbered
                            : VolumeNamingFamily.ZipSpanned,
                        Detection.VolumeContentFormat.Zip);

                default:
                    /*
                     * 内容不是 RAR / 跨盘 zip 的片，也**没有 7z 起始头** —— 但 7z 的**中间片与末片本来就是裸字节流**
                     * （内容里没有任何身份信息），所以"手上这一卷没有魔数"绝不等于"它不属于任何一组"。
                     *
                     * 真机那一档（`111.7z.001` / `111` / `111.7z.003`）里，用户手上完全可能就是那个名字丢了的
                     * `111`：这时**锚点去同目录找**（有 7z 起始头、基名与它逐字相同、这一组名字自洽、
                     * 而且它就是这组里名字丢了的那一片）⇒ 才按 7z 内容路走，基名从**锚点**推。
                     *
                     * ⛔ 找不到 / 找到两个说不清 ⇒ 照旧如实说"它的内容没说自己是分卷组的一员"，一个字节都不动。
                     */
                    string? sevenZipAnchor = TryFindSevenZipAnchor(path, pool);

                    if (sevenZipAnchor == null)
                    {
                        return Cannot(path, StatusText.VolumeRepairContentNotAVolumeMember);
                    }

                    return await PlanSevenZipByContentAsync(
                            sevenZipAnchor,
                            path,
                            pool,
                            engine,
                            workRootDirectory,
                            cancellationToken)
                        .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 手上这一卷**自己没有 7z 起始头**时，去同目录找这一组的**锚点**（= 带起始头的那一卷 = 第 1 卷）。
        ///
        /// <para>判据四条同时成立，缺一条就不算（⛔ 判不出 ⇒ 返回 null，"什么都不做"）：</para>
        /// <list type="number">
        /// <item><description>它**有 7z 起始头**（魔数，方案 §1.3 第 0 层：只认魔数，⛔ 不按后缀猜）；</description></item>
        /// <item><description>它的**基名**与手上这一卷逐字相同（<see cref="FileNameHelper.SecondStepBaseName"/>，
        /// 第一个点之前 —— 第二大步那份）；
        /// —— 这一条把"另一个包的同目录文件"挡在门外，而且只用名字，不读写别的文件；</description></item>
        /// <item><description>这一组的**名字自洽**且**真有一片名字丢了**（<see cref="Detection.VolumeContentInference.ReadSiblingShape"/>）；</description></item>
        /// <item><description>**手上这一卷正是那一片名字丢了的成员**（否则它不属于这一组）。</description></item>
        /// </list>
        ///
        /// <para>找到两个锚点 ⇒ 说不清 ⇒ 返回 null（判不出就不做）。</para>
        /// </summary>
        private static string? TryFindSevenZipAnchor(string path, IEnumerable<VolumeCandidate>? filesInDirectory)
        {
            const Detection.VolumeContentFormat format = Detection.VolumeContentFormat.SevenZip;
            string extension = Detection.VolumeContentInference.ExtensionFor(format);
            string directory = Path.GetDirectoryName(path) ?? string.Empty;

            var pool = (filesInDirectory ?? Array.Empty<VolumeCandidate>())
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Path))
                .Where(c => c.Size > 0)
                .Where(c => string.Equals(
                    Path.GetDirectoryName(c.Path), directory, StringComparison.OrdinalIgnoreCase))
                .ToList();

            string? selfBase = TryResolvePackageBaseName(path, extension);

            if (selfBase == null)
            {
                return null;
            }

            if (!pool.Any(c => SamePath(c.Path, path)))
            {
                pool.Add(new VolumeCandidate { Path = path, Size = LengthOf(path) });
            }

            string? found = null;

            foreach (VolumeCandidate candidate in pool)
            {
                if (SamePath(candidate.Path, path)
                    || Detection.VolumeContentInference.SniffFormat(candidate.Path) != format
                    || !string.Equals(
                        TryResolvePackageBaseName(candidate.Path, extension),
                        selfBase,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Detection.SiblingVolumeShape shape = Detection.VolumeContentInference.ReadSiblingShape(
                    candidate.Path, pool, extension);

                if (!shape.SelfConsistent
                    || shape.NamelessFillers.Count == 0
                    || !shape.NamelessFillers.Any(f => SamePath(f.Path, path)))
                {
                    continue;
                }

                if (found != null)
                {
                    // 同目录里有两个都说得通的锚点 ⇒ 说不清，判不出（⛔ 不挑一个）。
                    return null;
                }

                found = candidate.Path;
            }

            return found;
        }

        /// <summary>包基名（**第二大步那份**：<see cref="FileNameHelper.SecondStepBaseName"/>）；判不出返回 null。</summary>
        private static string? TryResolvePackageBaseName(string? path, string archiveExtension)
        {
            _ = archiveExtension;

            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string baseName = FileNameHelper.SecondStepBaseName(path);

            return baseName.Length > 0 ? baseName : null;
        }

        internal static long LengthOf(string? path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? 0 : new FileInfo(path!).Length;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>只留"与被修那一卷同目录"的候选（7z 那一档专用：它的内容里没有身份信息，不吃放宽的池）。</summary>
        private static IEnumerable<VolumeCandidate> OnlySameDirectory(
            IEnumerable<VolumeCandidate>? candidates,
            string currentPath)
        {
            string directory = Path.GetDirectoryName(currentPath) ?? string.Empty;

            return (candidates ?? Array.Empty<VolumeCandidate>())
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Path))
                .Where(c => string.Equals(
                    Path.GetDirectoryName(c.Path),
                    directory,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// 试拼时最多允许"定不下来"的片数：3 片 = 6 种排列，再多就不是"试一下"而是暴力搜索了
        /// （每一种排列都要让引擎真解码一遍整组；一组 675 MB 的包，一次就是几十秒）。
        /// </summary>
        private const int SpannedZipTrialMaxUndecidedDisks = 3;

        /// <summary>
        /// 跨盘 ZIP 的规范名前缀（stem）：**第二大步基名** = `FileNameHelper.SecondStepBaseName`。
        /// 末片名可能是 `X.mp4-572`、中间片可能是 `set.z甲` —— 都不许拿名字里的点段硬截；
        /// 基名只认那一个出口（第一个点之前）。规范名拼法仍只有 `BuildStandardNames` 一处。
        /// </summary>
        internal static bool TryDeriveSpannedStem(
            IReadOnlyList<string> ordered,
            string tailPath,
            int count,
            out string stem)
        {
            stem = FileNameHelper.SecondStepBaseName(tailPath);

            if (string.IsNullOrWhiteSpace(stem) && ordered.Count > 0)
            {
                stem = FileNameHelper.SecondStepBaseName(ordered[0]);
            }

            return !string.IsNullOrWhiteSpace(stem)
                && Detection.VolumeNumberFromContent
                    .BuildStandardNames(stem, VolumeNamingFamily.ZipSpanned, count).Count == count;
        }

        /// <summary>
        /// **专属算法：跨盘 zip 按归档自己的索引定盘**（用户 2026-10-01 真机 <c>FFF</c> 那一组）。
        ///
        /// <para>三步，一步比一步软：</para>
        /// <list type="number">
        /// <item><description><b>索引定盘（硬）</b>：末片的中央目录写着"每个文件的本地头在**第几盘**、
        /// 离那一盘开头多少字节" ⇒ 把每个候选文件当第 k 盘，去那个偏移处看是不是本地头、名字对不对得上。
        /// 对得上就是它 —— <b>不看名字、不用引擎、不用密码</b>。全钉住时直接出计划。</description></item>
        /// <item><description><b>缺卷（也硬）</b>：某一盘有锚点、可整个目录里没有一份对得上 ⇒ **那一片不在手上**，
        /// 如实点名缺第几片（⛔ 不是"判不出"）。</description></item>
        /// <item><description><b>剩下的几片（软）</b>：某一片里"一个文件都没开始"（整段夹在别的数据中间）⇒
        /// 它没有身份证，和另一片同样空白的片**在字节上完全对称**。这时才请引擎**试拼**：
        /// 几片就是几种排列（上限 3 片），硬链接进工作区、按标准卷名排好，让引擎**测试**一遍
        /// （顺序错了必然 CRC 错；⛔ 列目录没用 —— 中央目录在末片里、不看中间几片的数据）。</description></item>
        /// </list>
        ///
        /// <para>⛔ 这一档**不抢**统一算法的活：不适用（不是跨盘 zip / 片数 &lt; 3 / 候选对不上号）时返回
        /// <c>null</c>，调用方原样往下走；判不出来时也**只改名一个字节都不动**。</para>
        /// </summary>
        private static async Task<VolumeNameRepairPlan?> PlanSpannedZipByIndexAsync(
            string path,
            IEnumerable<VolumeCandidate>? filesInDirectory,
            Engines.IArchiveEngine engine,
            string? workRootDirectory,
            IReadOnlyList<string>? passwordCandidates,
            CancellationToken cancellationToken)
        {
            List<VolumeCandidate> candidates = (filesInDirectory ?? Array.Empty<VolumeCandidate>())
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Path))
                .ToList();

            if (candidates.Count < 3)
            {
                // 两片那一档统一算法本来就会（消去法），这一档不抢。
                return null;
            }

            /*
             * ① 同目录里找出"自述是跨盘 zip 末片"的那一份 —— 判据**只有一处**：
             * VolumeNumberFromContent.Read（EOCD 盘号 > 0 ⇒ Number = 盘号 + 1）。
             * 目录里出现两个"末片"就说不清哪一片才是末片 ⇒ 不抢，交给统一算法（它也会如实拒绝）。
             */
            Detection.VolumeNumberReading? tail = null;

            foreach (VolumeCandidate candidate in candidates)
            {
                Detection.VolumeNumberReading reading = Detection.VolumeNumberFromContent.Read(candidate.Path);

                if (reading.Format != Detection.VolumeContentFormat.Zip
                    || !reading.IsVolumeMember
                    || reading.Number == null)
                {
                    continue;
                }

                if (tail != null)
                {
                    return null;
                }

                tail = reading;
            }

            if (tail == null)
            {
                /*
                 * 末片不在了吗？（用户要的"说清缺的是第几片"）
                 *
                 * 手上的证据只有两条，但两条都硬：① 这些片**彼此等大**（跨盘 zip 的非末片都是切分上限那么大的满片）；
                 * ② 其中一片的**开头就是跨盘标记** `PK\x07\x08`（真 PKZIP / WinRAR 造的跨盘 zip 第 1 片长这样）。
                 * 合起来只有一个解释：这是一组跨盘 zip，缺的是**末片**（`.zip` 那一片，它有中央目录、是解压入口）。
                 * ⛔ 这一档不试开、不改名 —— 只是把"缺什么"说清楚，免得用户去满盘找一个根本不缺的中间片。
                 */
                VolumeNameRepairPlan? missingTail = DescribeMissingSpannedTail(path, candidates);

                if (missingTail != null)
                {
                    return missingTail;
                }

                return null;
            }

            Detection.SpannedZipIndex? index = Detection.SpannedZipIndex.TryRead(tail.Path);

            if (index == null || index.DiskCount < 3)
            {
                return null;
            }

            Detection.SpannedZipPin pin = index.Pin(candidates);

            /*
             * ⛔ 不自洽（候选池里多了/少了文件、末片比满片还大）⇒ 判不出这一档，原样交给统一算法。
             * 那**不是**"缺卷"的结论，⛔ 不能拿它去吓用户。
             */
            if (!pin.Consistent || !IsInSpannedGroup(pin, path))
            {
                return null;
            }

            // ② 有锚点却没人对得上 ⇒ 那几片**不在手上**：如实点名（这是结论，不是"判不出"）。
            if (pin.MissingDisks.Count > 0)
            {
                return Cannot(
                    path,
                    string.Format(
                        StatusText.VolumeRepairSpannedZipMissingFormat,
                        pin.DiskCount,
                        pin.MissingDisks.Count,
                        string.Join("、", pin.MissingDisks.Select(d => (d + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)))));
            }

            IReadOnlyList<string>? ordered = pin.AllPinned
                ? pin.Slots.Select(s => s!).ToList()
                : null;
            bool attempted = false;

            // ③ 还有几片定不下来 ⇒ 请引擎试拼（排列数有上限）。
            if (ordered == null)
            {
                if (pin.UndecidedDisks.Count > SpannedZipTrialMaxUndecidedDisks)
                {
                    return Cannot(
                        path,
                        string.Format(
                            StatusText.VolumeRepairSpannedZipUndecidedFormat,
                            pin.DiskCount,
                            pin.UndecidedDisks.Count));
                }

                var verifier = new VolumeProbeVerifier(engine);
                VolumeProbeOutcome probe = await verifier
                    .VerifySpannedZipOrderAsync(
                        BuildSpannedZipOrders(pin),
                        passwordCandidates,
                        workRootDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);

                attempted = probe.Attempted;

                if (!probe.Confirmed)
                {
                    /*
                     * ⛔ "没试"与"试过不成立"必须分开说（与 7z 那条路同一口径）：
                     * 没有工作区根 / 跨盘 ⇒ 一次都没试，只能如实报"无法确认"。
                     */
                    return probe.Attempted
                        ? Cannot(path, string.Format(StatusText.VolumeRepairContentProbeFailedFormat, probe.Reason), trialAttempted: true)
                        : Cannot(path, string.Format(StatusText.VolumeRepairNoProbeFormat, probe.Reason));
                }

                ordered = probe.OrderedVolumes.Select(v => v.Path).ToList();
            }

            // ④ 基名：优先末片；末片名坏了就从组内片推，最后退回基名。
            if (!TryDeriveSpannedStem(ordered, tail.Path, ordered.Count, out string stem))
            {
                return Cannot(path, StatusText.VolumeRepairNoSuggestion, trialAttempted: attempted);
            }

            IReadOnlyList<string> targetNames = Detection.VolumeNumberFromContent.BuildStandardNames(
                stem, VolumeNamingFamily.ZipSpanned, ordered.Count);

            if (targetNames.Count != ordered.Count)
            {
                return Cannot(path, StatusText.VolumeRepairNoSuggestion, trialAttempted: attempted);
            }

            var order = new Detection.VolumeGroupOrder
            {
                Confirmed = true,
                Slots = ordered
                    .Select((volume, i) => new Detection.VolumeGroupSlot { Path = volume, Number = i + 1 })
                    .ToList(),
                Fail = Detection.VolumeNumberFail.None,
                Detail = ordered.Count
            };

            // 入口那一卷 = 末片（引擎拿 `X.zip` 打开这一组，兄弟卷要在它旁边）。
            return BuildPlanFromOrder(
                path,
                order,
                targetNames,
                entryVolumePath: ordered[^1],
                trialAttempted: attempted);
        }

        /// <summary>
        /// 「末片不在了」这一档的诊断（**只出结论、不动任何文件**）。
        ///
        /// <para>判据两条，都要：① 同目录里至少 2 份文件**彼此等大**（满片规律 —— 跨盘 zip 除末片外都是满片）；
        /// ② 其中**恰好一份**的开头是跨盘标记 + 本地头（<c>PK\x07\x08PK\x03\x04</c>，实测真 PKZIP / WinRAR 的第 1 片）。
        /// 两条同时成立而目录里又没有一个自述是末片的文件 ⇒ 缺的就是末片。</para>
        ///
        /// <para>⚠ 这里用的是"**满片规律**"而不是"末卷更小"：后者**不是普遍成立**
        /// （实测 WinRAR 在条目比切分大小还大时，末片可以比满片大一倍）—— 拿它当闸门会误判。</para>
        /// </summary>
        private static VolumeNameRepairPlan? DescribeMissingSpannedTail(string path, List<VolumeCandidate> candidates)
        {
            List<VolumeCandidate> files = candidates
                .Where(c => c.Size > 0)
                .ToList();

            if (files.Count < 2)
            {
                return null;
            }

            long full = files
                .GroupBy(c => c.Size)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key)
                .First()
                .Key;

            List<VolumeCandidate> fullParts = files.Where(c => c.Size == full).ToList();

            if (fullParts.Count < 2 || fullParts.Count != files.Count)
            {
                // 不是"清一色满片"（有别的尺寸混着）⇒ 说不清，交给统一算法。
                return null;
            }

            if (!fullParts.Any(c => SamePath(c.Path, path)))
            {
                return null;
            }

            List<string> marked = fullParts.Where(StartsWithSpannedMarker).Select(c => c.Path).ToList();

            if (marked.Count != 1)
            {
                return null;
            }

            return new VolumeNameRepairPlan
            {
                CanRepair = false,
                CurrentPath = path,
                CurrentFileName = SafeFileName(path),
                Reason = string.Format(
                    StatusText.VolumeRepairSpannedZipTailMissingFormat,
                    fullParts.Count,
                    full,
                    SafeFileName(marked[0])),
                Siblings = fullParts.Select(c => SafeFileName(c.Path)).ToList(),
                SpannedTailMissing = true
            };
        }

        /// <summary>这一份的开头是不是"跨盘标记 + 本地头"（<c>PK\x07\x08PK\x03\x04</c>）。</summary>
        private static bool StartsWithSpannedMarker(VolumeCandidate candidate)
        {
            try
            {
                using var stream = new FileStream(
                    candidate.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                var head = new byte[8];

                if (stream.Read(head, 0, 8) < 8)
                {
                    return false;
                }

                return head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x07 && head[3] == 0x08
                    && head[4] == 0x50 && head[5] == 0x4B && head[6] == 0x03 && head[7] == 0x04;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 把"已经钉住的盘"与"剩下那几片的排列"拼成一份份**完整顺序**（每份都是 第 1 片 … 末片）。
        /// 只有定不下来的盘数 ≤ 3 时才会被调用（最多 6 份）。
        /// </summary>
        private static IReadOnlyList<IReadOnlyList<string>> BuildSpannedZipOrders(Detection.SpannedZipPin pin)
        {
            var results = new List<IReadOnlyList<string>>();
            string?[] slots = pin.Slots.ToArray();
            List<int> free = pin.UndecidedDisks.ToList();
            List<string> pool = pin.UndecidedCandidates.ToList();

            void Fill(int position, bool[] taken, string[] current)
            {
                if (position == free.Count)
                {
                    var snapshot = new string[slots.Length];

                    for (int i = 0; i < slots.Length; i++)
                    {
                        int freeIndex = free.IndexOf(i);
                        snapshot[i] = freeIndex >= 0 ? current[freeIndex] : slots[i]!;
                    }

                    results.Add(snapshot);
                    return;
                }

                for (int i = 0; i < pool.Count; i++)
                {
                    if (taken[i])
                    {
                        continue;
                    }

                    taken[i] = true;
                    current[position] = pool[i];
                    Fill(position + 1, taken, current);
                    taken[i] = false;
                }
            }

            if (free.Count == 0 || pool.Count != free.Count)
            {
                return Array.Empty<IReadOnlyList<string>>();
            }

            Fill(0, new bool[pool.Count], new string[free.Count]);

            return results;
        }

        /// <summary>手上的这一份在不在这一组里（钉住的片、或者"定不下来"的那几片之一）。</summary>
        private static bool IsInSpannedGroup(Detection.SpannedZipPin pin, string path) =>
            pin.Slots.Any(s => SamePath(s, path)) || pin.UndecidedCandidates.Any(p => SamePath(p, path));

        internal static bool SamePath(string? a, string? b) =>
            !string.IsNullOrWhiteSpace(a)
            && !string.IsNullOrWhiteSpace(b)
            && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}