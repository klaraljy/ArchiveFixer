using System;
using System.Collections.Generic;

namespace ArchiveFixer.Storage
{
    /// <summary>危险模式下一次"彻底删其余物"的结论。</summary>
    public sealed class RestPurgeOutcome
    {
        /// <summary>是否真的执行过删除（被门槛挡下 / 没有可删的 → false，此时一个字节都没动）。</summary>
        public bool Attempted { get; init; }

        /// <summary>是否**确实释放了空间**（危险模式的全部意义就在这里：净占用基本不变）。</summary>
        public bool Succeeded { get; init; }

        /// <summary>删掉的其余物目录（没删时为空）。</summary>
        public string Directory { get; init; } = string.Empty;

        /// <summary>彻底删除真正释放的字节数。</summary>
        public long FreedBytes { get; init; }

        /// <summary>顶层条目数。</summary>
        public int EntryCount { get; init; }

        /// <summary>
        /// **这一档是"空壳就地删"**（用户 2026-10-03）：其余物里递归地一个文件都没有 ⇒ 直接
        /// <c>Directory.Delete(recursive: true)</c> 掉，**一个字节都没进回收站**。
        ///
        /// <para>为什么要这个事实位、而不是让调用方看 <c>EntryCount == 0 &amp;&amp; FreedBytes == 0</c>：
        /// 那两个数**证明不了**"没进回收站"（别的原因也能是 0），而调用方要据此选文案 ——
        /// 选了「移入回收站」档时照旧写"已移入回收站"就是一句**假话**
        /// （AGENTS.md §9.5：要删 / 要写盘的动作判据只准读事实，⛔ 不许比数字猜）。</para>
        /// </summary>
        public bool RemovedAsEmptyShell { get; init; }

        /// <summary>一句话结论（进日志与任务详情）。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>删除日志（路径 + 理由 + 条目数 + 总大小），调用方原样写进界面/文件日志。</summary>
        public IReadOnlyList<string> LogLines { get; init; } = Array.Empty<string>();
    }
}
