using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>拆包闸门的扫描根与扫描边界（从 ExtractionCoordinator.Source.cs 搬出）；⛔ 判据/边界文案沿用原实现。</summary>
internal sealed class ExtractionSplitGateScanner
{
    private readonly ExtractionCoordinator _host;

    internal ExtractionSplitGateScanner(ExtractionCoordinator host)
    {
        _host = host;
    }

    /// <summary>
    /// 「半套分卷」那道闸门要扫的**成品目录树根**（同组的另一片可能落在这一棵树里的任何地方）。
    ///
    /// <para>⛔ 判定扫描范围的原则是"**宁可多扫**"：漏扫 = 放行 = 不可逆（§44.2 就是这么丢掉 25 GB 的），
    /// 而多扫最多只是把一次删除拦下来（兜底落在"什么都不做"，用户还能自己处理）。</para>
    ///
    /// <para><b>2026-10-03 第二轮复核改的那一处</b>：原来只取**第一个**候选的上一级（末尾那个 <c>break</c>），
    /// 于是"候选比成品根深 ≥2 层、另一片在同一个成品根下的**另一条分支**"这个形状
    /// （<c>&lt;pkg&gt;\AAA\BBB\mid.7z.001</c> vs <c>&lt;pkg&gt;\CCC\mid.z删除ip</c>）四个根一个都盖不到 ⇒ 放行。
    /// 现在**对每一个候选**沿祖先链上溯，逐层当扫描根，遇到"任务记下来的那几处"就停住。</para>
    ///
    /// <para><b>2026-10-03 第三轮（用户批准「取消 3 层上限」）</b>：候选落在这一批的目标根里面时，
    /// 上溯**不再有层数上限**，一路爬到目标根为止 —— 深 ≥3 层 + 同级另一条分支那个形状
    /// （<c>&lt;成品根&gt;\X\AAA\BBB\mid.7z.001</c> vs <c>&lt;成品根&gt;\Y\CCC\mid.z删除ip</c>）因此盖得住。
    /// ⛔ 目标根与盘根这两条界一个字都没动（详见方法体里那段说明）。</para>
    /// </summary>
    internal IReadOnlyList<string> EnumerateSplitGateArtifactRoots(
        ArchiveTask task,
        IReadOnlyList<string> candidates)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /*
         * 这一次允许看的唯一一棵树（口径见 ResolveSplitGateScanBoundary）：
         * · 选文件夹 = 选中的文件夹；
         * · 选单个压缩包 = 它旁边/指定位置里那个以包基名命名的成品文件夹；
         * · 选单个文件且任务已算出 OutputPath = 那个实际成品文件夹。
         */
        string batchRoot = ResolveSplitGateScanBoundary(task);

