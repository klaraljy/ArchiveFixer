using ArchiveFixer.Extraction;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Services;

/// <summary>
/// 清理功能的**注入项装配点**：<see cref="IDeleteExecutor"/> 与 <see cref="IDeleteFileSystemProbe"/>
/// 只在这里合成 <see cref="RecycleBinService"/> 与 <see cref="EmptyFolderCleaner"/>，
/// 预览、其余物执行、空文件夹清理三处收的是**同一份**装配。
///
/// <para>
/// 为什么必须只有一个装配点：<see cref="RecycleBinService"/> 的安全前置与 <see cref="EmptyFolderCleaner"/>
/// 的扫描用的是同一个探针 —— 探针若分成两份，"扫描看到的"和"删除时校验的"就是两个世界
/// （单测里尤其明显，见 <see cref="EmptyFolderCleaner"/> 的类注释）。
/// 默认实现的选择留在 <see cref="MaintenanceCleanupService"/> 的构造函数里（那里是组合根），
/// 本类只负责"把同一对注入项装成协作类要用的那两个对象"。
/// </para>
/// </summary>
public sealed class CleanupDeleteServices
{
    private readonly IDeleteExecutor _executor;
    private readonly IDeleteFileSystemProbe _probe;

    /// <param name="executor">真正动手的实现；默认走 Shell 回收站 + .NET 彻底删除。</param>
    /// <param name="probe">安全检查与扫描共用的文件系统探针；单测可注入假探针。</param>
    public CleanupDeleteServices(IDeleteExecutor executor, IDeleteFileSystemProbe probe)
    {
        _executor = executor;
        _probe = probe;
    }

    /// <summary>
    /// 删除服务。预览时的度量与执行时的删除都走它 —— 两处必须是同一段代码，
    /// 否则"预览报的数字"和"删除日志里的数字"会互相打架，用户只能自己猜哪个是真的。
    /// </summary>
    public RecycleBinService CreateDeleteService()
    {
        return new RecycleBinService(_executor, logSink: null, probe: _probe);
    }

    /// <summary>空文件夹清理器：与 <see cref="CreateDeleteService"/> 用同一个探针与执行器。</summary>
    public EmptyFolderCleaner CreateEmptyFolderCleaner()
    {
        return new EmptyFolderCleaner(CreateDeleteService(), _probe);
    }
}
