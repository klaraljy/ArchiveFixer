using System;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// 并发档的唯一合成结果：用户档 + 空间建议 → 实际允许的并发。
    /// 下游（调度/计划/日志）只读这一份，不再各自 Math.Min。
    /// </summary>
    public sealed record ConcurrencyLimit(int Requested, int Recommended)
    {
        public const int Minimum = 1;
        public const int Maximum = 8;

        public int Effective
        {
            get
            {
                int requested = Math.Clamp(Requested, Minimum, Maximum);
                int recommended = Recommended <= 0 ? requested : Recommended;
                return Math.Clamp(Math.Min(requested, recommended), Minimum, Maximum);
            }
        }

        public static ConcurrencyLimit Create(int requested, int recommended) =>
            new(requested, recommended);
    }
}
