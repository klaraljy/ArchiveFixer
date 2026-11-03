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
                /*
                 * 用户档是硬上限；空间建议只作为 plan.Recommended 供调度/日志使用，
                 * 不能在这里把普通批悄悄降成 1（那会让空间门排队测试与真实并发都变样）。
                 */
                return Math.Clamp(Requested, Minimum, Maximum);
            }
        }

        public static ConcurrencyLimit Create(int requested, int recommended) =>
            new(requested, recommended);
    }
}
