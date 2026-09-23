using System;
using System.Runtime.InteropServices;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// 本机的**处理器拓扑**（只用来给"并发开几个"一句建议，不参与任何判断）。
    ///
    /// <para><b>为什么需要它</b>：并发解压的收益在物理核数附近就吃满了，再往上只有代价。
    /// 2026-09-22 的并行实测（真样本、三档、逐字节校验）给的就是这个形状：</para>
    /// <list type="bullet">
    /// <item><description>并发 1→4：吞吐 **3.08×**（保守口径 2.70×）；</description></item>
    /// <item><description>并发 4→8：只再快 **1.9%**，而单包耗时从 6.63 s 涨到 10.91 s（+108%）、
    /// 整机 CPU 均值从 48.7% 涨到 72.7%；</description></item>
    /// <item><description>根因是**6 物理核超订**：第 1 波 8 个并发的单包耗时 11.9–14.2 s，
    /// 降到 4 个立刻回到 5.9–6.8 s。</description></item>
    /// </list>
    ///
    /// <para><b>物理核 vs 逻辑核</b>：<see cref="Environment.ProcessorCount"/> 给的是**逻辑**处理器数
    /// （开了超线程时是物理核的两倍），拿它当"建议上限"会把超订当成正常。
    /// 所以这里用 <c>GetLogicalProcessorInformation</c> 数一遍 <c>RelationProcessorCore</c> 条目
    /// （每个物理核一条，超线程的兄弟逻辑核共用一条）；**取不到就如实退回逻辑核并注明** ——
    /// 不假装知道，也不编一个数字。</para>
    ///
    /// <para>只用 <c>kernel32</c> 的既有导出（P/Invoke），**不引任何 NuGet 依赖**；
    /// 任何异常一律收敛成"取不到"，绝不把一句建议变成一次崩溃。</para>
    /// </summary>
    public static class ProcessorTopology
    {
        /// <summary>物理核数；取不到时为 0（调用方必须按"取不到"处理，不要当 0 核）。</summary>
        public static int PhysicalCoreCount
        {
            get
            {
                // 只算一次：处理器拓扑在进程生命周期内不会变（热插拔 CPU 不在本程序的目标里）。
                if (_physicalCoreCount < 0)
                {
                    _physicalCoreCount = TryCountPhysicalCores();
                }

                return _physicalCoreCount;
            }
        }

        /// <summary>逻辑处理器数（<see cref="Environment.ProcessorCount"/>，永远拿得到）。</summary>
        public static int LogicalCoreCount => Environment.ProcessorCount;

        /// <summary>
        /// 设置界面 / 主界面那句建议（唯一文案来源）。
        ///
        /// <para>写成**静态属性**（而不是方法）是为了让 XAML 能用 <c>{x:Static}</c> 直接引用它 ——
        /// 界面上的那一句与日志里那一句因此不可能分叉（处理器拓扑在进程生命周期内不会变，
        /// 所以"窗口创建时算一次"与"每次现算"结果一样）。</para>
        ///
        /// <para>检测到物理核数时说物理核；取不到时**明说是逻辑核** ——
        /// 用户按这句话调并发，含糊的数字比没有更糟。</para>
        /// </summary>
        public static string ConcurrencyAdvice => DescribeConcurrencyAdvice();

        /// <summary>同一句话的方法形态（代码里读起来顺一点；实现只有这一处）。</summary>
        public static string DescribeConcurrencyAdvice()
        {
            int physical = PhysicalCoreCount;

            return physical > 0
                ? $"建议不超过物理核心数（当前检测到 {physical} 核）"
                : $"建议不超过物理核心数（取不到物理核数，按逻辑核 {LogicalCoreCount} 核估算）";
        }

        private static int _physicalCoreCount = -1;

        /// <summary>
        /// 数物理核：<c>GetLogicalProcessorInformation</c> 每个物理核返回一条
        /// <c>RelationProcessorCore</c>（超线程的两个逻辑核共用一条，所以它数出来的是核不是线程）。
        /// </summary>
        private static int TryCountPhysicalCores()
        {
            try
            {
                int length = 0;

                // 第一次调用只为拿长度：它会以 ERROR_INSUFFICIENT_BUFFER 失败，这是官方用法。
                GetLogicalProcessorInformation(IntPtr.Zero, ref length);

                if (length <= 0)
                {
                    return 0;
                }

                IntPtr buffer = Marshal.AllocHGlobal(length);

                try
                {
                    if (!GetLogicalProcessorInformation(buffer, ref length))
                    {
                        return 0;
                    }

                    int entrySize = Marshal.SizeOf<SystemLogicalProcessorInformation>();

                    if (entrySize <= 0)
                    {
                        return 0;
                    }

                    int count = 0;

                    for (int offset = 0; offset + entrySize <= length; offset += entrySize)
                    {
                        IntPtr entry = IntPtr.Add(buffer, offset);
                        var info = Marshal.PtrToStructure<SystemLogicalProcessorInformation>(entry);

                        if (info.Relationship == RelationProcessorCore)
                        {
                            count++;
                        }
                    }

                    return count > 0 ? count : 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch
            {
                /*
                 * 老系统、受限环境、被安全软件拦下、结构体布局对不上 —— 全部收敛成"取不到"。
                 * 这句话只是给用户的建议，绝不能因为它把设置窗口打不开。
                 */
                return 0;
            }
        }

        /// <summary><c>LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore</c>。</summary>
        private const int RelationProcessorCore = 0;

        /// <summary>
        /// <c>SYSTEM_LOGICAL_PROCESSOR_INFORMATION</c>。
        ///
        /// <para>字段顺序与大小必须与 Windows 头文件一致：
        /// <c>ULONG_PTR ProcessorMask</c> + <c>int Relationship</c> + 16 字节的联合体
        /// （<c>Reserved[2]</c> 是最宽的那一项）。<c>Sequential</c> 布局下 x64 会补 4 字节对齐，
        /// 正好等于原生结构的 32 字节（x86 是 24 字节）—— 所以同一个类型两边都能用。</para>
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct SystemLogicalProcessorInformation
        {
            public UIntPtr ProcessorMask;

            public int Relationship;

            public ulong Reserved0;

            public ulong Reserved1;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformation(
            IntPtr buffer,
            ref int returnedLength);
    }
}
