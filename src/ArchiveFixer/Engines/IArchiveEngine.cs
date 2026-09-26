using ArchiveFixer.Models;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// 归档引擎的统一接口（设计.md §十七）。
    ///
    /// 核心模块（GUI / 调度器 / 递归核心）**只允许**通过这个接口做事，
    /// 不允许出现：直接拼 7z.exe 参数、直接解析 7-Zip 文本输出、在 GUI 里判断 7-Zip 错误字符串、
    /// 在递归逻辑里写死某个引擎的参数（AGENTS.md §3.1 的四条禁止项）。
    ///
    /// 现在只有一个实现（<c>SevenZipEngine</c>），但骨架必须**在只有一个引擎的时候就成立** ——
    /// 否则第二个引擎永远加不进来。
    /// </summary>
    public interface IArchiveEngine
    {
        /// <summary>稳定标识，例如 "sevenzip"。写进任务报告，用于"结果可追溯"。</summary>
        string Id { get; }

        /// <summary>给人看的名字，例如 "7-Zip 命令行"。</summary>
        string DisplayName { get; }

        /// <summary>引擎版本（取不到就是 "unknown"）。</summary>
        string Version { get; }

        /// <summary>当前是否可用（例如外部程序没装/被删）。不可用时所有操作都必须安全失败，不许抛。</summary>
        bool IsAvailable { get; }

        EngineCapabilities Capabilities { get; }

        /// <summary>低成本探测：只读文件头就能回答的部分。不做完整解析，不要在这里解压。</summary>
        Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default);

        /// <summary>列出条目。资源预算与路径预检都依赖它（M5）。</summary>
        Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default);

        /// <summary>完整性测试（7z 的 t 命令）。</summary>
        Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default);

        /// <summary>解压。失败一律通过返回值表达，不要靠异常。</summary>
        Task<ArchiveOperationResult> ExtractAsync(
            ArchiveRequest request,
            ExtractOptions options,
            CancellationToken cancellationToken = default);
    }
}
