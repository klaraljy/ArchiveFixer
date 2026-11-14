using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 递归展开的**一层**：把层准备已经定下的事实（归档 / 选项 / 候选 / 标签）交给收卷预检，
/// 再交给候选循环。
///
/// <para>本类只编排这一层的顺序：判据与执行体仍在既有唯一出口里
/// （<see cref="RecursiveSpannedZipStage"/>、<see cref="LayerExecutor"/>、层报告与工作区账本），
/// ⛔ 不复制第二份。宿主 <see cref="RecursiveExtractor"/> 保留公开 API 与主循环 facade。</para>
/// </summary>
internal static class RecursiveLayerExpansion
{
    internal static async Task<LayerOutcome> ExecuteAsync(
        RecursiveExtractor host,
        WorkItem item,
        LayerPlan plan,
        string? carvedSource,
        CancellationToken cancellationToken,
        IProgress<ArchiveProgress>? progress,
        Action<ArchiveStallNotice>? stalled,
        Action<RecursionStopReason, string> reportStopReason)
    {
        string archivePath = plan.ArchivePath;
        string layerLabel = plan.LayerLabel;

        /*
         * ===== 这一层到底交给引擎哪一个文件（2026-10-04 真机，用户报"密码是对的怎么解压不了"）=====
         *
         * 现场：`HK.7z.001` 第 0 层解出 3 个 4K 视频（7.11 GiB，全部正常），第 1 层探到
         * `4K (11)_2.mp4` 是内层包 → 直接把**原文件**交给 7-Zip → 7-Zip 回
         * `Cannot open the file as archive` → 整条链判"部分完成" ⇒ 已解出的 7.11 GiB 一个字节都不发布、
         * 工作区整份被清掉（14 分钟白跑）。
         *
         * 根因：那个 mp4 是**双面文件**（真视频 + 尾部一整个 ZIP，用户资源包里的常见形态）。
         * 7-Zip 只在"前面垫的数据 ≤ 8 MiB"时才容忍这种整体偏移，超过就报上面那句原话；
         * 单层路径早就接了这一档（先按偏移抠出来再解），**递归这条路一直没接** ——
         * 而 2026-10-04 起出厂默认档正是"展开所有分支"，于是这条路成了默认路径。
         *
         * 修法：本方法多一个"改用哪一份"的参数。正常传 null（照旧用 <see cref="WorkItem.ArchivePath"/>）；
         * 引擎回过"这不是归档"、而它又确实是双面文件时，外层用
         * <c>TryCarveEmbeddedInnerArchiveAsync</c> 抠出副本，再带这个参数重跑一次本方法。
         * ⛔ 抠出来的只是**副本**，原文件一个字节都不动；⛔ 判不出是不是双面文件 ⇒ 什么都不做（照旧失败）。
         */

        /*
         * ===== 跨层收卷 + 凑不齐就别试（用户 2026-10-05 真机）=====
         *
         * 现场：三卷 7z 的三片分别躺在**三个不同的层产物目录**里（`layer-001\output\HK.7z.002`、
         * `layer-002\output\HK.7z.003`、`layer-003\output\HK.7z.001`）。引擎找兄弟卷**只看入口文件
         * 旁边那一层** ⇒ 第 2 层拿 `HK.7z.001` 单独去解必然报
         * `Open ERROR: Cannot open the file as [7z] archive` ⇒ 整条链判「部分完成」、
         * 工作区 7 个文件 / 13.48 GiB 整份删掉、什么都没发布。用户原话：
         * 「各分卷在不同的目录，你就将其全部移动到头文件 .001 同级目录里面去」、
         * 「要不然你在分开了你还会继续解压单独的001」。
         *
         * <para>挂点为什么在**这里**（而不是 `ProbeInnerArchivesAsync` 的「还原」工序之后）：
         * ① 收卷要的是"**到解这一层时**兄弟卷都齐了"—— 三片由三个同深度的分支各自产出，
         * 谁先谁后由队列决定，在"产出"那一刻收会漏掉后面才产出 / 才被抠出来的那几片；
         * 解这一层是**唯一**能确定"该在的都在了"的时刻。
         * ② "凑不齐就别试"本来就只能在**调用引擎之前**判（这里正是引擎调用之前、候选循环之外）。
         * ③ 「还原」工序那一挂点照旧不动：它管的是本层产物的名字，与收卷互不干扰。</para>
         *
         * <para>⛔ 判据与执行体都不在这里：整件事转调 `VolumeNameRepair.ResolveCrossLayerVolumeGather`
         * （纯计划）+ `VolumeNameRepair.TryApply`（全成或全不成、绝不覆盖、失败倒序回滚）。
         * ⛔ 候选池只有两处：**入口自己那一层**（只读）+ **这条链的各层产物目录的直接子文件**
         * （`CurrentWorkspace.Layers`，⛔ 不递归、⛔ 不含 `carved` 这种我们自造的兄弟目录、
         * ⛔ 绝不碰用户源目录）；跨盘一律不收（`BuildPlanFromOrder` 里判）。</para>
         */
        /*
         * ===== 跨盘 ZIP：末片在手、其余几片散在源目录里（2026-10-05 真机第八批）=====
         *
         * 现场：`111.z0删除1/2/3` 三片散在三个源目录里，而这一组的末片 `111.zip` 压在两层层层加密的
         * RAR 里面。批首那一刻末片还在包里 ⇒ 源包那条路看不见它；链把末片解出来之后，其余几片又全在
         * 用户源目录里 ⇒ 上面那条跨层收卷（候选池 = 工作区各层产物）也看不见它们。两条路各自封闭，
         * 结果就是 7-Zip 报 `Missing volume : 111.z01`、整条链判「部分完成」、什么都没发布。
         *
         * 判据是**硬证据**：末片的 EOCD 是明文（`-p` 只加密数据），它自己写着"我是第 k 片、一共 n 片"。
         * 动作 = 给源目录里那几片在入口这一层**多起一个规范卷名**（硬链接，零字节；⛔ 不改名、不搬、
         * 绝不把用户的源片搬进工作区 —— 工作区是整份删的）。凑不齐 ⇒ 一次引擎调用都不做（不变量 7）。
         */
        /*
         * ⛔ **候选池要并上"这条链各层产物里的分卷片"**（2026-10-07 真机 EEEE）：老写法只给
         * "找卷窗口"（RootSourceCandidates），而 111(3) 解出来的那一片
         * 111(3)\111(3)\111.z0删除2 是**链产物**、不在窗口里 ⇒ 末片 gather 永远凑不齐
         * z01/z02/z03，整组判"判不出该怎么收"、零引擎调用。
         * 并池只多给"能被名字/内容定出盘号"的候选；⛔ 动作仍是硬链接（源片不改名不搬），
         * 盘号判不出/撞号的候选在 gather 内部照旧被拒（ResolveSpannedZipDiskGather 的判据不动）。
         */
        List<VolumeCandidate> spannedPool = host.RootSourceCandidates
            .Concat(RecursiveExtractor.EnumerateWorkspacePool(host.CurrentWorkspace))
            .ToList();
        SpannedZipStageResult spannedStage = RecursiveSpannedZipStage.Run(archivePath, layerLabel, spannedPool);

        if (spannedStage.Blocked)
        {
            host.Log("WARN", spannedStage.BlockedMessage);
            host.RememberUnresolvedVolumePiece(archivePath);

            return LayerOutcome.Stop(
                host.BuildLayerReport(
                    item,
                    result: null,
                    succeededPassword: null,
                    overrideMessage: spannedStage.BlockedMessage,
                    manifestEntries: null,
                    overrideStatus: StatusText.VolumeMissing),
                RecursionStopReason.MissingVolume);
        }

        if (spannedStage.LinkedCount > 0)
        {
            host.AddCrossBoundaryLinks(spannedStage.LinkedPaths);
            host.AddConsumedVolumeSources(spannedStage.LinkedSources);
            host.Log("INFO", spannedStage.LinkedMessage);
        }

        return await LayerExecutor.ExecuteAsync(
                host,
                new LayerExecutionState
                {
                    Item = item,
                    ArchivePath = archivePath,
                    Options = plan.Options,
                    Candidates = plan.Candidates,
                    LayerLabel = layerLabel,
                    CarvedSource = carvedSource,
                    CancellationToken = cancellationToken,
                    Progress = progress,
                    Stalled = stalled,
                    ReportStopReason = reportStopReason
                })
            .ConfigureAwait(false);
    }
}
