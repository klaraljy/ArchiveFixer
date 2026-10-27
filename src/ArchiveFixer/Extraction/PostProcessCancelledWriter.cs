using System;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 「用户已取消 ⇒ 不落解压失败」这一支的唯一出口（从 `ExtractionCoordinator` 搬出的第 9 个功能族，2026-10-23）。
    ///
    /// <para>取消不是失败：状态写「已取消」、机器终态 `Cancelled`、输出校验 `NotAttempted`；
    /// 原因沿用校验那一句（用户要知道停在哪），并写一行 INFO 说明"源包一个字节都不动"。</para>
    /// </summary>
    public static class PostProcessCancelledWriter
    {
        public static void Write(ArchiveTask task, string failureMessage, Action<string, string>? log = null)
        {
            if (task == null)
            {
                return;
            }

            task.Status = StatusText.Cancelled;
            task.ProgressText = StatusText.Cancelled;
            task.ErrorMessage = failureMessage;
            task.VerifyMessage = failureMessage;
            task.OutputVerification = OutputVerificationOutcome.NotAttempted;
            task.OutputManifestCrossChecked = false;
            task.Outcome = TaskOutcome.Cancelled;
            task.LastUpdatedTime = DateTime.Now;

            log?.Invoke(
                "INFO",
                $"{task.LogName}：用户已取消 —— 这次不落「解压失败」（{failureMessage}；源包一个字节都不动）。");
        }
    }
}
