using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction;

/// <summary>
/// **试开引擎调用**：把"列一次目录""完整测试一遍"翻译成 <see cref="IArchiveEngine"/> 的请求。
///
/// <para>本类是试开这一层**唯一**碰引擎的地方：请求怎么挂进度/失速回调、列目录用
/// <see cref="ArchiveRequest.For(string, string?)"/> 的哪种密码形态（列目录 = null，
/// 测试 = 空串/候选密码），都只在这里回答，⛔ 不把引擎参数拼进上层。</para>
///
/// <para>生命周期：每个 <see cref="VolumeProbeVerifier"/> 一个实例；只读、无状态地转发到引擎，
/// ⛔ 不缓存引擎结果、⛔ 不判结果、⛔ 不落盘。密码只作为参数在内存里传递，⛔ 不进日志/结论。</para>
/// </summary>
internal sealed class VolumeProbeEngineCalls
{
    private readonly IArchiveEngine _engine;

    internal VolumeProbeEngineCalls(IArchiveEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    /// <summary>当前有没有可用的解压引擎（不可用 ⇒ 一次引擎调用都不做）。</summary>
    internal bool IsAvailable => _engine.IsAvailable;

    /// <summary>列一次目录（试开的核心证据：能列出条目才算这一组成立）。</summary>
    internal Task<ArchiveListResult> ListAsync(
        string archivePath,
        IProgress<ArchiveProgress>? progress,
        Action<ArchiveStallNotice>? stalled,
        CancellationToken cancellationToken)
    {
        ArchiveRequest request = ArchiveRequest.For(archivePath, null);
        request.Progress = progress;
        request.Stalled = stalled;

        return _engine.ListAsync(request, cancellationToken);
    }

    /// <summary>完整测试一遍（顺序验证用：逐条目 CRC 才对得上）。</summary>
    internal Task<ArchiveOperationResult> TestAsync(
        string archivePath,
        string? password,
        IProgress<ArchiveProgress>? progress,
        Action<ArchiveStallNotice>? stalled,
        CancellationToken cancellationToken)
    {
        ArchiveRequest request = ArchiveRequest.For(archivePath, password);
        request.Progress = progress;
        request.Stalled = stalled;

        return _engine.TestAsync(request, cancellationToken);
    }
}
