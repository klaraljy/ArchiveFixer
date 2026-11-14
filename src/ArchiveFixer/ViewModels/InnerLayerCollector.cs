using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 一轮产物的内层包收集：扫本层产物目录 → 按内容认归档 → 排除工作区/过程物/后续卷 →
/// 算出每个候选的父层落点。
///
/// <para>为什么单独成类：这一段只读盘上事实与任务账，与"跑几轮、什么时候入队"无关；
/// 且它是整条一键处理里唯一会"逐个文件按内容嗅探"的地方，独立出来才好审查性能与跳过判据。</para>
/// </summary>
internal sealed class InnerLayerCollector
{
    private readonly MainViewModel _vm;
    private readonly ExtractionCoordinator _extractionCoordinator;
    private readonly ArchiveDetectService _detectService = new();

    /// <summary>
    /// 已经判成"内容物"的产出文件缓存：同一路径 + 大小 + 修改时间不再打开。
    /// 轮次扫描/批末确认读到它就直接跳过，避免图片、视频在每一轮被重复嗅探。
    /// </summary>
    private readonly Dictionary<string, (long Length, long Ticks, string How)> _terminalProducedContent =
        new(StringComparer.OrdinalIgnoreCase);

    internal InnerLayerCollector(MainViewModel vm, ExtractionCoordinator extractionCoordinator)
    {
        _vm = vm;
        _extractionCoordinator = extractionCoordinator;
    }

    private ObservableCollection<ArchiveTask> Tasks => _vm.Tasks;

    private AppSettings Settings => _vm.Settings;

