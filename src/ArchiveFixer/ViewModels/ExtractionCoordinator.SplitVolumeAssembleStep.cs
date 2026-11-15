using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 分卷组装（整组缺卷时按候选拼一份可读的；第 12 块）——实现已按职责搬到
/// <see cref="SplitVolumeAssembleHandler"/>（<c>ViewModels/Extraction/Steps/SplitVolumeAssembleHandler.cs</c>，
/// 与别的步骤 Handler 同一种写法：扁平命名空间、只持有宿主），本分片只留同签名薄转发。
///
/// <para>⛔ 本分片不再放任何判据：搬走的是**实现**，调用点（<c>ExtractionPreflight</c> /
/// <c>ExtractionCoordinator.ServiceForwarding</c> 与守门用例）一个字都没改。</para>
///
/// <para>静态成员保持原名与可见性：<c>IsOurWorkspaceSubdirectory</c> 仍被 <c>IWorkspaceStepHost</c>
/// 与守门用例直接调用；私有的 <c>TryResetAssemblyDirectory</c> 只有搬走的实现自己用，所以不留在分片
/// （ADR 0003：⛔ 不给没人调用的内部方法留空转发）。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    /// <summary>
    /// 分卷组装 —— 实现见 <see cref="SplitVolumeAssembleHandler.TryAssembleSplitVolumesAsync"/>；
    /// 判据、日志顺序、返回口径与搬出前逐字一致。
    /// </summary>
    internal Task<(string? FirstVolumePath, ArchiveListResult? List)> TryAssembleSplitVolumesAsync(
        ArchiveTask task,
        string carvedPath,
        IReadOnlyList<PasswordItem> candidates,
        CancellationToken cancellationToken) =>
        new SplitVolumeAssembleHandler(this).TryAssembleSplitVolumesAsync(
            task,
            carvedPath,
            candidates,
            cancellationToken);

    /*
     * 下面是 <c>SplitVolumeAssembleHandler</c> 需要的最小内部访问器：成员本体一个字节不动
     * （判据/写点仍只有原来那一处，可见性也没改），这里只把"读它"的口径提到 internal，
     * 供同程序集的协作类使用 —— ⛔ 不用反射、不新增第二套判断。
     */
    internal int MaxPreflightPasswordAttemptsForSplitVolumeAssemble => MaxPreflightPasswordAttempts;

    /// <summary>
    /// 这个子目录名是不是"我们自己造的"（决定清工作区时敢不敢整份删）—— 实现见
    /// <see cref="SplitVolumeAssembleHandler.IsOurWorkspaceSubdirectory"/>，白名单口径仍只有那一处。
    /// </summary>
    internal static bool IsOurWorkspaceSubdirectory(string name) =>
        SplitVolumeAssembleHandler.IsOurWorkspaceSubdirectory(name);
}
