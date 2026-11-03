using System;
using System.Collections.Generic;

namespace ArchiveFixer.Password
{
    /// <summary>
    /// 一次密码候选循环的唯一账本。
    ///
    /// <para>三类结果必须分清：<see cref="Tried"/> 真的拿密码解过（占一次上限）、
    /// <see cref="Skipped"/> 按规则不试（不占次数）、<see cref="NotCounted"/> 明确不记账的原因。
    /// 调用方不再手工 `attempts++/--`。</para>
    /// </summary>
    public sealed class PasswordAttemptLedger
    {
        private readonly List<string> _skippedReasons = new();
        private readonly List<string> _notCountedReasons = new();

        public PasswordAttemptLedger(int maxAttempts)
        {
            MaxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
        }

        public int MaxAttempts { get; }

        public int Tried { get; private set; }

        public int Skipped { get; private set; }

        public int NotCounted { get; private set; }

        public bool CanTry => Tried < MaxAttempts;

        public IReadOnlyList<string> SkippedReasons => _skippedReasons;

        public IReadOnlyList<string> NotCountedReasons => _notCountedReasons;

        /// <summary>真的尝试一个候选；已经到上限时返回 false，且不记账。</summary>
        public bool Try()
        {
            if (!CanTry)
            {
                return false;
            }

            Tried++;
            return true;
        }

        /// <summary>按规则跳过（空密码 / 加密头等），不占次数。</summary>
        public void Skip(string reason)
        {
            Skipped++;
            _skippedReasons.Add(reason ?? string.Empty);
        }

        /// <summary>明确“这次不算尝试”，并留下原因（例如引擎不可用、候选来源异常）。</summary>
        public void NotCount(string reason)
        {
            NotCounted++;
            _notCountedReasons.Add(reason ?? string.Empty);
        }

        public string Describe() =>
            $"试过 {Tried}/{MaxAttempts}，跳过 {Skipped}，不计次 {NotCounted}";
    }
}