    private bool TryGetTerminalProducedContent(string file, out string how)
    {
        how = string.Empty;

        try
        {
            var info = new FileInfo(file);

            if (!info.Exists)
            {
                return false;
            }

            if (_terminalProducedContent.TryGetValue(file, out (long Length, long Ticks, string How) fact)
                && fact.Length == info.Length
                && fact.Ticks == info.LastWriteTimeUtc.Ticks)
            {
                how = fact.How;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private void RememberTerminalProducedContent(string file, string how)
    {
        try
        {
            var info = new FileInfo(file);

            if (info.Exists)
            {
                _terminalProducedContent[file] = (info.Length, info.LastWriteTimeUtc.Ticks, how);
            }
        }
        catch
        {
        }
    }

    private static bool IsVolumeGroupEntryInPlace(string file)
    {
        string name = FileNameHelper.GetFileName(file);

        if (LegacyVolumeNames.TryGetIndex(name) != 1)
        {
            return false;
        }

        string directory = Path.GetDirectoryName(file) ?? string.Empty;

        if (directory.Length == 0)
        {
            return false;
        }

        try
        {
            foreach (string other in Directory.EnumerateFiles(directory))
            {
                if (!string.Equals(other, file, StringComparison.OrdinalIgnoreCase)
                    && LegacyVolumeNames.BelongsToSameGroup(
                        FileNameHelper.GetFileName(other),
                        name))
                {
                    return true;
                }
            }
        }
        catch
        {
            // 目录读不动 ⇒ 判不出 ⇒ 照旧走"按内容"那条路（什么都不多做）。
        }

        return false;
    }

    internal async Task<(List<InnerArchiveCandidate> Candidates, List<string> LogLines)> CollectAsync(
        int round,
        IReadOnlyList<ArchiveTask> roundTargets,
        HashSet<string> existingFiles,
        HashSet<string> unreadableDirectories,
        HashSet<string> sourcePaths)
    {
        // 已经在任务列表里的文件不重复加（去重）。
        var knownTaskPaths = new HashSet<string>(
            Tasks.Select(t => t.CurrentPath).Where(p => !string.IsNullOrWhiteSpace(p)),
            StringComparer.OrdinalIgnoreCase);

        /*
         * task.OutputPath / task.CollectedPath 是解压管线回写的**实际落点**（唯一权威来源），
         * 不是"打算输出到哪"。所以这里无条件按它去找内层包：
         * 目录已经存在时它可能是自动改名后的 xxx(1)，绝不是快照里那个原定目录。
         * 是不是"这一轮新产物"由下面的 existingFiles 继续把关 ——
         * 解压前就有的文件会被过滤掉，不会把用户自己放在旁边的包重新解一遍。
         */
        var parents = new List<(ArchiveTask Task, string OutputDirectory)>();
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ArchiveTask task in roundTargets)
        {
            /*
             * 只有**产物校验通过**的任务才有"产物"可言（用户 2026-09-24 要求：判据只准看事实）。
             *
             * 老写法是 `IsSuccessStatus(task)`，也就是拿 `task.Status == "解压成功"` 这个**字符串**去决定
             * "要不要继续往下解"。真机日志里恰好出现过"状态写着解压成功、校验却已判否"的那一刻 ——
             * 那种情况下续解链会在两个 0 字节的桩文件上继续解两层，全程在解空气。
             * 现在读的是校验那一刻的事实；失败/取消的任务在这里天然被排除（它们的结论不是 Passed）。
             */
            if (task.OutputVerification != OutputVerificationOutcome.Passed)
            {
                continue;
            }

            foreach (string directory in InnerLayerCandidateClassifier.CandidateDirectories(task))
            {
                /*
                 * ⚠ 同一个目录只扫一次（2026-09-25 第 35 条修的真机日志噪声）：
                 * 共用输出根那一档下 4 个任务指向同一个目录，逐个任务扫就把同一批文件数了 4 遍 ——
                 * 日志里出现"新文件 16 个"（实际 4 个）和同一条"认出…"重复 4 行，纯噪声。
                 */
                if (!string.IsNullOrWhiteSpace(directory) &&
                    !unreadableDirectories.Contains(directory) &&
                    seenDirectories.Add(directory))
                {
                    parents.Add((task, directory));
                }
            }
        }

        if (parents.Count == 0)
        {
            /*
             * ⛔ 这一档原来**静默早退**（连"扫描到底跑没跑"都看不出来 —— 真机 EEE 就是这样：
             * 第 2 轮日志里只有「第 2 层没有发现可继续解压的内层包」，没有任何扫描行，
             * 于是分不清是"没有候选"还是"扫描根本没跑"）。
             *
             * 按项目那条规矩（判不出就把**判据输入**写进日志）补一行：每一单为什么没交出目录 ——
             * 产物校验过没过 / 落点与归集那两格是空还是有值。⛔ 只写程序当场算出来的值，不改任何判据。
             */
            var why = new List<string>(roundTargets.Count);

            foreach (ArchiveTask candidateTask in roundTargets)
            {
                string outputPath = candidateTask.OutputPath ?? string.Empty;
                string collectedPath = candidateTask.CollectedPath ?? string.Empty;

                why.Add(
                    $"{candidateTask.LogName}（校验={candidateTask.OutputVerification}，"
                    + $"落点={(outputPath.Length > 0 ? outputPath : "空")}，"
                    + $"归集={(collectedPath.Length > 0 ? collectedPath : "空")}）");
            }

            return (new List<InnerArchiveCandidate>(), new List<string>
            {
                $"[WARN] 第 {round} 层产物扫描：这一轮一个可扫的产物目录都没有（{roundTargets.Count} 单）—— "
                + string.Join("；", why)
            });
        }

        /*
         * 轮次输入账本：本轮成功任务的定稿计划已经登记了实际落地的内容物。
         * 全部父任务都有账本 ⇒ 下一轮只吃这些路径，不扫输出目录树；
         * 有一个任务没有账本（旧任务 / 定稿未走计划）才回落到目录扫描。
         */
        bool useProducedLedger = parents.Count > 0
            && parents.All(parent => parent.Task.ProducedFiles.Count > 0);

        List<(string OutputDirectory, List<string> Files)> scanned;

        if (useProducedLedger)
        {
            string workRoot = _vm.CurrentWorkspaceRoot;

            scanned = parents
                .Select(parent => (
                    parent.OutputDirectory,
                    parent.Task.ProducedFiles
                        .Where(File.Exists)
                        .Where(file => !WorkspaceTree.IsInsideWorkspace(file, workRoot))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()))
                .ToList();
        }
        else
        {
            scanned = await Task.Run(() =>
            {
                var results = new List<(string, List<string>)>();

                // 扫描发生在后台线程上，先把当前生效的工作区根读出来（读的是批首定好的那一个）。
                string workRoot = _vm.CurrentWorkspaceRoot;

                foreach ((ArchiveTask _, string outputDirectory) in parents)
                {
                    if (!Directory.Exists(outputDirectory))
                    {
                        continue;
                    }

                    try
                    {
                        /*
                         * ⛔ 第 N 层产物扫描**必须排除工作区自己那棵树**（用户 2026-09-30）：
                         * 工作区默认就建在目标目录里面（<目标目录>\.ArchiveFixer.work），
                         * 里面躺着抠出来的内嵌归档副本、逐层的中间包 —— 它们**全都能被识别成归档**。
                         * 不排除的话，程序会把工作区里的中间产物当成"用户的内层包"继续解，
                         * 解完再把它们当内容物搬出去：不可逆的事故。
                         *
                         * ⚠ 「其余物」**不许整棵排除**（2026-10-05 实测）：默认档（只解当前这一层 +
                         * 一键处理续解）下，上一层产出的内层包正是**收进其余物之后**才被这一轮扫描认出来、
                         * 当下一个任务接着解的 —— 排掉整棵树等于把续解链砍掉
                         * （`InnerLayerContinuationTests` / `ChainManifestCompletenessTests` 当场 18 条红）。
                         * 真机那 857 vs 855 的噪声改由 <see cref="ShouldScanProducedFile"/> 那一处收口。
                         */
                        results.Add((outputDirectory, WorkspaceTree.EnumerateFiles(outputDirectory, workRoot).ToList()));
                    }
                    catch
                    {
                        // 读不了这个目录：跳过（快照阶段已经写过一条 WARN，不重复打扰用户）。
                    }
                }

                return results;
            });
        }

        var found = new Dictionary<string, InnerArchiveCandidate>(StringComparer.OrdinalIgnoreCase);

        /*
         * 这一轮认出来的内层包**按盘上物理身份去重**（2026-10-18，`8888` 现场）：
         * 同一份文件的两条名字（接片建的硬链接 / 同目录副本）只允许建一单 —— 见下面循环里那段。
         */
        var seenInnerPackageIdentities = new Dictionary<string, string>(StringComparer.Ordinal);

        // 本轮按父任务登记的候选（父任务、父任务名、内层包路径）—— 落点要等扫完一整轮才能定。
        var candidates = new List<(ArchiveTask Parent, string ParentName, string File)>();

        var scanLog = new List<string>();
        var recognizedLines = new List<string>();
        var skippedLines = new List<string>();
        int newFileCount = 0;

        /*
         * 「内容物保留关键词」判据（用户 2026-10-04 的新功能「内容物压缩文件不解压」）：
         * 一轮扫下来只算一次（设置在一轮里不会变），空列表 ⇒ 一个都不拦。
         */
        ContentKeepRules keepRules = ContentKeepRules.FromSettings(Settings);

        foreach ((ArchiveTask task, string outputDirectory) in parents)
        {
            List<string>? files = scanned
                .Where(entry => string.Equals(entry.OutputDirectory, outputDirectory, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Files)
                .FirstOrDefault();

            if (files == null)
            {
                continue;
            }

            string parentName = string.IsNullOrWhiteSpace(task.FileName)
                ? Path.GetFileName(task.CurrentPath)
                : task.FileName;

            foreach (string file in files)
            {
                // 只认"这一轮新出现"的：解压前就在那里的文件不是这一轮的产物。
                if (existingFiles.Contains(file))
                {
                    continue;
                }

                /*
                 * ⛔ 已经是我们自己的东西（本批的源包 / 已经登记过路径的文件）**不进这一轮的统计与日志**
                 * （用户 2026-10-05 真机）：真机那一轮扫出 857 个文件，其中 2 个是
                 * `其余物\HK.7z.001/.002`（本批的源包）—— 它们永远只会是"跳过"，
                 * 却把"新文件"这个数字与日志行都撑大了（用户点名的"857 个文件、20 行日志"）。
                 *
                 * ⚠ 判据只过滤**已知的**（见 <see cref="ShouldScanProducedFile"/>）：
                 * ⛔ 不许顺手把整个「其余物」目录排除掉 —— 里面**没登记过**的包正是默认档续解链的输入。
                 */
                if (!OneClickCoordinator.ShouldScanProducedFile(file, sourcePaths, knownTaskPaths))
                {
                    continue;
                }

                newFileCount++;

                /*
                 * 已经判成内容物的文件（同一路径 + 大小 + 修改时间）不再打开：
                 * 轮次扫描、批末确认反复枚举到它时直接跳过，避免图片/视频被重复嗅探。
                 */
                if (TryGetTerminalProducedContent(file, out _))
                {
                    continue;
                }

                /*
                 * ===== 「内容物保留关键词」：命中就**不当内层归档**（用户 2026-10-04 的新功能）=====
                 *
                 * 用户原话：「只要文件名里面包含着这个字符就不能动……这些压缩文件碰都不要碰」。
                 * 出厂默认档（RecursionMode = SingleLayer）**不进递归核心**，内层包由这里按轮次继续解
                 * —— 所以这条设置必须在这条路也生效，否则默认档下它等于没做。
                 *
                 * 判据唯一出口 <see cref="ContentKeepRules"/>（包含即命中、大小写不敏感、
                 * ⛔ 不做通配 / 正则、只吃文件名）；空关键词列表 ⇒ 一个都不拦（行为与以前逐字相同）。
                 */
                string? keepsKeyword = keepRules.FindMatch(file);

                if (keepsKeyword != null)
                {
                    skippedLines.Add($"{file}（名字命中「内容物保留关键词」：{keepsKeyword} —— 按设置不解开也不改名）");
                    RememberTerminalProducedContent(file, "内容物保留关键词");
                    continue;
                }

                if (ArchiveFixer.Security.ContentContainerProtection.ShouldProtect(file))
                {
                    skippedLines.Add(
                        $"{file}（{ArchiveFixer.Security.ContentContainerProtection.Describe(file)}"
                        + " —— 默认当内容物保留，不解开也不改名；要拆请单独选中它用「只解压」）");
                    RememberTerminalProducedContent(file, "内容容器");
                    continue;
                }

                /*
                 * ===== 内层包候选的**下限**（用户 2026-09-24 铁证）=====
                 *
                 * 现场：外层 ZIP 是加密条目 → 7z 半成功写出 **0 字节**的 `2部轻熟1.7z.001/.002` →
                 * 老逻辑只看后缀，把那个 0 字节的 `.001` 当成"包"继续往下解，一路解到 `2部轻熟1.7z`
                 * 才被 7-Zip 用"文件为空"打回来 —— 用户看到的是连解两层空气。
                 * 用户明确要求：**0 字节的文件绝不许进入续解链**。
                 */
                if (!InnerLayerCandidateClassifier.TryGetCandidateLength(file, out long candidateLength) || candidateLength <= 0)
                {
                    skippedLines.Add($"{file}（0 字节或读不到，不可能是包）");
                    continue;
                }

                /*
                 * 空间不足模式：其余物里的东西是「最后一层过程物」或已成功过程的副本，
                 * 成功时应彻底删除，失败时只留给用户，⛔ 不再当下一轮的新内层包。
                 */
                if (OneClickCoordinator.ShouldSkipAsProcessArtifact(file, _vm.SpaceTightMode, out string artifactReason))
                {
                    skippedLines.Add($"{file}（{artifactReason}）");
                    continue;
                }

                /*
                 * 唯一还按名字排除的一类：分卷的**后续卷**（.002 / .z02 / .r01 / .part2+）。
                 * 理由是"哪一卷是组的开头"只能从名字看出来 —— 这是**分组**信息，不是格式判断；
                 * 格式一律只看内容（下一段）。⛔ 别在这里再加"后缀得是已知归档后缀"那种条件：
                 * 那正是 2026-09-25 真机上"名字被塞了两个字就只解一层"的根因。
                 *
                 * ⛔ **但名字这一格必须让内容先说**（2026-10-18，唯一出口
                 * <see cref="ArchiveFixer.Detection.VolumePieceFacts.ContentProvesNotAVolumePiece"/>）：
                 * 现场（困难批 `24242424`）`222-W1-1.932` 的真实内容是**单盘 ZIP**，名字里那个 `932`
                 * 只是伪装尾巴 —— 老写法把它当"后续卷"跳过 ⇒ 它的密码候选只试 1 个就收场。
                 * 内容可证是单卷 ⇒ 名字不算数，照旧按内容分类进续解链。
                 */
                if (InnerLayerCandidateClassifier.IsVolumeContinuationPart(file)
                    && !ArchiveFixer.Detection.VolumePieceFacts.ContentProvesNotAVolumePiece(file)
                    && !InnerLayerCandidateClassifier.ContentProvesFirstVolumePiece(file))
                {
                    skippedLines.Add($"{file}（是分卷的后续卷，起点在第一卷上）");
                    continue;
                }

                /*
                 * 普通产物先看底层卡，不再对每个图片/视频都跑一遍 DetectAsync。
                 * 只对“卡上仍判不出格式且没有归档结构”的文件进入完整归档探测；
                 * 媒体前缀归档的卡是 ContentClass=Archive/EmbeddedOffset>0，不会被这里跳过。
                 */
                FileNatureCard producedNature = _extractionCoordinator.TryGetProducedCard(file, out FileNatureCard cachedNature)
                    ? cachedNature
                    : FileNature.Inspect(file);
                bool isArchive;
                string how;

                if (producedNature.IsKnownNormalMedia
                    && producedNature.ContentClass != FileContentClass.Archive
                    && producedNature.BodyKind == ArchiveBodyKind.Unknown)
                {
                    RememberTerminalProducedContent(file, "普通媒体（内容结构完整，无尾部归档）");
                    skippedLines.Add($"{file}（普通媒体，不做归档探测）");
                    continue;
                }

                if (producedNature.Format != VolumeContentFormat.Unknown
                    || producedNature.ContentClass == FileContentClass.Archive
                    || producedNature.BodyKind != ArchiveBodyKind.Unknown)
                {
                    isArchive = true;
                    how = producedNature.EmbeddedOffset > 0
                        ? $"尾部内嵌归档（{producedNature.Format}，偏移 {producedNature.EmbeddedOffset}）"
                        : $"文件头魔数（{producedNature.Format}）";
                }
                else
                {
                    (isArchive, how) = await ClassifyArchiveByContentAsync(file);
                }

                /*
                 * ⚠ **引擎按内容打不开，不代表它不该进续解链**（2026-10-11 真机 EEE 实测，轮次模型补的那一格）：
                 *
                 * 现场逐字：`第 1 层产物扫描 —— 新文件 3 个；按内容认出归档 0 个；跳过 3 个`，
                 * 而这三个新文件是**第二大步刚解出来的那两片**（`111.parts1.racr` / `111.part删2.ra除r`）
                 * —— RAR 找兄弟卷**靠名字**，这一对名字是脏的（一个 `parts1`、一个夹着"删/除"），
                 * 引擎自然打不开它 ⇒ 0 个"归档" ⇒ 这一批就"到此为止"，
                 * 可用户要的东西还压在这两层分卷里（正是他说的"接着就该下一轮的第二大步"）。
                 *
                 * ⇒ 补一道**分卷专属那把尺子**：这一份确实是**某一组的第一卷**（同目录里真有它的兄弟卷）
                 * 就照样当候选收进来 —— 判据只读名字与目录事实，⛔ 不猜格式、⛔ 不改名；
                 * 名字怎么改、那一组怎么开，是**下一轮第二大步**的事。
                 */
                if (!isArchive && IsVolumeGroupEntryInPlace(file))
                {
                    isArchive = true;
                    how = "按分卷尺子认出：这一组的第一卷（引擎按内容打不开是因为名字脏，交给第二大步）";
                }

                /*
                 * 内容自证是“某一组的第一片”（7z 起始头自述整包/片数）⇒ 名字像后续卷也不算数：
                 * 真机 19191919：`.0!601` 是首片，名字尾巴 601 被当成“后续卷”跳过，整组就永远缺卷。
                 * ⛔ 只认内容事实，不改名、不猜格式。
                 */
                /*
                 * ⛔ 只有名字本身带卷槽时才允许做“内容自证首片”的 `FileNature`：
                 * 普通图片/视频没有卷槽，不该为了这一条再读一遍头尾。
                 * 同基名的内容物由第二大步候选层按基名上下文处理。
                 */
                if (!isArchive
                    && LegacyVolumeNames.TryGetIndex(FileNameHelper.GetFileName(file)).HasValue
                    && InnerLayerCandidateClassifier.ContentProvesFirstVolumePiece(file))
                {
                    isArchive = true;
                    how = "按内容认出：分卷首片（起始头自述整包/片数），交给第二大步";
                }


                if (!isArchive)
                {
                    RememberTerminalProducedContent(file, how);
                    skippedLines.Add($"{file}（{how}）");
                    continue;
                }

                /*
                 * ⛔ **同一份物理文件的两条名字，只算一个内层包**（2026-10-18，困难批 `8888` 运行期定案）。
                 *
                 * <para>现场逐字：`打包111-L2.part1.rar` 与 `打包111-L2.part1删删删删删删删.rar`
                 * 是**同一个 File ID**（`fsutil file hardlink list` 两个名字、`接片` 建的是零字节硬链接），
                 * 而这一轮扫描按**名字**给它们各建了一单 ⇒ 两单都去解同一组 ⇒ 第一单解出的
                 * `打包111-L1.7z` 被第二单当"自己的过程物"搬进其余物 ⇒ 下一轮又被当新原包解开
                 * ⇒ `contentBytes = 125829120`（两份 60 MiB，判据 ① 红）。</para>
                 *
                 * <para>判据只读盘上身份（<see cref="ArchiveFixer.Detection.FileIdentity"/>：卷序列号 + 文件索引，
                 * 改名 / 移动 / 硬链接都不变）；同一份物理文件保留**内容卡自证"这条名字就是规范名"**的那一条
                 * （2026-10-18 困难批 `15151515` 组合 6 运行期定案：`aaa-L2.zip` 与 `aaa-L2.z00000000ip`
                 * 是同一份字节，卡的 `CanonicalVolumeName` 都是 `aaa-L2.zip`；原来"留先出现的"留下了脏名
                 * ⇒ 第二轮拿脏名去开这一组 ⇒ 引擎找不到 `.z01` ⇒ ① ② 双红）。两条都不是规范名 ⇒ 照旧留先出现的。
                 * ⛔ 不涉及用户源包 —— 源包在前面 `ShouldScanProducedFile` 已经排掉。</para>
                 */
                if (ArchiveFixer.Detection.FileIdentity.TryGetIdentity(file, out ulong dupVolume, out ulong dupIndex))
                {
                    string identityKey = dupVolume.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ":"
                        + dupIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

                    if (seenInnerPackageIdentities.TryGetValue(identityKey, out string? keptFile))
                    {
                        bool candidateIsCanonical = OneClickCoordinator.IsContentCanonicalName(file);
                        bool keptIsCanonical = OneClickCoordinator.IsContentCanonicalName(keptFile!);

                        if (candidateIsCanonical && !keptIsCanonical)
                        {
                            /*
                             * 新来的这一条才是规范名 ⇒ 换掉旧那一条（把旧名字从已认出的清单里撤掉，
                             * 否则同一个身份会留下两条候选）。
                             */
                            skippedLines.Add(
                                $"{keptFile}（与「{file}」是同一份文件的两个名字 —— 只算一个内层包，留规范名那一条）");

                            recognizedLines.RemoveAll(line =>
                                line.StartsWith(keptFile! + "（", StringComparison.Ordinal));
                            candidates.RemoveAll(candidate =>
                                string.Equals(candidate.File, keptFile, StringComparison.OrdinalIgnoreCase));
                            found.Remove(keptFile!);

                            seenInnerPackageIdentities[identityKey] = file;
                        }
                        else
                        {
                            skippedLines.Add($"{file}（与「{keptFile}」是同一份文件的两个名字 —— 只算一个内层包）");
                            continue;
                        }
                    }
                    else
                    {
                        seenInnerPackageIdentities[identityKey] = file;
                    }
                }

                recognizedLines.Add($"{file}（{TaskSpaceEstimate.FormatSize(candidateLength)}，{how}）");

                if (!found.ContainsKey(file))
                {
                    /*
                     * ⚠ 落点**按每个内层包自己所在的目录**算（第 35 条），不能用"每个父任务一个" ——
                     * 共用输出根那一档下，同一个父任务的两个内层包分别躺在
                     * `…\111\2222\` 与 `…\111\3333\`，各自的内容物必须回到各自那一层旁边。
                     *
                     * 先只记下来，"要不要给这一层再套一层包名目录"**必须等这一轮扫完**才能定：
                     * 判据里有"同一个父任务认出了几个内层包"（分支 or 单链）这条事实。
                     */
                    candidates.Add((task, parentName, file));
                }
            }
        }

        /*
         * 同一份内层包在「其余物」里还有一份副本时，只认产品树那一份。
         * 空间不足模式会删掉产品树里的源，但其余物副本仍在；不拦就会把同一包
         * 再当一次新内层包解开，出现 222-L2 / 222-L2(1) 这种重复目录。
         * 判据 = 同名 + 同字节数；只拦其余物里的那一份，正常「只存在于其余物」的续解包照旧保留。
         */
        var outsideRestKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((ArchiveTask _, string _, string file) in candidates)
        {
            if (!ProcessArtifactLayout.IsInsideDeletableProcessFolders(file))
            {
                outsideRestKeys.Add(OneClickCoordinator.InnerPackageKey(file));
            }
        }

        foreach ((ArchiveTask _, string _, string file) in candidates.ToList())
        {
            if (ProcessArtifactLayout.IsInsideDeletableProcessFolders(file)
                && outsideRestKeys.Contains(OneClickCoordinator.InnerPackageKey(file)))
            {
                skippedLines.Add($"{file}（产品树里已有同名同大小的内层包，其余物里的副本不再当成新包）");
                candidates.RemoveAll(candidate =>
                    string.Equals(candidate.File, file, StringComparison.OrdinalIgnoreCase));
                found.Remove(file);
            }
        }

        /*
         * ===== 续解层那一层目录（用户 2026-09-27：首尾必留，中间看开关）=====
         *
         * 判据全部在 <see cref="ShouldAddContinuationLevelLayer"/> 里（唯一实现），这里只做三件事：
         * ① 按父任务分组（"这一轮认出几个内层包"是按父任务数的）；
         * ② 要建层时取**这一层内层包的包基名**（分卷组取不出来 → 过程物名不成层，见 AppendOwnLayer）；
         * ③ 层名被内层包自己那个文件占着时不建层，写日志说清（见下面那一段）。
         */
        foreach (IGrouping<ArchiveTask, (ArchiveTask Parent, string ParentName, string File)> group in
                 candidates.GroupBy(candidate => candidate.Parent))
        {
            ArchiveTask parent = group.Key;
            List<(ArchiveTask Parent, string ParentName, string File)> children = group.ToList();

            // 父任务自己产出了内容物 = 这一层是"内容 + 内层包"的分支，不是干净的单链中间层。
            // ⚠ 判据是**搬出去的内容物文件数**（唯一权威来源 = 定稿计划），不是
            // `ContentDirectoryPath` —— 后者对"只出过程物"的层也非空（见 ArchiveTask.ContentFileCount）。
            bool parentProducedContent = parent.ContentFileCount > 0;

            bool addLayer = OneClickCoordinator.ShouldAddContinuationLevelLayer(
                parentProducedContent,
                children.Count);

            if (parentProducedContent && addLayer && children.Count > 1)
            {
                scanLog.Add(
                    $"{parent.FileName}：这一层自己产出了内容物、又认出了 {children.Count} 个内层包 ——"
                    + "它们各自占一层（否则几个包的内容物会倒进同一层）。");
            }
            else if (parentProducedContent && !addLayer)
            {
                /*
                 * 用户 2026-09-27 真机：父层已经出了内容物 → 内层包解出来的东西**并进父层那一个目录**，
                 * 与父层自己的内容物并排，不再用内层包名另造一层（那会把真内容埋深一层）。
                 */
                scanLog.Add(
                    $"{parent.FileName}：这一层自己产出了内容物 —— 内层包解出来的东西并进这一层"
                    + "（不再另建以包名命名的目录，免得真内容被埋到上一层文件下面）。");
            }

            foreach ((ArchiveTask _, string parentName, string file) in children)
            {
                string layerName = addLayer ? OneClickCoordinator.ResolveContinuationLayerName(file) : string.Empty;

                /*
                 * 层名**被内层包自己那个文件占着**（名字被改坏、剥不出后缀那种）→ 那一层建不出来，
                 * 内容落进父任务那一层。这是用户看得见的落点差别，必须写日志说清（⛔ 绝不静默）。
                 */
                if (layerName.Length > 0
                    && OneClickCoordinator.IsOwnLayerOccupiedByPackageFile(OneClickCoordinator.ResolveContinuationBaseDirectory(parent, file), layerName, file))
                {
                    scanLog.Add(
                        $"{file}：这一层要建的位置被「内层包自己」占着（名字被改坏、剥不出后缀）——"
                        + "这一层不另建，内容落进父任务那一层里。");
                }

                found[file] = new InnerArchiveCandidate
                {
                    Path = file,
                    ParentOutputDirectory = OneClickCoordinator.ResolveContinuationOutputDirectory(parent, file, layerName),
                    ParentTaskName = parentName,
                    RootSourcePath = parent.ChainRootIdentity
                };
            }
        }

        /*
         * 扫描日志：**先写清"看了多少、认出几个、跳过几个"，再把逐条明细列出来**。
         * 上限 20 条明细（一个包里几千个文件时不能让日志爆掉），超出只报个数 ——
         * 但"有多少没列出来"必须写出来，绝不静默截断。
         */
        scanLog.Add(
            $"一键处理：第 {round} 层产物扫描 —— 新文件 {newFileCount} 个；"
            + $"按内容认出归档 {recognizedLines.Count} 个；跳过 {skippedLines.Count} 个。");

        foreach (string line in InnerLayerCandidateClassifier.AppendCapped(recognizedLines, "认出（会继续解）"))
        {
            scanLog.Add(line);
        }

        foreach (string line in InnerLayerCandidateClassifier.AppendCapped(skippedLines, "跳过"))
        {
            scanLog.Add(line);
        }

        return (found.Values.OrderBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).ToList(), scanLog);
    }

    /// <summary>
    /// 按**内容**判断一个候选是不是归档（后缀一个字都不看）。
    ///
    /// <para>判据与识别阶段**同一口径**：直接调 <see cref="ArchiveDetectService.DetectAsync"/>
    /// ——也就是"扫描任务"那一步用的同一个实现：先读文件头魔数（RAR5 / 7z / ZIP / 分卷起点…），
    /// 文件头认不出时它自己会去看文件尾部有没有"内嵌归档"（假 MP4 头 + 尾部完整 ZIP 那种双面文件）。
    /// ⛔ 这里**不许**再另造一套签名表或另加一道按名字的筛子 —— 那正是 2026-09-25
    /// 真机上"名字被塞了两个字就只解一层"的根因。</para>
    ///
    /// <para>返回 <c>(是归档?, 判据说明)</c>：说明会原样进日志（用户要求"看日志就能定位"）。</para>
    /// </summary>
    private async Task<(bool IsArchive, string How)> ClassifyArchiveByContentAsync(string filePath)
    {
        try
        {
            DetectResult result = await _detectService.DetectAsync(filePath);

            if (!result.IsArchive || !result.IsKnownFormat)
            {
                return (false, "按内容认不出是归档（文件头与尾部都试过了）");
            }

            /*
             * 说清是**哪一条**判据认出来的：
             * · 尾部内嵌归档 → 这种文件必须按偏移抠出来才能解（解压侧已经这么做了）；
             * · 文件头魔数 → 普通包。
             * 用户要的是"日志能自证"，所以这一句不能含糊。
             */
            return result.EmbeddedArchiveOffset > 0
                ? (true, $"尾部内嵌归档（{result.Format}，偏移 {result.EmbeddedArchiveOffset}）")
                : (true, $"文件头魔数（{result.Format}）");
        }
        catch
        {
            // 读不了（被独占 / 刚好被删）：当作"认不出来"，不把它当包 —— 宁可不续解，也不解空气。
            return (false, "读不了（被占用或刚好被删）");
        }
    }

}

