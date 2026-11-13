using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 递归路线：非第二步任务整条交给 <see cref="RecursiveExtractor"/>（第 0 层 + 内层链），
    /// 拿到结果后先搬"这一步解出来的"、再走同一个成功收尾；⛔ 判据/顺序与单层路一字不差。
    /// </summary>
    internal sealed class ExtractionRecursiveStep
    {
        internal async Task RunAsync(
            ExtractionCoordinator host,
            ArchiveTask task,
            string engineArchivePath,
            string engineOutputPath,
            string outputRedirectNote,
            bool sharedOutputRoot,
            bool oneClickRun,
            CancellationToken cancellationToken)
        {

            RecursionResult? recursion;

            try
            {
                recursion = await host.RunRecursiveAsync(
                    task, engineArchivePath, engineOutputPath, oneClickRun, cancellationToken,
                    maxDepth: oneClickRun ? null : 1);
            }
            catch (Exception ex)
            {
                host.AppendLog("ERROR", $"{task.LogName}：递归解压异常 —— {ex.GetType().Name}：{ex.Message}");
                throw;
            }

            // 这一趟有没有**可发布的产物**（用户 2026-10-06：「上一步得到的产物下一步就有可能成为
            // 所需要解压的文件」）—— ⛔ 发布与否只由产物事实回答，⛔ 不再由终态标签兼任开关：
            // `111.rar` 解出 `111.zip` 后因缺 `.z01` 停下时，旧写法把状态记成「部分完成」⇒
            // 校验 / 定稿 / 发布整条链一次都不跑 ⇒ 已经解出来的 `111.zip` 一个字节都不落地。
            bool hasPublishableProducts = recursion != null
                && recursion.Layers.Any(layer => layer.Success)
                && !string.IsNullOrWhiteSpace(recursion.FinalOutputPath);

            /*
             * ⚠ **一拿到递归结果就搬**（位置三条硬要求，全是实测踩出来的）：
             * ① **必须在工作区被清掉之前**：那一片（`111.zip`）就活在工作区的递归层目录里，
             *    收尾一清它就没了 ⇒ 放到最后再搬会静默搬不到（守门用例当场逮到：
             *    "这一步解出来的"那一行压根没出现、"结果校验 —— 输出目录是空目录"照旧）。
             * ② **必须在结果校验之前**：校验读的是**暂存目录**，那一片不搬进去 ⇒ 校验看到空目录 ⇒
             *    「校验未通过：输出目录是空目录，没有产物」⇒ 列表显示「解压失败」+「完整性：可证不完整」
             *    ⇒ 源包一个字节都不处理（真机 2026-10-06 11:24 那两个 bug 就是这一处）。
             * ③ **必须在"成功才进"那一支的外面**：`111.rar` 这类是「部分完成」⇒ 进不去那一支。
             * ⇒ 三条一起满足的唯一位置就是这里（拿到结果之后、进任何收尾分支之前）。
             */
            if (recursion != null && recursion.UnresolvedVolumePieces.Count > 0)
            {
                List<string> carried = host.PublishUnresolvedVolumePieces(recursion, task);
                host.AdoptUnresolvedVolumePieces(recursion, task, carried);

                // 接片接不进去的那几份，才在产出它的那一单自己的落点里补一份（顺序不许反过来）。
                host.PublishUnresolvedPieceLandingCopies(carried, task);
            }

            if (task.Outcome == TaskOutcome.Succeeded || hasPublishableProducts)
            {
                // 递归产物同样要走"校验 → 定稿 → 归集 → 源包处理"，与单层路径一个字都不差。
                bool recursionConclusionStands = await host.PostProcessSuccessAsync(
                    task,
                    engineArchivePath,
                    string.Empty,
                    engineOutputPath,
                    outputRedirectNote,
                    sharedOutputRoot,
                    oneClickRun,
                    cancellationToken,
                    knownList: null,
                    recursion: recursion);

                if (recursionConclusionStands && task.Outcome == TaskOutcome.Succeeded)
                {
                    host.AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
                }
            }

            /*
             * ⚠ 落地那一档**已经在上面搬过了**（拿到递归结果就搬 —— 必须在工作区被清掉之前）。
             * ⛔ 这里不要再搬一遍。
             */

            /*
             * ===== 递归这条路的密码类失败也要登记到本批（2026-10-05 只读审计）=====
             *
             * 现场：<see cref="RecordPasswordFailure"/> 过去只有单层路径的两个调用点，而这一支解完就 return ——
             * 于是出厂默认档（展开所有分支 ⇒ 内层包走递归）下，用户 2026-10-02 明确要的批末指路
             * （「可能本来就没有密码 / 也可能对的密码排在候选更靠后 ⇒ 去④页调顺序或调大上限」）
             * 与"本批 N 个包没能解开"整句**一次都不出现**。
             *
             * 判据**不在**这里重写：<see cref="RecordPasswordFailure"/> 自己就只认三个状态常量
             * （密码错误 / 密码可能不对也可能数据坏了 / 达到密码尝试上限），不是那一档就什么都不做 ——
             * 转调它 = 登记判据与批末分档仍然只有一处（§9.5）。
             */
            host.RecordPasswordFailure(task);

            return;
        }
    }
}

