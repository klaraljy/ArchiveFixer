using System;
using ArchiveFixer.Helpers;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 时间源出口（2026-11-10 审计：106 处 DateTime.Now 散在 34 个文件，时间相关用例没法钉住时间）。
    /// </summary>
    public class ClockTests
    {
        [Fact]
        public void 没冻结时_就是当前本地时间()
        {
            DateTime before = DateTime.Now;
            DateTime value = Clock.Now;
            DateTime after = DateTime.Now;

            Assert.InRange(value, before.AddSeconds(-1), after.AddSeconds(1));
        }

        [Fact]
        public void 冻结后_返回固定值()
        {
            var frozen = new DateTime(2026, 3, 4, 5, 6, 7);
            using (Clock.Freeze(frozen))
            {
                Assert.Equal(frozen, Clock.Now);
                Assert.Equal(frozen, Clock.Now);
            }
        }

        [Fact]
        public void Dispose后_恢复真实时间()
        {
            var frozen = new DateTime(2026, 3, 4, 5, 6, 7);
            using (Clock.Freeze(frozen))
            {
                Assert.Equal(frozen, Clock.Now);
            }

            Assert.NotEqual(frozen, Clock.Now);
        }

        [Fact]
        public void 嵌套冻结_内层结束后回到外层()
        {
            var outer = new DateTime(2026, 1, 1);
            var inner = new DateTime(2026, 2, 2);

            using (Clock.Freeze(outer))
            {
                Assert.Equal(outer, Clock.Now);

                using (Clock.Freeze(inner))
                {
                    Assert.Equal(inner, Clock.Now);
                }

                Assert.Equal(outer, Clock.Now);
            }
        }
    }
}
