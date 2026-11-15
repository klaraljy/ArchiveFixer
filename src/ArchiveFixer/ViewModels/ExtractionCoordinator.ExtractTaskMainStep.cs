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

/// <summary>任务解压主流程（调度执行/单任务处理/单任务解压主体；第 12 块，同一 partial 类）。</summary>
internal sealed partial class ExtractionCoordinator
{
    /// <summary>
    /// 跑一个任务：解压 → （空间不足模式下当场删源包）→ 其余物按档处理 → 释放预留 + 刷新可用空间。
    ///
    /// <para>⚠ 顺序是刻意的：删除发生在**任务收尾之后、释放预留之前**。
    /// 这样"删源包回收回来的空间"会出现在下一个任务的空间门判断里（这正是那个模式能解决空间不够的原因），
    /// 而账本上的预留仍然按"这些字节还在盘上"来算，绝不会提前把它许给别的任务。</para>
    /// </summary>
    // 任务生命周期（RunScheduledTaskAsync / ProcessExtractTaskAsync）已搬到 ExtractionTaskRunner。

    internal Task ExtractSingleTaskAsync(ArchiveTask task, CancellationToken cancellationToken, bool oneClickRun) =>
        new ExtractTaskMainHandler(this).RunAsync(task, cancellationToken, oneClickRun);

    /*
     * 下面是 <c>ExtractTaskMainHandler</c> 需要的最小内部访问器：成员本体一个字节不动
     * （判据/写点仍只有原来那一处，可见性也没改），这里只把"读它/调它"的口径提到 internal，
     * 供同程序集的协作类使用 —— ⛔ 不用反射、不新增第二套判断。
     */
    internal string GlobalPasswordForExtractMain => GlobalPassword;

    internal int MaxPasswordAttemptsPerLayerForExtractMain => MaxPasswordAttemptsPerLayer;

    internal int MaxPreflightPasswordAttemptsForExtractMain => MaxPreflightPasswordAttempts;

    internal void RegisterUnrecognizedSuspectForExtractMain(ArchiveTask task) =>
        RegisterUnrecognizedSuspect(task);

    internal void InsertManualPasswordCandidatesForExtractMain(List<PasswordItem> candidates) =>
        InsertManualPasswordCandidates(candidates, _passwordFailureRegistry.ManualBatchPasswords);

    internal void DiscardStaleCarvedArtifactForExtractMain(ArchiveTask task) =>
        DiscardStaleCarvedArtifact(task);

    internal string BuildEmbeddedArchivePathForExtractMain(ArchiveTask task) =>
        BuildEmbeddedArchivePath(task);

    internal string AlignCarvedArchiveExtensionForExtractMain(ArchiveTask task, string carvedPath) =>
        AlignCarvedArchiveExtension(task, carvedPath);

    /// <summary>
    /// ⛔ 真正的 APK 不碰：不解压、不递归、不改名（用户 2026-10-10 拍板）。判据 = 内容证据，
    /// 唯一出口 <c>AndroidPackageDetector</c>；判不出 ⇒ 照旧照常处理。
    /// </summary>
    /// <returns>true = 这一单到此为止（状态与终态已经写好）。</returns>
    /// <summary>
    /// ⛔ 真正的 APK 不碰：不解压、不递归、不改名（用户 2026-10-10 拍板）。判据 = 内容证据，
    /// 唯一出口 <c>AndroidPackageDetector</c>；判不出 ⇒ 照旧照常处理。
    /// </summary>
    /// <returns>true = 这一单到此为止（状态与终态已经写好）。</returns>

}
