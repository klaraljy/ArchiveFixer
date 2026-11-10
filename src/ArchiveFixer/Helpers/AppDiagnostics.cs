using System;

namespace ArchiveFixer.Helpers
{
    /// <summary>
    /// 危险路径上的「吞掉异常」出口。
    ///
    /// <para>
    /// 背景（2026-11-10 审计）：全项目 161 个 catch 既不记录也不重抛。绝大多数是格式嗅探，
    /// 吞掉是对的；但**删其余物 / 清工作区 / 写密码本 / 写设置 / 回收站**这些路径上吞掉，
    /// 意味着"用户以为处理完了，其实没删/没存"。
    /// </para>
    ///
    /// <para>
    /// ⛔ 这里**只记录，不改控制流**：调用方仍旧按原逻辑降级返回。
    /// 没有宿主设置 sink 时是纯 no-op，所以单元测试不受影响。
    /// </para>
    /// </summary>
    internal static class AppDiagnostics
    {
        /// <summary>由 <c>MainViewModel</c> 在初始化时接到日志上；为空时不产生任何输出。</summary>
        internal static Action<string, Exception>? ProblemSink { get; set; }

        /// <summary>
        /// 记录一次「操作失败但被降级处理」。
        /// </summary>
        /// <param name="area">哪一块（用于在日志里一眼看出位置，例如「其余物」「工作区」）。</param>
        /// <param name="operation">具体在做什么（例如「删除片」「清理目录」）。</param>
        /// <param name="ex">被吞掉的异常。</param>
        internal static void Problem(string area, string operation, Exception ex)
        {
            Action<string, Exception>? sink = ProblemSink;
            if (sink == null)
            {
                return;
            }

            try
            {
                sink(area + "·" + operation, ex);
            }
            catch
            {
                // 记录失败绝不影响主流程。
            }
        }
    }
}
