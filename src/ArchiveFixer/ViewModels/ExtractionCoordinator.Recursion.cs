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
        /// <summary>
        /// 把暂存目录里"**名字不标准的一整套分卷**"整组改回标准卷名（用户 2026-10-01 真机）。
        ///
        /// <para><b>为什么需要它</b>：外层跨盘 zip（`222.zscip.zip` + `222.zscip.z01`）解出来的两个条目
        /// 本身**又是一份完整的两卷 split zip**，而它们的名字是归档内部记的原名（`222.zi删除p` + `222.z0sc1`）。
        /// 跨盘 zip 只按**标准卷名**找兄弟（末片 `X.zip`、首片 `X.z01`），名字不对**谁**都打不开
        /// —— 7-Zip / WinRAR / UnRAR 实测给的是同一句"打不开"。整组改名的两个调用点都在
        /// "任务表"那一侧（批首 / 手动修复），递归产物从没进过任务表 ⇒ 名字原样落地。</para>
        ///
        /// <para><b>放哪一步</b>：定稿规划的最开头（暂存目录已经定下来、还没做任何分类与套层）。
        /// 这样后面的分卷组闸门、套层判定、搬运看到的都是标准名，不必在四处各补一次特例。</para>
        ///
        /// <para><b>判据不放宽一个字节</b>（唯一出口 = <see cref="VolumeGroupResolver"/> 的结论 +
        /// <see cref="VolumeGroupResolution.IsGroupMember"/> 认下来的那一卷）：</para>
        /// <list type="bullet">
        /// <item><description>这一组必须**结论完整**（判不出 / 缺卷 / 疑缺 ⇒ 一个字都不改）；</description></item>
        /// <item><description>每一卷的**规范名都真的在盘上**（`X.zip` / `X.z01` …）—— 这一条正是"名字变体那一档"的解药：</description></item>
        /// </list>
        /// 现场那一组是 `222.zip`（另一个文件）+ `222.zi删除p`（这个名字本身是那一卷的另一个写法），
        /// 规范名 `222.zip` 在盘上、变体 `222.zi删除p` 也在盘上 ⇒ 把后者改回 `222.zip` 不成立（会覆盖），
        /// 这一组**保持原样**（它的内容一个字节都不丢，用户自己看得见）。
        ///
        /// <para>⛔ 全成或全不成：任何一卷没到位就**倒序改回原名**（与 <c>VolumeNameRepair.TryApply</c>
        /// 同一条纪律）；目标名被占的那一卷跳过、其余照改；改名失败只写 WARN，⛔ 绝不拖垮定稿。</para>
        /// </summary>
        private static void NormalizeEngineOutputVolumeNames(
            string stageRoot,
            List<(string Level, string Message)>? changes,
            HashSet<string>? renamedMembers)
        {
            if (string.IsNullOrWhiteSpace(stageRoot) || !Directory.Exists(stageRoot))
            {
                return;
            }

            foreach (string directory in EnumerateDirectoriesSafe(stageRoot))
            {
                RenameVolumeGroupInDirectory(directory, changes, renamedMembers);
            }
        }

        /// <summary>这一层（含根）的每个目录各判一次：这个目录里有没有"名字不标准但规范名齐全"的一整套分卷。</summary>
        private static void RenameVolumeGroupInDirectory(
            string directory,
            List<(string Level, string Message)>? changes,
            HashSet<string>? renamedMembers)
        {
            IReadOnlyList<VolumeGroupEntry> entries;

            try
            {
                var list = new List<VolumeGroupEntry>();
                int order = 0;

                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    long size;

                    try
                    {
                        size = new FileInfo(file).Length;
                    }
                    catch
                    {
                        continue;
                    }

                    list.Add(new VolumeGroupEntry
                    {
                        Path = SafePathHelper.GetFullPathSafe(file),
                        Size = size,
                        LastWriteTimeUtc = File.GetLastWriteTimeUtc(file),
                        OrderIndex = order++
                    });
                }

                entries = list;
            }
            catch
            {
                return;
            }

            if (entries.Count < 2)
            {
                return;
            }

            var candidates = entries
                .Select(e => new VolumeCandidate { Path = e.Path, Size = e.Size })
                .ToList();

            foreach (VolumeGroup group in VolumeGroupDetector.Group(candidates))
            {
                VolumeGroupResolution resolution = new VolumeGroupResolver().Resolve(new VolumeGroupQuery
                {
                    AnchorPath = group.FirstVolumePath,
                    DirectoryEntries = entries,
                    AllowTrialOpen = false
                });

                TryRenameVolumeGroupToStandardNames(resolution, changes, renamedMembers);
            }
        }

        /// <summary>
        /// 把一组改回标准卷名；判据不成立就原样返回（不写日志、不动盘）。
        ///
        /// <para>闸门只用"这一组**已经是完整的一组**"（<see cref="VolumeGroupVerdict.Complete"/>）+
        /// "**每一个成员**都能唯一推出一个规范名"两条：</para>
        /// <list type="bullet">
        /// <item><description>⛔ **不拿 `HasRenamedVolume` 当拦下条件** —— 它说的正是"这个名字不标准"，
        /// 而本方法的全部目的就是把不标准的名字改成标准的（拿它当拦下条件等于这个方法永远不干活）；
        /// 真机那一组（`222.zi删除p` + `222.z0sc1`）就是被它拦下的。</description></item>
        /// <item><description>⛔ 只要有一个成员推不出规范名、或两个成员推出同一个名字，**整组不动**
        /// （宁可保持原样，也不猜着改用户的名字）。</description></item>
        /// </list>
        /// </summary>
        private static void TryRenameVolumeGroupToStandardNames(
            VolumeGroupResolution resolution,
            List<(string Level, string Message)>? changes,
            HashSet<string>? renamedMembers)
        {
            if (resolution.Verdict != VolumeGroupVerdict.Complete)
            {
                return;
            }

            var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string member in resolution.GroupFilePaths)
            {
                if (File.Exists(member))
                {
                    onDisk.Add(member);
                }
            }

            if (onDisk.Count < 2)
            {
                return;
            }

            var moves = new List<(string From, string To)>();
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string member in onDisk)
            {
                string fileName = Path.GetFileName(member);
                string directory = Path.GetDirectoryName(member) ?? string.Empty;

                if (directory.Length == 0)
                {
                    return;
                }

                if (!TryResolveStandardVolumeName(fileName, out string target))
                {
                    return;
                }

                string targetPath = Path.Combine(directory, target);

                // 已经是标准名 ⇒ 这一卷不动（但它也占住了这个目标名）。
                if (string.Equals(fileName, target, StringComparison.OrdinalIgnoreCase))
                {
                    if (!targets.Add(targetPath))
                    {
                        return;
                    }

                    continue;
                }

                // 目标名被别的文件占着 ⇒ 这一卷跳过（绝不覆盖用户的数据）。
                if (File.Exists(targetPath) || Directory.Exists(targetPath))
                {
                    continue;
                }

                if (!targets.Add(targetPath))
                {
                    // 两个成员推出同一个名字 ⇒ 这一组认不清，整组不动。
                    return;
                }

                moves.Add((member, targetPath));
            }

            if (moves.Count == 0)
            {
                return;
            }

            var done = new List<(string From, string To)>();

            try
            {
                foreach ((string from, string to) in moves)
                {
                    File.Move(from, to);
                    done.Add((from, to));
                }

                // 全成或全不成：全部改完之后复查一遍（另一个进程可能刚好插手）。
                foreach ((_, string to) in done)
                {
                    if (!File.Exists(to))
                    {
                        throw new IOException($"改名后找不到目标：{Path.GetFileName(to)}");
                    }
                }
            }
            catch (Exception ex)
            {
                for (int i = done.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        File.Move(done[i].To, done[i].From);
                    }
                    catch
                    {
                        // 回滚失败如实写进下面那条 WARN 里，⛔ 不假装成功。
                    }
                }

                if (changes != null)
                {
                    changes.Add((
                        "WARN",
                        $"这一层有一组分卷的名字没能改回标准名（{ex.Message}），已尽量改回原名；"
                        + "这一组要打开得手工把名字改成标准卷名（末片 X.zip、首片 X.z01…）。"));
                }

                return;
            }

            if (changes != null)
            {
                changes.Add((
                    "INFO",
                    $"这一层有一组跨盘分卷的名字不是标准名，已改回标准名（{done.Count} 卷，只改名字、内容一个字节没动）："
                    + string.Join("、", done.Select(m => $"{Path.GetFileName(m.From)} → {Path.GetFileName(m.To)}"))));
            }

            /* 记下"刚改过名"的那几个新路径：它们**不许进可删的其余物**（它们是内容物，不是过程物）。 */
            if (renamedMembers != null)
            {
                /*
                 * 整组都记进来（不只是真被改名的那几卷）：调用方据此判"这一组是内容物、不许进可删的其余物"，
                 * 而本来就标准的那几卷（`一只顶美.z01`）同样是这一组的内容物，漏掉它们等于把它们删掉。
                 */
                foreach (string member in onDisk)
                {
                    renamedMembers.Add(SafePathHelper.GetFullPathSafe(member));
                }

                foreach ((_, string to) in done)
                {
                    renamedMembers.Add(SafePathHelper.GetFullPathSafe(to));
                }
            }
        }

        /// <summary>
        /// 这一卷按跨盘 zip 的族规则该叫什么。
        ///
        /// <para>基名就是**文件名去掉最后一段后缀**（`222.zi删除p` → `222`；`222.z0sc1` → `222`）——
        /// 与归档内部记的名字对不上时也只影响"改完像不像人写的"，能不能打开由"标准卷名"这一条决定。</para>
        ///
        /// <para>认不出来就返回 false ⇒ 调用方**整组不动**（宁可保持原样，也不猜着改用户的名字）。</para>
        /// </summary>
        private static bool TryResolveStandardVolumeName(string fileName, out string target)
        {
            target = string.Empty;

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            int dot = fileName.IndexOf('.');

            if (dot <= 0)
            {
                return false;
            }

            string stem = fileName[..dot];
            string last = fileName[(dot + 1)..];

            // 脏本体名（`222.zi删除p`：还原出来是 `zip`）⇒ 它就是末片 `.zip`。
            if (ExtensionHelper.TryRecoverDisguisedArchiveBody(last, out string suffix, out _)
                && suffix.Equals("zip", StringComparison.OrdinalIgnoreCase))
            {
                target = stem + ".zip";
                return true;
            }

            // 干净的末片。
            if (last.Equals("zip", StringComparison.OrdinalIgnoreCase))
            {
                target = stem + ".zip";
                return true;
            }

            // 卷标记（`z01` / `z0sc1` / `z删除01`）⇒ 一律按标准 `.zNN` 写。
            if (ExtensionHelper.TrySplitVolumeSegmentTolerant(last, out string mark, out _)
                && mark.Length == 3
                && (mark[0] == 'z' || mark[0] == 'Z')
                && int.TryParse(mark.AsSpan(1), out int ordinal)
                && ordinal >= 1)
            {
                target = stem + ".z" + ordinal.ToString("D2", CultureInfo.InvariantCulture);
                return true;
            }

            return false;
        }

        /// <summary>暂存树里的全部目录（含根；读不到就是空的 —— 什么都不做）。</summary>
        private static IEnumerable<string> EnumerateDirectoriesSafe(string root)
        {
            var result = new List<string> { root };

            try
            {
                result.AddRange(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories));
            }
            catch
            {
                // 读不全就按已经拿到的那些判：宁可少改，也绝不乱改。
            }

            return result;
        }

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
        /// **分卷组完整性确认**（用户 2026-09-30 真机铁证）：⛔ **只有确认整组完整且每一卷名字都标准，
        /// 分卷才允许进"可删的其余物"**。
        ///
        /// <para><b>为什么必须有它</b>：老判据只看"扩展名像不像分卷"，于是"缺首卷 / 缺末卷的残组"里
        /// 每一卷单看都像"待续解的过程物"，被打包收进可删的其余物；再叠加「空间不足」模式的永久删除，
        /// 用户那 25 GB 就是这么没的（日志还写着"解压成功 ｜ 校验通过"）。</para>
        ///
        /// <para><b>判据已经整体搬进唯一出口 <see cref="VolumeGroupResolver"/></b>（2026-09-30 分卷组装算法）：
        /// 这里只做一次"同目录问一问"，不再自己按扩展名拼判据 —— 老实现那五条（<c>.z01</c> 找同名 <c>.zip</c>、
        /// 纯数字找 <c>.001</c>、<c>.partNN</c> 找首卷…）现在全在判定器的证据表里，而且认得出
        /// "末卷被改名成 <c>一只顶美.z删除ip</c>"这种老判据必然漏掉的情形。</para>
        ///
        /// <para>⛔ 兜底不变：**确认不了（目录读不到、名字认不出、判不出）⇒ 一律按不完整处理** ——
        /// 兜底落在"什么都不做"那一档（AGENTS §9.5）。</para>
        /// </summary>
        internal static bool TryConfirmVolumeGroupComplete(string filePath, out string note)
        {
            note = string.Empty;

            VolumeGroupResolution resolution = new VolumeGroupResolver().Resolve(new VolumeGroupQuery
            {
                AnchorPath = filePath,
                AllowTrialOpen = false
            });

            if (resolution.Verdict == VolumeGroupVerdict.Undetermined)
            {
                note = $"{Path.GetFileName(filePath)}：{resolution.Reason}";
                return false;
            }

            if (resolution.CanEnterDeletableRestItems)
            {
                return true;
            }

            string missing = resolution.MissingVolumeNames.Count > 0
                ? "缺 " + string.Join("、", resolution.MissingVolumeNames)
                : resolution.Reason;

            note = $"{resolution.BaseName}：{missing}";
            return false;
        }

        /// <summary>一个暂存目录里的分卷组 + 它的判定结论（定稿闸门按文件查它）。</summary>
        internal sealed class StageVolumeGroupView
        {
            /// <summary>这一组的目录（绝对路径）。</summary>
            public string DirectoryPath { get; init; } = string.Empty;

            /// <summary>判定器给出的唯一结论。</summary>
            public VolumeGroupResolution Resolution { get; init; } = new();

            /// <summary>组的全部文件（绝对路径，含推定进来的候选）。</summary>
            public IReadOnlyList<string> FilePaths { get; init; } = Array.Empty<string>();
        }

        /// <summary>
        /// 暂存树的一次分卷扫描：**已经认出来的组** + **逐目录的事实清单**（后者给"孤儿卷"复查用）。
        ///
        /// <para>为什么要带着事实清单：带卷标记的文件**不一定**落进某个组 ——
        /// <see cref="VolumeGroupDetector"/> 对"名字被伪装过"的桶还要过一道尺寸规律，
        /// 过不了就把整桶丢掉（2026-09-28 三层方案，防认错组）。丢掉之后那些文件在
        /// <see cref="PlanFinalLayout"/> 里就只剩"按扩展名像不像分卷"这一条老判据了 ——
        /// 而它们恰恰可能是**缺了兄弟的那一组**。所以那一档必须回头问判定器（见调用点）。</para>
        /// </summary>
        internal sealed class StageVolumeScan
        {
            /// <summary>认出来的组（每组一条结论）。</summary>
            public IReadOnlyList<StageVolumeGroupView> Groups { get; init; } = Array.Empty<StageVolumeGroupView>();

            /// <summary>逐目录的事实清单（键 = 目录绝对路径）。</summary>
            public IReadOnlyDictionary<string, IReadOnlyList<VolumeGroupEntry>> EntriesByDirectory { get; init; } =
                new Dictionary<string, IReadOnlyList<VolumeGroupEntry>>(StringComparer.OrdinalIgnoreCase);

            /// <summary>这个文件的同目录事实清单（拿不到就返回空 —— 空清单在判定器里等于"判不出"）。</summary>
            public IReadOnlyList<VolumeGroupEntry> EntriesFor(string filePath)
            {
                try
                {
                    string? directory = Path.GetDirectoryName(filePath);

                    return !string.IsNullOrWhiteSpace(directory) &&
                           EntriesByDirectory.TryGetValue(directory, out IReadOnlyList<VolumeGroupEntry>? entries)
                        ? entries
                        : Array.Empty<VolumeGroupEntry>();
                }
                catch
                {
                    return Array.Empty<VolumeGroupEntry>();
                }
            }
        }

        /// <summary>
        /// 把暂存树里出现的每一组分卷判一次（**逐目录**判：判定器只在同目录里认组）。
        ///
        /// <para>调用时机 = 定稿闸门开工前一次（不是每个文件各判一次 —— 那是 O(n²) 的目录枚举）。
        /// 不做试开：这一步跑在同步的定稿路径上；试开那一档在解前预检里做，两边收的是**同一份结论类型**。</para>
        /// </summary>
        private static StageVolumeScan ResolveStageVolumeGroups(string stageRoot)
        {
            var result = new List<StageVolumeGroupView>();
            var byDirectory = new Dictionary<string, List<VolumeGroupEntry>>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in Directory.EnumerateFiles(stageRoot, "*", SearchOption.AllDirectories))
            {
                try
                {
                    string? directory = Path.GetDirectoryName(file);

                    if (string.IsNullOrWhiteSpace(directory))
                    {
                        continue;
                    }

                    if (!byDirectory.TryGetValue(directory, out List<VolumeGroupEntry>? list))
                    {
                        list = new List<VolumeGroupEntry>();
                        byDirectory[directory] = list;
                    }

                    list.Add(new VolumeGroupEntry
                    {
                        Path = SafePathHelper.GetFullPathSafe(file),
                        Size = new FileInfo(file).Length,
                        LastWriteTimeUtc = File.GetLastWriteTimeUtc(file),
                        OrderIndex = list.Count
                    });
                }
                catch
                {
                    // 单个文件量不出事实（被占用 / 权限）⇒ 它进不了任何组，照老路当内容物处理。
                }
            }

            var resolver = new VolumeGroupResolver();

            foreach (KeyValuePair<string, List<VolumeGroupEntry>> pair in byDirectory)
            {
                IReadOnlyList<VolumeGroupEntry> entries = pair.Value;

                var candidates = entries
                    .Select(e => new VolumeCandidate { Path = e.Path, Size = e.Size })
                    .ToList();

                foreach (VolumeGroup group in VolumeGroupDetector.Group(candidates))
                {
                    VolumeGroupResolution resolution = resolver.Resolve(new VolumeGroupQuery
                    {
                        AnchorPath = group.FirstVolumePath,
                        DirectoryEntries = entries,
                        AllowTrialOpen = false
                    });

                    result.Add(new StageVolumeGroupView
                    {
                        DirectoryPath = pair.Key,
                        Resolution = resolution,
                        FilePaths = resolution.GroupFilePaths
                    });
                }
            }

            return new StageVolumeScan
            {
                Groups = result,
                EntriesByDirectory = byDirectory.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<VolumeGroupEntry>)pair.Value,
                    StringComparer.OrdinalIgnoreCase)
            };
        }

        /// <summary>这个文件属于哪一组（不属于任何组返回 null）。判定器认进来的推定候选也算。</summary>
        private static StageVolumeGroupView? FindStageVolumeGroup(
            IReadOnlyList<StageVolumeGroupView> groups,
            string filePath)
        {
            string full = SafePathHelper.GetFullPathSafe(filePath);

            foreach (StageVolumeGroupView group in groups)
            {
                foreach (string member in group.FilePaths)
                {
                    if (string.Equals(member, full, StringComparison.OrdinalIgnoreCase))
                    {
                        return group;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 执行定稿：按 <see cref="PlanFinalLayout"/> 的计划把暂存产物搬进最终目录。
        ///
        /// **只允许在后台线程上跑**（几千个 File.Move、跨盘还是拷贝）。
        /// 四条硬要求：
        /// ① 取消时把已经搬过去的**搬回来**（最终目录不许留半成品，契约 §6 第 2 条）；
        /// ② 同名绝不**默认**覆盖（AutoRename 改名，记进 <see cref="StageCommitResult.RenamedCount"/>）；
        /// ③ 单个文件搬不动只记一笔，不拖垮其余文件（与 <see cref="ExtractionWorkspace"/> 的搬运口径一致）；
        /// ④ 用户明确选了「覆盖」时才覆盖，而且必须走 <see cref="PathService.TryOverwriteLanding"/>
        ///    的两阶段（先挪到临时名 → 落位 → 再删），并逐个落点写日志留痕。
        /// </summary>
        /// <param name="conflictAction">同名冲突档（<see cref="ConflictActions"/> 的值）。</param>
        /// <param name="conflictDecision">
        /// 用户对同名冲突的答案（Ask 档；非 Ask 档为 null）。为 null 且档位是 Ask 时，
        /// <see cref="PathService.ResolveConflict"/> 会退回保守档（跳过）—— 绝不替用户决定成覆盖。
        /// </param>
        private StageCommitResult ExecuteFinalLayout(
            ArchiveTask task,
            string stageDirectory,
            string destinationDirectory,
            bool sharedOutputRoot,
            TerminalLayoutMode terminalLayout,
            string conflictAction,
            ConflictDecision? conflictDecision,
            CancellationToken cancellationToken,
            bool verificationPassed = true,
            SpecialExtractionPlan? specialExtraction = null,
            RecursionResult? recursion = null)
        {
            var logEntries = new List<(string Level, string Message)>();

            if (string.IsNullOrWhiteSpace(stageDirectory) ||
                string.IsNullOrWhiteSpace(destinationDirectory) ||
                !Directory.Exists(stageDirectory))
            {
                return new StageCommitResult { Attempted = false };
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new StageCommitResult
                {
                    Attempted = true,
                    Cancelled = true,
                    Message = "定稿开始前已取消"
                };
            }

            FinalLayoutPlan plan;

            try
            {
                plan = PlanFinalLayoutForTask(
                    task,
                    stageDirectory,
                    destinationDirectory,
                    sharedOutputRoot,
                    terminalLayout,
                    specialExtraction,
                    recursion);
            }
            catch (Exception ex)
            {
                string readFailure = $"读取暂存目录失败：{ex.Message}";

                logEntries.Add(("ERROR", $"{task.LogName}：{readFailure}"));

                return new StageCommitResult
                {
                    Attempted = true,
                    FailedCount = 1,
                    Failures = new[] { readFailure },
                    Message = readFailure,
                    LogEntries = logEntries
                };
            }

            if (plan.Failed)
            {
                /*
                 * ⚠ 2026-10-09（用户点名第二条红字）：**"分卷组不完整"这一档不是失败、也不写 ERROR**。
                 * 用户口径：「连完整的都没有，这就不是程序的错误」＋「在最后一步之前碰到分卷缺失的就先跳过」。
                 * 判据读**结构化标志**（`plan.BlockedByIncompleteVolumeGroup`），⛔ 不比中文文案；
                 * ⛔ "什么都不动"一个字没放松（照旧不定稿、不搬、不删）。
                 */
                string planFailure = plan.BlockedByIncompleteVolumeGroup
                    ? "这一层里有分卷组还没凑齐（先跳过，等最后那一轮再判）：" + plan.FailureReason
                    : "定稿布局规划失败：" + plan.FailureReason;

                logEntries.Add((
                    plan.BlockedByIncompleteVolumeGroup ? "WARN" : "ERROR",
                    $"{task.LogName}：{planFailure}"));

                return new StageCommitResult
                {
                    Attempted = true,
                    FailedCount = 1,
                    Failures = new[] { planFailure },
                    Message = planFailure,
                    LogEntries = logEntries
                };
            }

            foreach (string warning in plan.Warnings)
            {
                logEntries.Add(("WARN", $"{task.LogName}：定稿提醒 —— {warning}"));
            }

            if (plan.Moves.Count == 0)
            {
                /*
                 * 引擎什么都没写出来（空包）：没有可定稿的东西，也不该凭空造一个空目录给用户。
                 *
                 * ⚠ "只有 0 字节产物"要单独说清楚（用户 2026-09-24 要求）：它看起来也是"没有产物"，
                 * 但成因完全不同（密码不对 / 数据不全时 7z 会写 0 字节的桩文件），
                 * 写成"这个包本来就是空的"会把人引到错的方向 —— 那句话必须带上"校验未通过，产物视为无效"。
                 */
                string emptyMessage = plan.ZeroByteProductsOnly
                    ? "暂存目录里只有 0 字节产物 —— 校验未通过，产物视为无效：不定稿、不生成其余物、源包留在原地。"
                    : "暂存目录里没有产物，没有需要定稿的内容。";

                logEntries.Add(("WARN", $"{task.LogName}：{emptyMessage}"));

                return new StageCommitResult
                {
                    Attempted = true,
                    Message = emptyMessage,
                    LogEntries = logEntries
                };
            }

            bool destinationExisted = Directory.Exists(destinationDirectory);

            if (!SafePathHelper.EnsureDirectoryExists(destinationDirectory))
            {
                string createFailure = $"最终目录不存在且创建失败：{destinationDirectory}";

                logEntries.Add(("ERROR", $"{task.LogName}：{createFailure}"));

                return new StageCommitResult
                {
                    Attempted = true,
                    FailedCount = plan.Moves.Count,
                    Failures = new[] { createFailure },
                    Message = createFailure,
                    LogEntries = logEntries
                };
            }

            var moved = new List<(string From, string To)>();
            var failures = new List<string>();
            var overwritten = new List<string>();

            int movedContent = 0;
            int movedProcess = 0;
            int renamed = 0;
            int skippedContent = 0;
            int skippedProcess = 0;

            // 计划里 **内容物在前、其余物在后**：先让用户要的东西落位，再收拾过程物。
            foreach (PlannedMove move in plan.Moves)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    RollBackFinalLayout(moved, destinationDirectory, destinationExisted);

                    logEntries.Add(("WARN", $"{task.LogName}：定稿被取消，已把 {moved.Count} 项退回暂存目录。"));

                    return new StageCommitResult
                    {
                        Attempted = true,
                        Cancelled = true,
                        Message = "定稿被取消，产物已退回暂存目录",
                        LogEntries = logEntries
                    };
                }

                bool isProcessArtifact = plan.ProcessArtifactSources.Contains(move.From);

                try
                {
                    // 计划里的一条可能是**文件**，也可能是**整个目录**（判定表 2/3/4 会整棵搬）。
                    bool isDirectory = Directory.Exists(move.From) && !File.Exists(move.From);

                    /*
                     * ⚠ 2026-10-09 真机 `EEEE`（用户最在意的那条「为什么有两个 `111.zip`」）：
                     * **目标位上已经是同一份物理文件 ⇒ 不必再搬**（⛔ 不删、⛔ 不改名，直接跳过这一条）。
                     *
                     * 现场（这一条**两个改动缺一不可**：上面把"本链产出"判成内容物之后，
                     * 它就会来定稿落地，于是撞上这个形状）：接片给"**本单自己暂存目录**"里那一份
                     * **建的是硬链接**（`ShouldMovePieceIntoGroup` 那一档只建链接、绝不搬走，
                     * 真机行 146 的 `moveIntoGroup=False` 就是它）⇒ `111\111\111.zip` 与暂存里的
                     * `111.zip` **是同一个 FileId**。定稿再搬一次，只会在同一个目录里多出一个
                     * `111(1).zip` —— 用户看到的"两个"就从"其余物里一个"变成"`(1)` 一个，症状根本不消失。
                     *
                     * 判据只读事实（唯一出口 <see cref="FileIdentity.IsSamePhysicalFile"/>：卷序列号 + 文件索引），
                     * ⛔ 不比名字、⛔ 不看大小。**一个字节都不动**：数据本来就已经在目标位上（同一条记录）。
                     */
                    if (!isDirectory && FileIdentity.IsSamePhysicalFile(move.From, move.To))
                    {
                        logEntries.Add((
                            "INFO",
                            $"{task.LogName}：{Path.GetFileName(move.To)} 已经在目标位上、而且是「同一份文件」"
                            + "（接片时建的硬链接）—— 不必再搬一次，也不留多余的名字。"));

                        continue;
                    }

                    /*
                     * ⚠ 2026-10-09 真机 `EEEE` 的另一半（同一个"两个 `111.zip`"的另一形状）：
                     * **这一片已经由接片搬进"这一组的落点层"了 ⇒ 产出方自己落点里这一份是多余名**。
                     *
                     * 与上面那一条的区别（真机行 161 实测）：那一档 `moveIntoGroup=True` ⇒ 接片对
                     * 层产物用的是**移动**（不是建链接），所以两边**不是同一个 FileId**，
                     * `IsSamePhysicalFile` 判不出来 —— 只能按"组层是不是已经有这个名字"判。
                     *
                     * 判据两条事实，缺一不可：① 这一份是**分卷片**（`IsVolumePartFile`）；
                     * ② 这一组的**落点层**（唯一出口 `ResolveBatchGroupDirectory`）里已经有同名的一份。
                     * ⇒ 那才是它的正位（整组在那一层拼、在那一层解），产出方落点里这一份就是
                     * 用户点名过的"没人认领的名字"（`SiblingFolderVolumeGatherTests` ③ 那条用例钉的就是它）。
                     * ⛔ 数据一个字节都不丢：正位那一份已经在了。
                     */
                    if (!isDirectory && IsVolumePartFile(move.From))
                    {
                        string pieceName = Path.GetFileName(move.From);

                        /*
                         * ⚠ **读"接片那一刻定下来的层"，⛔ 不在这里重算**（2026-10-11 实测）：
                         * 这一档原来转调 `ResolveBatchGroupDirectory` 的 1 参版，而它当年靠"先到先得"那条
                         * 兜底才答得出层；兜底按用户口径删掉之后它答不出 ⇒ 这道闸门**静默失效**
                         * ⇒ 本单的工作区那一份又被发布到产出方落点里，成了**没人认领的多余名**
                         * （实测残留逐字 `…\out-…\111(2)_\111.z01`；守门用例
                         * `SiblingFolderVolumeGatherTests.真机形状_过路层那一单的其余物按档删掉_48MB那处`
                         * 第 ③ 条断言「盘上收口：只剩成品本身」钉的就是它）。
                         * 层只有一个出口：接片那一档算过的那个（`_groupLayerByBaseName`）。
                         */
                        string groupLayer = _groupLayerByBaseName.TryGetValue(
                            FileNameHelper.GetArchiveBaseName(pieceName),
                            out string? rememberedLayer)
                            ? rememberedLayer
                            : string.Empty;

                        if (groupLayer.Length > 0 && File.Exists(Path.Combine(groupLayer, pieceName)))
                        {
                            logEntries.Add((
                                "INFO",
                                $"{task.LogName}：{pieceName} 已经由接片搬进「{Path.GetFileName(groupLayer)}」"
                                + "这一组的落点层了 —— 产出方这里这一份是多余的名字，不落地（一个字节都不丢）。"));

                            continue;
                        }
                    }

                    if (!SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(move.To)))
                    {
                        failures.Add($"{Path.GetFileName(move.To)}（无法创建目标目录）");
                        continue;
                    }

                    string target = move.To;

                    // 覆盖档由 TryOverwriteLanding 自己把新条目搬过去（它要先腾位），这里不再搬第二次。
                    bool landed = false;

                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        /*
                         * 目标是**最终目录自己**时不许覆盖：那等于把刚落位的产物连同目录一起顶掉，
                         * 而且是用户完全没预期的一次删除。规划器现在不会给出这种条目
                         * （计划里永远是 destDir\子项），这里是最后一道保险。
                         */
                        bool isDestinationRoot = SafePathHelper.PathEquals(target, destinationDirectory);

                        ConflictResolution resolution = isDestinationRoot
                            ? new ConflictResolution(ConflictChoice.AutoRename, target, true, false)
                            : _pathService.ResolveConflict(
                                target,
                                isDirectory ? ConflictTargetKind.Directory : ConflictTargetKind.File,
                                conflictAction,
                                conflictDecision);

                        if (resolution.Choice == ConflictChoice.Skip)
                        {
                            // 「跳过」：这一项不动，已有文件一个字节都不改（新产物留在暂存目录）。
                            if (isProcessArtifact)
                            {
                                skippedProcess++;
                            }
                            else
                            {
                                skippedContent++;
                            }

                            logEntries.Add(("WARN", $"{task.LogName}：同名冲突，按你的选择跳过：{target}"));
                            continue;
                        }

                        if (resolution.Choice == ConflictChoice.Overwrite && !isDestinationRoot)
                        {
                            /*
                             * 「覆盖」= 两阶段落位（AGENTS.md §6 第 3 条）：
                             * 先把占位者挪到临时名 → 新条目落位 → 落位成功后才删掉那个临时名。
                             * **禁止"先 File.Delete 再 Move"**：中间任何失败都会让用户想保留的那一份
                             * 永久消失，而新条目还没到位。
                             */
                            if (!_pathService.TryOverwriteLanding(
                                    move.From,
                                    target,
                                    isDirectory,
                                    out string overwriteError,
                                    out string overwriteNote))
                            {
                                failures.Add($"{Path.GetFileName(move.From)}（{overwriteError}）");
                                continue;
                            }

                            // 真正覆盖前要留痕：哪个落点被顶掉了必须写进日志（AGENTS.md §6 第 3 条 / 用户要求）。
                            overwritten.Add(target);
                            landed = true;

                            logEntries.Add((
                                "WARN",
                                $"{task.LogName}：同名冲突，按你的选择覆盖：{target}（{overwriteNote}）"));
                        }
                        else
                        {
                            // 自动重命名：换成 名字(1)，**绝不覆盖**（AGENTS.md §6 第 3 条）。
                            string renamedTarget = resolution.TargetPath;

                            if (!string.Equals(renamedTarget, target, StringComparison.OrdinalIgnoreCase))
                            {
                                renamed++;
                            }

                            logEntries.Add(("INFO", $"{task.LogName}：同名冲突，改名落位（绝不覆盖）：{renamedTarget}"));

                            target = renamedTarget;
                        }
                    }

                    if (!landed)
                    {
                        if (isDirectory)
                        {
                            Directory.Move(move.From, target);
                        }
                        else
                        {
                            File.Move(move.From, target);
                        }
                    }

                    moved.Add((move.From, target));

                    if (isProcessArtifact)
                    {
                        movedProcess++;
                    }
                    else
                    {
                        movedContent++;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(move.From)}（{ex.Message}）");
                }
            }

            /*
             * 抠出来的内嵌归档：它是从源文件按偏移再生出来的派生数据，契约 §3.2 把它列为其余物。
             * 它**不在暂存区里**（在任务工作区根下，是解压的输入），所以不在上面的计划里，单独搬一次。
             * 为什么默认不搬：见 MoveCarvedArtifactIntoProcessDirectory 的说明。
             */
            if (MoveCarvedArtifactIntoProcessDirectory && task.EmbeddedArchiveOffset > 0)
            {
                string carvedPath = BuildEmbeddedArchivePath(task);

                if (File.Exists(carvedPath))
                {
                    try
                    {
                        string processRoot = string.IsNullOrWhiteSpace(plan.ProcessArtifactDirectory)
                            ? Path.Combine(destinationDirectory, ProcessArtifactDirectoryName)
                            : plan.ProcessArtifactDirectory;

                        string carvedTarget = Path.Combine(processRoot, Path.GetFileName(carvedPath));

                        SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(carvedTarget));

                        if (File.Exists(carvedTarget))
                        {
                            carvedTarget = SafePathHelper.AutoRenameFilePath(carvedTarget);
                            renamed++;
                        }

                        File.Move(carvedPath, carvedTarget);

                        movedProcess++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{Path.GetFileName(carvedPath)}（抠出的中间归档：{ex.Message}）");
                    }
                }
            }

            string processDirectory = string.IsNullOrWhiteSpace(plan.ProcessArtifactDirectory)
                ? Path.Combine(destinationDirectory, ProcessArtifactDirectoryName)
                : plan.ProcessArtifactDirectory;

            /*
             * 定稿摘要 —— **"没能搬运"那几条必须带上明细**（用户 2026-10-04 真机）。
             *
             * 现场：`老王.apk` 那一单写着「定稿搬运失败，产物没能写进输出目录：…；10 项没能搬运」，
             * ①页「错误信息」列与日志里都只有这一个数字 —— 那 10 条 `文件名（原因原文）` 在收集处
             * （上面的 `failures`）就已经拿到了，却在 CommitFailed 的提前 return 那条路上被丢掉，
             * 用户手里没有任何可行动的线索（而批末诊断还指着他去看"引擎原话"，定稿类失败没有引擎原话）。
             *
             * 为什么补在摘要里而不是另写一行：摘要这一份字符串**同时**是日志那一行
             * （"定稿完成 —— …"）与①页「错误信息」列（`StageCommitResult.Message` →
             * `CommitFailureMessage`）的来源 ⇒ 一处出口、两个消费点，不会各写一份、也不会对不上。
             * ⛔ 判据一个都没动：这里只把**已经收集到的**事实说出来。
             */
            string summary =
                $"内容物 {plan.ContentFileCount} 个文件 → {destinationDirectory}" +
                (movedProcess > 0
                    ? $"；其余物 {movedProcess} 项（{plan.ProcessArtifactTotalSize} 字节）→ {processDirectory}"
                    : string.Empty) +
                (renamed > 0 ? $"；{renamed} 项同名，已改名未覆盖" : string.Empty) +
                (overwritten.Count > 0 ? $"；{overwritten.Count} 项同名，按你的选择覆盖（已写日志）" : string.Empty) +
                (skippedContent + skippedProcess > 0 ? $"；{skippedContent + skippedProcess} 项同名，按你的选择跳过" : string.Empty) +
                (failures.Count > 0 ? $"；{failures.Count} 项没能搬运" : string.Empty) +
                (failures.Count > 0
                    ? string.Format(
                        CultureInfo.CurrentCulture,
                        StatusText.FinalizeMoveFailureDetailFormat,
                        MaxFinalizeContentNameLines,
                        DescribeFinalizeMoveFailures(failures))
                    : string.Empty);

            /*
             * 「定稿完成」这句话**不许在"没解出东西"时读成正常完成**（用户 2026-09-24 要求）。
             *
             * 现场误读：日志写着"定稿完成 —— 内容物 0 个文件 → …；其余物 2 项（0 字节）→ …"，
             * 而实际上校验早已判否、那 2 项是 0 字节垃圾 —— 用户看到"定稿完成"就以为流程正常走完了。
             * 现在两种情况各挂一句明确的结论：
             * · 校验未通过 → "⚠ 校验未通过，产物视为无效"；
             * · 校验通过但一个内容物都没有 → 如实说"这次没有内容物"（那是合法的，例如只出过程物）。
             */
            string contentNote = plan.ContentFileCount > 0
                ? string.Empty
                : verificationPassed
                    ? "（⚠ 这次没有内容物，只有其余物）"
                    : "（⚠ 校验未通过，产物视为无效；不定稿有效内容、不搬源包）";

            logEntries.Add((failures.Count == 0 ? "INFO" : "WARN", $"{task.LogName}：定稿完成{contentNote} —— {summary}"));

            /*
             * 产物**点名**（用户 2026-09-25："出问题我看日志就能知道"）。
             *
             * 为什么必须列名字：续解那一步是按"这一轮新出现的文件"找下一层的，而名字被改坏的产物
             * （真机上是 `222.ra删除r`）在日志里如果只报个数，用户就只能自己去翻目录 —— 那次就是这么绕远的。
             * 上限 20 条，超出只报个数（几千个文件时不能让日志爆掉），但"还有多少没列"必须写出来。
             */
            if (plan.Moves.Count > 0)
            {
                List<PlannedMove> contentMoves = plan.Moves
                    .Where(move => !plan.ProcessArtifactSources.Contains(move.From))
                    .ToList();

                List<string> contentNames = contentMoves
                    .Select(move => Path.GetFileName(move.To))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Take(MaxFinalizeContentNameLines)
                    .ToList();

                if (contentNames.Count > 0)
                {
                    logEntries.Add((
                        "INFO",
                        $"{task.LogName}：本次内容物 —— {string.Join("、", contentNames)}"
                        + (contentMoves.Count > contentNames.Count
                            ? $"（…还有 {contentMoves.Count - contentNames.Count} 项没列出来）"
                            : string.Empty)));
                }
            }

            // 覆盖留痕：被顶掉的落点单独再写一条，用户事后追查"我原来那份去哪了"时有据可依。
            foreach (string overwrittenPath in overwritten)
            {
                logEntries.Add(("WARN", $"{task.LogName}：已覆盖（原文件按两阶段落位删除）：{overwrittenPath}"));
            }

            return new StageCommitResult
            {
                Attempted = true,
                PlannedContentCount = plan.PlannedContentCount,
                MovedContentCount = movedContent,
                MovedProcessCount = movedProcess,
                ContentFileCount = plan.ContentFileCount,
                ProcessArtifactDirectory = processDirectory,
                ContentDirectory = ResolveContentLayerDirectory(destinationDirectory, processDirectory),
                ProcessArtifactBytes = plan.ProcessArtifactTotalSize,
                RenamedCount = renamed,
                SkippedCount = skippedContent + skippedProcess,
                SkippedContentCount = skippedContent,
                OverwrittenCount = overwritten.Count,
                OverwrittenPaths = overwritten,
                FailedCount = failures.Count,
                Failures = failures,
                Message = summary,
                LogEntries = logEntries
            };
        }

        /// <summary>
        /// 定稿**没能搬运**的那几条明细（<c>文件名（原因原文）</c>），最多前
        /// <see cref="MaxFinalizeContentNameLines"/> 条，多出来的折成"还有 K 项没列出来"。
        ///
        /// <para>为什么是 5 条：与「本次内容物 —— 名字、名字…」同一个理由（前 5 个足够定性，
        /// 几十个同名文件全列出来只会把日志与①页那一格撑爆）。</para>
        ///
        /// <para>⛔ 这里一个字都不改判据、也不重算原因：明细就是 <c>failures</c> 里已经收好的那一份
        /// （来源：`无法创建目标目录` / 两阶段覆盖的失败原因 / `File.Move` / `Directory.Move` 的异常原文）。</para>
        /// </summary>
        private static string DescribeFinalizeMoveFailures(IReadOnlyList<string> failures)
        {
            List<string> shown = failures.Take(MaxFinalizeContentNameLines).ToList();
            string text = string.Join("、", shown);

            if (failures.Count <= shown.Count)
            {
                return text;
            }

            return text + string.Format(
                CultureInfo.CurrentCulture,
                StatusText.FinalizeMoveFailureHiddenFormat,
                failures.Count - shown.Count);
        }

        /// <summary>
        /// 定稿的回滚：把已经搬过去的条目按**相反顺序**搬回暂存区原路径。
        ///
        /// 为什么必须回滚（契约 §6 第 2 条"定稿前 destDir 不得出现半成品"）：
        /// 用户按下「取消当前」时，搬运可能已经做了一半 —— 只把最终目录里那一半留下，
        /// 用户看到的就是一个"解压到一半"的目录，正是这次改造要根治的观感问题。
        /// 回滚失败（文件被别的程序锁住等）只吞掉：那是更小的问题，而且日志里已经有取消记录。
        ///
        /// 条目可能是**整个目录**（判定表 2/3/4 会整棵搬），所以这里也要按类型分开搬回去。
        /// </summary>
        private static void RollBackFinalLayout(
            List<(string From, string To)> moved,
            string destinationDirectory,
            bool destinationExisted)
        {
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                try
                {
                    SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(moved[i].From));

                    if (Directory.Exists(moved[i].To) && !File.Exists(moved[i].To))
                    {
                        Directory.Move(moved[i].To, moved[i].From);
                    }
                    else
                    {
                        File.Move(moved[i].To, moved[i].From);
                    }
                }
                catch
                {
                    // 回滚不掉的单个条目不影响"已经尽量退回"这个结论。
                }
            }

            if (destinationExisted)
            {
                // 目录本来就在（续解内层包时它装着上一轮的内容物），绝不能删。
                return;
            }

            TryDeleteEmptyDirectoryTree(destinationDirectory);
        }

        /// <summary>由深到浅删掉空目录（只删空的；非空一律不碰）。定稿回滚后收拾自己造出来的目录壳。</summary>
        private static void TryDeleteEmptyDirectoryTree(string root)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    return;
                }

                string[] directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);

                foreach (string directory in directories.OrderByDescending(d => d.Length))
                {
                    try
                    {
                        if (Directory.GetFileSystemEntries(directory).Length == 0)
                        {
                            Directory.Delete(directory, recursive: false);
                        }
                    }
                    catch
                    {
                        // 删不掉就留着，不影响结论。
                    }
                }

                if (Directory.GetFileSystemEntries(root).Length == 0)
                {
                    Directory.Delete(root, recursive: false);
                }
            }
            catch
            {
                // 见方法注释：收拾目录壳是尽力而为。
            }
        }

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
            TaskProgressSink recursionProgressSink = new(this, task);

            RecursionResult result = await recursiveExtractor.ExtractAsync(
                recursionTask,
                stageDirectory,
                previousDecision: null,
                cancellationToken,
                recursionProgressSink,
                notice => OnEngineStalled(task, notice));

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
                        notice => OnEngineStalled(task, notice));
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
