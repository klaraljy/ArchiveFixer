using System;
using ArchiveFixer.Extraction;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// **状态 → 成败**的唯一分类出口（AGENTS.md §9.5：同一件事的真值只允许有一个出口）。
    ///
    /// <para><b>为什么必须有它</b>（用户 2026-09-27 真机 `giu.7z.001`）：同一个任务在三处被
    /// 判成了三种结果 —— ①页说「部分完成」、一键汇总说「未处理 1」、批末诊断说「下一步：其他」。
    /// 根子是**同一份失败名单在三个地方各写了一遍**，而其中一处漏了一条就会立刻表现为口径打架
    /// （AGENTS.md §11.5 那句"失败名单有两处，必须一致"就是当时为此写的）。</para>
    ///
    /// <para>现在名单只在这里一份：<c>OneClickCoordinator</c> 与 <c>TaskSummaryService</c>
    /// 都转调本类，⛔ 谁都不许再自己写一份 <c>status is …</c> 的名单。</para>
    ///
    /// <para>⚠ 判据只读**状态常量**与**机器终态**（<see cref="TaskOutcome"/>），
    /// ⛔ 绝不比对中文文案的"意思"（§7）。状态常量本身就是英文标识符形式的中文串，
    /// 拿它做 switch 是既有口径（既有名单也都这么写），比的是**标识**不是文案内容。</para>
    /// </summary>
    public static class TaskOutcomeClassifier
    {
        /// <summary>
        /// 处置本身就没法开始 / 中途停下、且**这一单没拿到产物**的那些状态：
        /// 解压失败 / 权限 / 冲突 / 缺卷 / 路径过长 / 引擎缺失 / 达到上限 / 空间不足 /
        /// 源文件已变化 / 改名失败 / 测试失败 / 未知错误。
        ///
        /// <para>「源文件已变化」与「磁盘空间不足」都在里面：引擎一次都没被调用，
        /// 但这是一次**正常的失败终态** —— 用户要处理的是解压侧的动作（重新扫描 / 清空间），
        /// 统计上必须算"失败"，否则分项之和与任务数对不上。</para>
        /// </summary>
        public static bool IsFailureStatus(string? status) => status is
            StatusText.ExtractFailed or
            StatusText.WrongPassword or
            StatusText.Corrupted or
            StatusText.PasswordOrCorrupted or
            StatusText.AccessDenied or
            StatusText.OutputConflict or
            StatusText.VolumeMissing or
            StatusText.PathTooLong or
            StatusText.SevenZipMissing or
            StatusText.NoEngineAvailable or
            StatusText.PasswordAttemptLimitReached or
            StatusText.DiskSpaceInsufficient or
            StatusText.SourceChanged or
            StatusText.UnknownError or
            StatusText.RenameFailed or
            StatusText.TestFailed;

        /// <summary>
        /// **解压侧**没拿到产物的那些状态（= <see cref="IsFailureStatus"/> 去掉改名 / 测试
        /// 与"密码错误 / 文件损坏"这两种**各自单独成桶**的档）。
        ///
        /// <para>①页那三格「解压失败 / 密码错误 / 文件损坏」靠它分桶；名字沿用既有的
        /// <c>TaskSummaryService.IsExtractFailureStatus</c>（那个私有实现已删，转调这里）。</para>
        /// </summary>
        public static bool IsExtractFailureStatus(string? status) => status is
            StatusText.ExtractFailed or
            StatusText.PasswordOrCorrupted or
            StatusText.AccessDenied or
            StatusText.OutputConflict or
            StatusText.VolumeMissing or
            StatusText.PathTooLong or
            StatusText.SevenZipMissing or
            StatusText.NoEngineAvailable or
            StatusText.UnknownError or
            StatusText.PasswordAttemptLimitReached or
            StatusText.DiskSpaceInsufficient or
            StatusText.SourceChanged;

        /// <summary>
        /// 解压成功（含"已覆盖"）**且产物校验没有判否**。
        ///
        /// <para>⚠ 只用于统计与显示：凡"要不要删源 / 搬源 / 继续往下解"的裁决一律读
        /// <see cref="ArchiveTask.OutputVerification"/> 与 <see cref="ArchiveTask.Outcome"/>。</para>
        /// </summary>
        public static bool IsSuccessStatus(ArchiveTask? task)
        {
            if (task == null || task.OutputVerification == OutputVerificationOutcome.Failed)
            {
                return false;
            }

            return task.Status == StatusText.ExtractSuccess ||
                   task.Status == StatusText.Overwritten;
        }

        /// <summary>
        /// 递归停在半路时，那些**状态本身说不清是哪一档**的停因（内容类）：换密码没有帮助，
        /// 或用户自己喊停。返回 true 时 <paramref name="status"/> / <paramref name="outcome"/>
        /// 已经定好；<c>NeedsDecision</c>（多分支默认不展开）等"正常收起"的停因返回 false，
        /// 由调用方按既有口径落「部分完成」。
        /// </summary>
        public static bool TryResolveRecursionStop(
            RecursionStopReason stopReason,
            bool producedAnyLayerOutput,
            out string status,
            out TaskOutcome outcome)
        {
            status = string.Empty;
            outcome = TaskOutcome.Failed;

            switch (stopReason)
            {
                case RecursionStopReason.WrongPassword:
                case RecursionStopReason.PasswordAttemptsExceeded:

                /*
                 * 文件名已加密（-mhe / -hp）：与密码那两档**同一处置**（一个字节都没产出就是失败、
                 * 产出过东西才叫部分完成），但**状态另算**（见下面那张表）——
                 * 用户要做的动作不同："先给它一个密码"而不是去翻密码本。
                 */
                case RecursionStopReason.EncryptedHeaders:
                case RecursionStopReason.Corrupted:
                case RecursionStopReason.PasswordOrCorrupted:
                case RecursionStopReason.EngineFailed:
                case RecursionStopReason.UnsafeEntry:
                case RecursionStopReason.DiskSpaceInsufficient:
                    /*
                     * **什么都没产出 ⇒ 这是失败，不是"部分完成"**（用户 2026-09-27 真机）：
                     * `giu.7z.001` 那一单一个文件都没解出来，①页却写「部分完成」——
                     * 用户会去暂存目录里找根本不存在的产物。反过来"解出来一半再失败"
                     * 仍然是「部分完成」（不变量 6 的反面同样成立）。
                     */
                    if (producedAnyLayerOutput)
                    {
                        status = StatusText.PartiallyCompleted;
                        outcome = TaskOutcome.PartiallyCompleted;
                        return true;
                    }

                    status = stopReason switch
                    {
                        RecursionStopReason.WrongPassword => StatusText.WrongPassword,
                        RecursionStopReason.PasswordAttemptsExceeded => StatusText.PasswordAttemptLimitReached,

                        /*
                         * 文件名已加密：与单层路径**逐字同一个状态**（`StatusText.EncryptedHeaders`，
                         * 值 = 文件名已加密）—— 单层路 2026-09-26 就落它，递归路 2026-10-05 才对齐
                         * （用户拍板）。⛔ 不许折成"密码错误"：那会把用户指去翻密码本。
                         */
                        RecursionStopReason.EncryptedHeaders => StatusText.EncryptedHeaders,
                        RecursionStopReason.Corrupted => StatusText.Corrupted,

                        /*
                         * 两义那一档（引擎既说密码不对、又说数据坏了）：状态写两种可能，
                         * ⛔ 不许折成上面任意一档 —— 折哪一档都是"单独定原因"，两条都错
                         * （用户 2026-09-30 真机；与 7-Zip 侧同一口径）。
                         */
                        RecursionStopReason.PasswordOrCorrupted => StatusText.PasswordOrCorrupted,

                        /*
                         * 写不下盘：归档本身没问题，用户要做的是清空间 / 换盘 ——
                         * 落「磁盘空间不足」而不是「解压失败」，批末诊断才会把它归到空间那一组
                         * （用户 2026-09-27 真机：递归内层包撞空间不足，诊断却归"其他"）。
                         */
                        RecursionStopReason.DiskSpaceInsufficient => StatusText.DiskSpaceInsufficient,

                        // 产物越界 / 危险条目（不变量 4）：与"引擎解不开"同一档 —— 结论不成立。
                        _ => StatusText.ExtractFailed
                    };

                    outcome = TaskOutcome.Failed;
                    return true;

                default:
                    return false;
            }
        }
    }
}
