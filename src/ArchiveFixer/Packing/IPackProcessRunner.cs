using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Packing
{
    /// <summary>
    /// "起一次打包用的外部进程"这件事的抽象。
    ///
    /// <para>为什么要有接口：<see cref="PackingService"/> 里那些**判据**（没有 Rar.exe 时不许产生 rar、
    /// 取消时不许留半个 rar、第二步失败时第一步的分卷必须还在）都可以在不碰真 WinRAR 的前提下验证 ——
    /// 验收明确要求"用假的 runner 断言它没被调用"（docs/打包功能.md §9）。</para>
    /// </summary>
    public interface IPackProcessRunner
    {
        Task<PackStepResult> RunAsync(
            PackToolKind tool,
            IReadOnlyList<string> arguments,
            string usedPassword,
            IProgress<PackStepProgress>? progress,
            CancellationToken cancellationToken,
            string? workingDirectory = null);
    }
}
