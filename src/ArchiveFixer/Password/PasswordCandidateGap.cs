using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace ArchiveFixer.Password
{
    /// <summary>
    /// **候选被每层上限截断时，点名"哪几条没试到"**（用户 2026-10-05 真机第七批，AGENTS.md §11.5 最后一条）。
    ///
    /// <para><b>现场</b>：候选共 12 个（空密码 + 密码本 11 条）、每层上限 10 ⇒ **第 11 条从没被试过**，
    /// 而日志里只有一句「达到密码尝试上限」—— 用户根本不知道"到底是哪一条没试"，
    /// 也就没法判断"该不该把上限调大 / 该不该调顺序"。</para>
    ///
    /// <para>⛔ 点名用的是**既有那一个描述器**（<c>PasswordService.BuildTryPasswordLogText</c>，
    /// 形如「尝试密码列表第 11 项：******」）—— 本类只负责"取哪几条 + 折行"，
    /// ⛔ 不自己拼候选来源、⛔ 绝不让明文密码出现在这句话里（描述器输出的只有占位符 <c>******</c>）。</para>
    ///
    /// <para>⚠ 两句文案都必须**以点名结尾**（见 <see cref="StatusText.PasswordCandidateUntriedLogFormat"/>）：
    /// 脱敏器只认「描述：******」收尾这一种形状，尾巴上再挂一句话就会被整行擦成「尝试密码 ******」——
    /// 那正是这条修复要治的毛病。</para>
    ///
    /// <para>单层路与递归路**共用这一处**（§9.5）：两处各写一遍，迟早有一处忘了改。</para>
    /// </summary>
    public static class PasswordCandidateGap
    {
        /// <summary>最多点名几个（其余折成"还有 K 条"）——与批末诊断那一条同一个口径。</summary>
        public const int MaxNamed = 3;

        /// <summary>点名结果：有几条没试到 + 前几名的描述（保序、已脱敏）。</summary>
        public readonly record struct Gap(int UntriedCount, string Named);

        /// <summary>
        /// 「哪几条没试到」；没有没试到的候选时返回 <c>null</c>（调用方一个字都不写）。
        /// </summary>
        /// <param name="candidates">完整的候选表（**保序**；单层路是 <c>List&lt;PasswordItem&gt;</c>，递归路是值列表）。</param>
        /// <param name="firstUntriedIndex">第一条**没试到**的候选在表里的下标。</param>
        /// <param name="firstUntriedOrdinal">
        /// 第一条没试到的候选在**循环里会是第几项**（1 起）。
        /// ⚠ 两把尺子必须分开：递归路会把"空密码"跳过去（不占序号），那时下标与序号差着被跳过的个数，
        /// 拿下标当序号会与日志里已经出现过的那几行「候选 i/N」对不上。
        /// </param>
        /// <param name="describe">候选描述器：<c>(候选, 第几项)</c> → 脱敏说明（**必须**是既有那一个）。</param>
        public static Gap? Describe<T>(
            IReadOnlyList<T>? candidates,
            int firstUntriedIndex,
            int firstUntriedOrdinal,
            Func<T, int, string>? describe)
        {
            if (candidates == null || candidates.Count <= 0 || describe == null)
            {
                return null;
            }

            int from = Math.Max(0, firstUntriedIndex);

            if (from >= candidates.Count)
            {
                return null;
            }

            var named = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int index = from; index < candidates.Count && named.Count < MaxNamed; index++)
            {
                // 第几项：与循环里那一条候选日志**同一把尺子**（1 起，从"下一条会是第几项"开始数）。
                int ordinal = Math.Max(1, firstUntriedOrdinal) + (index - from);
                string described = describe(candidates[index], ordinal);

                if (!string.IsNullOrWhiteSpace(described) && seen.Add(described))
                {
                    named.Add(described);
                }
            }

            if (named.Count == 0)
            {
                return null;
            }

            int untried = candidates.Count - from;

            /*
             * ⛔ 这里**只列前几条、后面什么都不挂**：脱敏器只认「描述：******」收尾的已知安全形状，
             * 在点名后面再折一行"还有 K 条"会被整行擦掉（"一共几条"由文案前半句说）。
             */
            return new Gap(untried, string.Join("、", named));
        }

        /// <summary>日志里那一行（收尾那行；<c>{0}</c> = 任务名 / 层标签，<c>{1}</c> = 每层上限）。</summary>
        public static string BuildLogLine(string label, int attemptLimit, Gap gap)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                StatusText.PasswordCandidateUntriedLogFormat,
                label,
                gap.UntriedCount,
                attemptLimit,
                gap.Named);
        }

        /// <summary>结论里那一句（进任务的失败原因，用户才会在①页详情 / 失败清单里看见）。</summary>
        public static string BuildConclusionSuffix(Gap gap)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                StatusText.PasswordCandidateUntriedSuffixFormat,
                gap.UntriedCount,
                gap.Named);
        }
    }
}
