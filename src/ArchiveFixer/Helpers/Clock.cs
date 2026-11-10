using System;

namespace ArchiveFixer.Helpers
{
    /// <summary>
    /// 全项目统一的时间源出口。
    ///
    /// <para>
    /// 背景（2026-11-10 审计）：产品代码里有 106 处 <c>DateTime.Now</c> 散落在 34 个文件。
    /// 后果有两个：时间相关的用例没法把时间钉住（只能写"接近现在"这种弱断言），
    /// 时间戳格式也各自为政。收敛到一个出口之后，这两件事都有地方管。
    /// </para>
    ///
    /// <para>
    /// ⛔ 用法限制：只用于**取值**。不要在这里做格式化、不要缓存结果、不要拿它做业务判断之外的副作用。
    /// </para>
    /// </summary>
    internal static class Clock
    {
        private static Func<DateTime>? _override;

        /// <summary>当前本地时间。没有冻结时等价于 <see cref="DateTime.Now"/>。</summary>
        internal static DateTime Now => (_override ?? DefaultNow)();

        /// <summary>
        /// 把时间钉住，返回的对象 Dispose 时恢复。
        /// 测试里用 <c>using (Clock.Freeze(new DateTime(2026, 1, 1)))</c>。
        /// </summary>
        internal static IDisposable Freeze(DateTime value)
        {
            Func<DateTime>? previous = _override;
            _override = () => value;
            return new Restore(() => _override = previous);
        }

        private static DateTime DefaultNow() => DateTime.Now;

        private sealed class Restore : IDisposable
        {
            private Action? _restore;

            internal Restore(Action restore)
            {
                _restore = restore;
            }

            public void Dispose()
            {
                Action? restore = _restore;
                _restore = null;
                restore?.Invoke();
            }
        }
    }
}
