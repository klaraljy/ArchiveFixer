using System.Text;
using System.Threading;

namespace ArchiveFixer.Helpers
{
    /// <summary>
    /// 把 <b>代码页编码</b>（GBK / GB18030 / 936 这类）注册进 .NET。
    ///
    /// <para><b>为什么必须有这一步</b>：.NET Core 起，<c>Encoding.GetEncoding("GB18030")</c> 默认**抛异常** ——
    /// 代码页编码在 <c>System.Text.Encoding.CodePages</c> 里，要先显式注册。不注册的后果是
    /// **静默降级**：密码本那条回退路径会落到 <see cref="Encoding.Default"/>（.NET Core 上就是 UTF-8），
    /// 于是**中文 Windows 下用记事本存的 GBK 密码本会被读成乱码**，用户只会看到"密码全都不对"。</para>
    ///
    /// <para><b>为什么做成幂等的一次性初始化</b>：注册是进程级、全局、只生效一次的动作。
    /// 以前它散在打包的进程读取器里（<c>PackProcessRunner</c> 按控制台代码页解码输出时顺手注册），
    /// 结果是"谁先跑到那一步"决定了别处 GB18030 能不能用 —— 测试里就真的因此出现过**顺序相关的偶发失败**。
    /// 现在收敛成这一个入口：启动时调一次，用到它的地方（密码本解析、打包输出解码）也各自确保一次。</para>
    /// </summary>
    public static class CodePageEncodingBootstrap
    {
        private static int _registered;

        /// <summary>当前进程有没有成功注册代码页编码（供诊断/测试查询）。</summary>
        public static bool IsRegistered => Volatile.Read(ref _registered) == 2;

        /// <summary>
        /// 确保代码页编码可用。**可以随便重复调用**（幂等、无锁竞争、失败也不抛）。
        ///
        /// <para>失败不抛：注册不上只是"拿不到 GBK"，调用方本来就都有回退路径 ——
        /// 一个可选能力不该把程序启动搞崩。</para>
        /// </summary>
        public static void EnsureRegistered()
        {
            if (Volatile.Read(ref _registered) != 0)
            {
                return;
            }

            int result = 2;

            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

                // 真取一次才算数：注册成功但系统缺代码页时，这里仍然会抛。
                _ = Encoding.GetEncoding("GB18030");
            }
            catch
            {
                result = 1;
            }

            Interlocked.CompareExchange(ref _registered, result, 0);
        }
    }
}