        /*
         * ⚠ 这一张去重表**只能服务于 `roots` 这一份清单**（2026-10-03 实测踩过）：
         * 一开始让"任务记下来的那几处"和"祖先链"共用同一张表，于是祖先链上那几层
         * （它们本来就等于任务自己的输出目录）全被判成"已经加过了" ⇒ `roots` 空 ⇒
         * 我新加的"拿不到成品目录树 ⇒ 拦下"那一档**每次都误触发**（逐层回收一个字节都删不掉）。
         */
        void Add(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            string full = SafePathHelper.GetFullPathSafe(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            /*
             * ⛔ 盘根永远不是"成品目录树"：候选不在这一批目标根里面时，上溯 3 层可能正好走到
             * H:\ / D:\。把盘根交给下面的递归枚举 = 整盘扫一遍（真机 2026-10-26：H:\ 上
             * System Volume Information 读不动，UI 线程卡到 Windows 判定 AppHang）。
             * 判不出就什么都不做，绝不拿"扫不动"去换一次不可逆删除。
             */
            string? driveRoot = Path.GetPathRoot(full);

            if (!string.IsNullOrWhiteSpace(driveRoot)
                && string.Equals(
                    full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    driveRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            /*
             * 边界外的任何地方都不进扫描清单 —— 用户的源包/别的目录没有资格被扫。
             */
            if (batchRoot.Length > 0
                && !ArchivePathGuard.IsInsideRoot(batchRoot, full, out _))
            {
                return;
            }

            if (full.Length > 0 && seen.Add(full))
            {
                roots.Add(full);
            }
        }

        /*
         * 上溯的**上界 = 这一次真正允许看的那棵树**：
         * · 「未指定位置」+ 选文件夹 = 用户选中的文件夹；
         * · 「未指定位置」+ 选单个压缩包 = 这个包基名的成品文件夹；
         * · 「指定位置」+ 选单个压缩包 = 指定位置里这个包基名的成品文件夹；
         * · 「指定位置」+ 选文件夹 = 用户指定的成品根。
         * ⛔ 不再无条件拿 Settings.CustomOutputDirectory 当边界 —— 那一格在「未指定位置」时可能
         * 是上一次留下的旧地址（真机 2026-10-26：旧值是 `H:\BaiduNetdiskDownload\大测试-...`，
         * 而这一批实际在 `H:\BaiduNetdiskDownload\666` ⇒ 候选被判成"在边界外" ⇒ 上溯到 H:\ ⇒ 整盘扫）。
         */

        /*
         * 每个候选沿祖先链上溯。
         *
         * ===== 2026-10-03 第三轮：**去掉 3 层上限**（用户批准的原话：「①（上界）取消 3 层上限」）=====
         *
         * 漏的形状（只读复核给的现场）：要删的候选比成品根深 ≥3 层、同组另一片落在**同级另一条分支** ——
         * `<成品根>\X\AAA\BBB\mid.7z.001` 要删 vs `<成品根>\Y\CCC\mid.z删除ip` 留在成品里。
         * 第 3 层那个祖先（`X`）虽然是递归扫的，但 `Y` 不在它里面 ⇒ 上溯出来的几个根一个都盖不到
         * ⇒ 判成"不是半套" ⇒ **放行 = 不可逆删除**（用例 `ChainLayerReclaimTests.用例H2` 钉的就是它：
         * 加回 3 层上限时那一组三卷当场被删光）。
         *
         * 改法：候选**落在这一批的边界里面**时，从它自己那一层一路上溯，**直到边界为止**（含），
         * 不再有层数上限。⛔ 候选**不在边界里面**时，只允许看它自己所在的那一层 ——
         * 绝不顺着父目录爬到用户的上一层/盘根（2026-10-26 用户逐字：扫描范围只限于选中的文件夹
         * 或指定的成品位置，其他地方没有资格动手）。
         */
        foreach (string candidate in candidates)
        {
            string full = SafePathHelper.GetFullPathSafe(candidate);
            string? directory = Path.GetDirectoryName(full);

            bool insideBatchRoot = batchRoot.Length > 0
                && ArchivePathGuard.IsInsideRoot(batchRoot, directory, out _);

            for (int level = 0; !string.IsNullOrWhiteSpace(directory); level++)
            {
                string current = directory!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                /*
                 * 边界外的候选（例如单文件模式下的原包自己）不扫它的父目录：
                 * 用户明确说"外面只有原包，不用管其他地方"。只扫边界内那一棵成品树。
                 */
                if (batchRoot.Length > 0 && !insideBatchRoot)
                {
                    break;
                }

                Add(current);

                if (batchRoot.Length > 0 &&
                    string.Equals(current, batchRoot, StringComparison.OrdinalIgnoreCase))
                {
                    break;   // 到允许看的边界了，再往上不是本次的地盘
                }

                /*
                 * 边界外 / 没有边界：只保留候选自己那一层，绝不上溯。
                 */
                if (!insideBatchRoot || batchRoot.Length == 0)
                {
                    break;
                }

                string? parent = Path.GetDirectoryName(current);

                if (string.IsNullOrWhiteSpace(parent) ||
                    string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;   // 到盘根了（绝对兜底）
                }

                directory = parent;
            }
        }

        // 任务自己记下来的那几处（与其余物那一档同一口径）。
        if (!string.IsNullOrWhiteSpace(task.RestDirectoryPath))
        {
            string fullRest = SafePathHelper.GetFullPathSafe(task.RestDirectoryPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            Add(Path.GetDirectoryName(fullRest));
        }

        // `ParentOutputDirectory` = 父任务交出来的那个落点（链上所有任务共用的那一层，见 ArchiveTask 的说明）。
        Add(task.ParentOutputDirectory);
        Add(task.ContentDirectoryPath);
        Add(task.OutputPath);

        return roots.Where(Directory.Exists).ToList();
    }

    /// <summary>
    /// 「半套分卷」闸门允许看的**唯一一棵树**。
    ///
    /// <para>用户 2026-10-26 口径：
    /// · 选文件夹 = 这个文件夹里面；
    /// · 选单个压缩包 = 它旁边/指定位置里那个**以包基名命名的成品文件夹**；
    /// · 指定位置 = 只在这个成品位置里面，绝不看源包外面的其他目录。</para>
    /// ⛔ 不读「未指定位置」时那份可能已经过期的 <see cref="AppSettings.CustomOutputDirectory"/>。
    /// </summary>
    internal string ResolveSplitGateScanBoundary(ArchiveTask task)
        => ResolveSplitGateScanBoundary(task, _host.Settings);

    /// <summary>上面那个边界的**纯函数版**（单测直接调，不 new 协调器）。</summary>
    internal static string ResolveSplitGateScanBoundary(ArchiveTask task, AppSettings? settings)
    {
        string Normalize(string? path) => SafePathHelper.GetFullPathSafe(path ?? string.Empty)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        string FirstExistingOutput()
        {
            foreach (string? candidate in new[] { task.OutputPath, task.ContentDirectoryPath, task.CollectedPath })
            {
                string normalized = Normalize(candidate);

                if (normalized.Length > 0 && Directory.Exists(normalized))
                {
                    return normalized;
                }
            }

            return string.Empty;
        }

        string baseName = task.CurrentPath.Length > 0
            ? OutputPlacement.ResolveArchiveBaseName(task.CurrentPath)
            : string.Empty;

        bool fileSelection = task.SourceSelectionKind != SourceSelectionKind.Folder;
        bool customPlacement = settings != null && !settings.ExtractToOriginalDirectory;

        if (customPlacement)
        {
            string customRoot = Normalize(settings?.CustomOutputDirectory);

            /*
             * 指定位置 + 选单个压缩包：只在「指定位置\<包基名>\」里扫
             * （用户例子：指定位置\111\，绝不扫指定位置的上一层/别的包）。
             */
            if (fileSelection)
            {
                string actualOutput = FirstExistingOutput();

                if (actualOutput.Length > 0)
                {
                    return actualOutput;
                }

                if (customRoot.Length > 0 && baseName.Length > 0)
                {
                    return Normalize(Path.Combine(customRoot, baseName));
                }
            }

            if (customRoot.Length > 0)
            {
                return customRoot;
            }

            string actualRoot = Normalize(task.OutputPath);

            if (actualRoot.Length > 0)
            {
                return actualRoot;
            }
        }

        /*
         * 「未指定位置」+ 选文件夹：这个文件夹就是边界（每个包各自的成品文件夹都在它下面）。
         */
        if (task.SourceSelectionKind == SourceSelectionKind.Folder
            && !string.IsNullOrWhiteSpace(task.SourceSelectionRoot))
        {
            string selectedRoot = Normalize(task.SourceSelectionRoot);

            if (selectedRoot.Length > 0 && Directory.Exists(selectedRoot))
            {
                return selectedRoot;
            }
        }

        /*
         * 「未指定位置」+ 选单个压缩包：H:\111.rar → H:\111\，只扫这个成品文件夹。
         * 源包自己就在外面，用户明确说"外面只有原包，不用管其他"。
         */
        string actualFileOutput = FirstExistingOutput();

        if (actualFileOutput.Length > 0)
        {
            return actualFileOutput;
        }

        string sourceDirectory = Normalize(Path.GetDirectoryName(task.CurrentPath));

        return baseName.Length > 0
            ? Normalize(Path.Combine(sourceDirectory, baseName))
            : sourceDirectory;
    }
}

