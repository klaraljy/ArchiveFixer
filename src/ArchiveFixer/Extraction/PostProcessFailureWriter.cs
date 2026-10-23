using System;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 「产物校验未通过 ⇒ 落解压失败」这一支的唯一出口（从 `ExtractionCoordinator` 搬出的第 8 个功能族，2026-10-23）。
    ///
    /// <para>它做四件事、顺序固定：① 状态/进度/原因/校验原因都写校验那一句；② 若"包没加密"则补一句
    /// （读机器事实 <see cref="ArchiveTask.PasswordStatus"/>）；③ 补"已试 N 个候选"与"候选结果完全相同"
    /// 两句（读任务上的事实字段、幂等）；④ 机器事实落 `Failed` —— ⛔ 不能停在"没通过校验"的含糊档，
    /// 调用方与汇总都靠这一档区分"校验判否"与"越界/超预算/定稿失败"。</para>
    /// </summary>
    public static class PostProcessFailureWriter
    {
        public static void Write(ArchiveTask task, string failureMessage, Action<string, string>? log = null)
        {
            if (task == null)
            {
                return;
            }

            task.Status = StatusText.ExtractFailed;
            task.ProgressText = StatusText.ProgressFailed;
            task.ErrorMessage = failureMessage;
            task.VerifyMessage = failureMessage;

            if (task.PasswordStatus == StatusText.PasswordNotNeeded)
            {
                task.ErrorMessage += "（这个包没有加密：清单里没有加密条目，换密码不会改变结果，问题不在密码上）";
            }

            if (task.RecursionTriedCandidateCount > 0
                && !task.ErrorMessage.Contains("产物始终不完整", StringComparison.Ordinal))
            {
                task.ErrorMessage += $"（已试 {task.RecursionTriedCandidateCount} 个候选，产物始终不完整）";
            }

            if (task.RecursionCandidatesIdentical
                && !task.ErrorMessage.Contains("完全相同", StringComparison.Ordinal))
            {
                task.ErrorMessage += "（连续两个候选解出来的产物完全相同，换密码不会改变结果）";
            }

            task.OutputVerification = OutputVerificationOutcome.Failed;
            task.Outcome = TaskOutcome.Failed;
            task.LastUpdatedTime = DateTime.Now;

            log?.Invoke(
                "ERROR",
                $"{task.LogName}：{failureMessage}；不落「解压成功」，源包一个字节都不动。");
        }
    }
}
