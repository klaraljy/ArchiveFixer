using System;

namespace ArchiveFixer.Engines;

/// <summary>
/// 引擎一次运行的"进度与卡住提示"接收端。
///
/// <para>
/// 为什么单独一个类型而不是直接给 <see cref="ArchiveRequest"/>：
/// 运行器（<c>SevenZipProcessRunner</c> / <c>UnRarProcessRunner</c>）的公开方法收的是
/// "路径 + 选项 + 令牌"，把它们全换成 <see cref="ArchiveRequest"/> 会动到大量既有调用点与测试。
/// 这个类型只承载"往哪里报"，与请求本身的参数解耦。
/// </para>
///
/// <para>
/// <see cref="Progress"/> 由引擎层**节流之后**调用（见 <see cref="ArchiveProgressReporter"/>），
/// 所以接收端可以放心地往 UI 线程投递，不必自己再去重。
/// </para>
/// </summary>
public sealed class EngineProgressContext
{
    /// <summary>进度接收端；null = 不报进度（但仍然会从日志里剔除进度噪声）。</summary>
    public IProgress<ArchiveProgress>? Progress { get; init; }

    /// <summary>"长时间无输出"提示；null = 不提。</summary>
    public Action<ArchiveStallNotice>? Stalled { get; init; }

    /// <summary>多久没有输出算"长时间无响应"。默认 90 秒（见 <see cref="EngineOutputActivityMonitor.DefaultStallThreshold"/>）。</summary>
    public TimeSpan StallThreshold { get; init; } = EngineOutputActivityMonitor.DefaultStallThreshold;

    /// <summary>从 <see cref="ArchiveRequest"/> 取出接收端；请求没带任何跟踪需求时返回 null。</summary>
    public static EngineProgressContext? From(ArchiveRequest? request)
    {
        if (request == null)
        {
            return null;
        }

        if (request.Progress == null && request.Stalled == null)
        {
            return null;
        }

        return new EngineProgressContext
        {
            Progress = request.Progress,
            Stalled = request.Stalled,
            StallThreshold = request.StallThreshold
        };
    }
}
