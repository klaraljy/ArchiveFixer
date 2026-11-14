using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.Diagnostics;
using System.IO;

namespace ArchiveFixer.Services;

/// <summary>
/// 路径服务。
/// 负责输出目录生成、安全文件夹名、路径冲突处理、打开目录等。
/// </summary>
public class PathService
{
    /// <summary>
    /// 程序根目录。
    /// </summary>
    public string AppBaseDirectory => AppContext.BaseDirectory;

    /// <summary>
    /// 用户数据目录（固定为 <c>%LOCALAPPDATA%\ArchiveFixer</c>）。
    /// </summary>
    public string DataRootDirectory { get; set; } = DefaultDataRootDirectory;

    /// <summary>固定数据目录名（安装/卸载/升级都必须认同一个名字）。</summary>
    public const string DataFolderName = "ArchiveFixer";

    /// <summary>
    /// 默认数据根目录：<c>%LOCALAPPDATA%\ArchiveFixer</c>。
    ///
    /// <para>⛔ 这是用户设置 / 密码本 / 元数据的唯一固定位置：
    /// 它不再跟程序安装目录走，覆盖升级、换安装位置、卸载都不会把它带走。
    /// 以后任何版本都不许改这个路径；旧位置不再迁移。</para>
    ///
    /// <para>⚠ 仅供**测试 / 夹具**的进程内覆盖：环境变量 <c>ARCHIVEFIXER_DATA_ROOT</c> 非空时优先用它。
    /// 生产入口不设这个变量 ⇒ 永远还是上面的固定路径；设置它是为了让 WPF 探针这类
    /// "必须走真实启动路径"的用例也有自己的临时数据根（真机 2026-11-01：全量单测把用户真实
    /// <c>password-list.dat</c> 写成了 1 条测试密码）。</para>
    /// </summary>
    public static string DefaultDataRootDirectory
    {
        get
        {
            string overrideRoot = Environment.GetEnvironmentVariable("ARCHIVEFIXER_DATA_ROOT") ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(overrideRoot))
            {
                return overrideRoot;
            }

            return DefaultDataRootDirectoryCore;
        }
    }

    /// <summary>
    /// 生产默认值（**不含**测试覆盖）：守门用例要断言"固定路径不跟程序目录走"时读这一个，
    /// 否则测试进程为了让夹具隔离而设置的 <c>ARCHIVEFIXER_DATA_ROOT</c> 会把断言带偏。
    /// </summary>
    internal static string DefaultDataRootDirectoryCore =>
        Path.Combine(ResolveLocalAppDataRoot(), DataFolderName);

    private static string ResolveLocalAppDataRoot()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return localAppData;
        }

        // 极端环境（用户配置目录被禁用）才走这里：仍然落在用户目录下，
        // ⛔ 不回落程序目录（那会把用户数据重新绑回安装位置）。
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return string.IsNullOrWhiteSpace(profile)
            ? Path.Combine(AppContext.BaseDirectory, "user-data")
            : Path.Combine(profile, "AppData", "Local");
    }

    /// <summary>
    /// 日志目录：生产环境放在程序安装目录下（用户 2026-11-04 明确要求），
    /// 测试/夹具使用临时数据根时仍放在数据根里，避免污染仓库。
    /// </summary>
    public string LogsDirectory
    {
        get
        {
            if (!UsesProductionDataRoot)
            {
                return Path.Combine(DataRootDirectory, "logs");
            }

            return ResolveAppSideDirectory("logs");
        }
    }

    /// <summary>
    /// 临时目录：生产环境也放在程序目录下，测试/夹具落在临时数据根。
    /// </summary>
    public string TempDirectory
    {
        get
        {
            if (!UsesProductionDataRoot)
            {
                return Path.Combine(DataRootDirectory, "temp");
            }

            return ResolveAppSideDirectory("temp");
        }
    }

    private bool UsesProductionDataRoot =>
        string.Equals(
            Path.GetFullPath(DataRootDirectory),
            Path.GetFullPath(DefaultDataRootDirectoryCore),
            StringComparison.OrdinalIgnoreCase);

    private string ResolveAppSideDirectory(string name)
    {
        string appDirectory = Path.Combine(AppBaseDirectory, name);

        try
        {
            Directory.CreateDirectory(appDirectory);
            return appDirectory;
        }
        catch
        {
            // 安装到 Program Files 等不可写位置时，退回用户数据根，不能因此丢日志/临时目录。
            return Path.Combine(DataRootDirectory, name);
        }
    }

    /// <summary>
    /// 递归解压的工作区根目录（**当前生效的那一个**）。
    ///
    /// <para>中间产物放这里，避免直接写用户的最终目录（AGENTS.md §6 第 12 条、设计.md §十三）。
    /// 它也**不在源目录里**：源目录可能只读、可能是别人的共享、也可能是 U 盘。</para>
    ///
    /// <para><b>只有一个来源：这一单的目标目录</b>（用户 2026-09-30 明确指示）：每批开工前由
    /// <see cref="ArchiveFixer.Storage.WorkspaceRootResolver"/> 派生
    /// <c>&lt;目标目录&gt;\.ArchiveFixer.work</c>（点开头 + 隐藏属性）并写进这个属性
    /// （见 <c>ExtractionCoordinator</c> 批首那一步）。⛔ 没有任何设置项 / 参数能改它。</para>
    ///
    /// <para>⚠ 目标位置不可用时批首会直接停手（<c>WorkspaceRootResolution.Resolved</c> 为 false），
    /// 所以生产路径上不会有人拿着"没解析过"的值去拼暂存目录。</para>
    ///
    /// <para>没有解析过时为空串：⛔ 不再回落到 <c>&lt;数据根&gt;\work</c>，也不回 CWD/程序目录。
    /// 旧 work 只允许只读清理。</para>
    /// </summary>
    public string WorkDirectory
    {
        get => _workspaceRootOverride;
        set => _workspaceRootOverride = value ?? string.Empty;
    }

    /// <summary>本批工作区根是否已经被解析过；false 时 <see cref="WorkDirectory"/> 为空，调用方必须停手。</summary>
    public bool WorkspaceRootResolved => !string.IsNullOrWhiteSpace(_workspaceRootOverride);

    private string _workspaceRootOverride = string.Empty;

    /// <summary>
    /// 默认工作区目录名（<c>.ArchiveFixer.work</c>，**点开头 = Windows 默认隐藏**）。
    /// 唯一来源是 <see cref="WorkspaceRootResolver.DefaultWorkspaceDirectoryName"/>，这里只做转发。
    /// </summary>
    public const string DefaultWorkspaceDirectoryName = WorkspaceRootResolver.DefaultWorkspaceDirectoryName;

    /// <summary>
    /// 7-Zip 工具目录。
    /// </summary>
    public string SevenZipDirectory => ToolLocator.Default.BundledDirectory;

    /// <summary>
    /// 7z.exe 路径。
    /// </summary>
    public string SevenZipExePath => ToolLocator.Default.SevenZipExePath;

    /// <summary>
    /// 7z.dll 路径。
    /// </summary>
    public string SevenZipDllPath => ToolLocator.Default.SevenZipDllPath;

    /// <summary>
    /// appsettings.json 路径。
    /// </summary>
    public string SettingsFilePath => Path.Combine(DataRootDirectory, "appsettings.json");

    /// <summary>
    /// 损坏配置备份路径。
    /// </summary>
    public string BrokenSettingsFilePath => Path.Combine(DataRootDirectory, "appsettings.broken.json");

    /// <summary>
    /// 暂存子目录名：<c>&lt;任务工作区&gt;\stage</c>。
    /// 入仓（stage）阶段所有中间动作都在这里做，**一个字节都不写源目录、也不写最终目录**（契约 §2.1）。
    /// </summary>
    public const string StageDirectoryName = "stage";

    /// <summary>
    /// 生成任务输出目录（契约 §1.2 的落点公式）。
    ///
    /// <para>
    /// 公式的**唯一实现处**是 <see cref="OutputPlacement.ResolveDestinationDirectory"/>，本方法只做三件事：
    /// ① 续解出来的内层包优先用父任务的落点（见下）；
    /// ② 把旧的三个布尔/字符串设置翻译成落点模式（<see cref="OutputPlacement.FromLegacyFlags"/>），
    ///    旧配置的行为不变（契约 §1.3：不得让用户升级后行为突变）；
    /// ③ 解不出来时返回**空串**，绝不回落到程序安装目录。
    /// </para>
    ///
    /// <para>
    /// 为什么解不出来必须返回空（父代理 2026-09-21 明确要求的必修项）：
    /// 旧实现在"自定义位置"模式下把空的 <c>CustomOutputDirectory</c> 回落成 <c>AppBaseDirectory</c>，
    /// 于是用户没设输出目录时，内容物会被解进**程序自己的安装目录**（data 旁边、和 exe 混在一起）。
    /// 返回空串之后由调用方给出"输出目录无效"的明确状态，用户重新选一个目录即可 ——
    /// 少一个产物目录，好过多一堆没人找得到的文件。
    /// </para>
    /// </summary>
    /// <param name="volumeGroupBaseName">分卷组基名（可选；给了就不再从文件名剥一遍）。</param>
    public string BuildOutputPath(
        ArchiveTask task,
        ExtractOptions options,
        string? volumeGroupBaseName = null)
    {
        return ResolveOutputPlacement(task, options, volumeGroupBaseName)
            .DestinationDirectory;
    }

    /// <summary>
    /// 落点解析的**唯一入口**（返回整份结论，不只是路径）。
    ///
    /// <para>
    /// 与 <see cref="BuildOutputPath"/> 是同一件事的两半：后者只是取
    /// <see cref="OutputPlacementResult.DestinationDirectory"/>。需要
    /// <see cref="OutputPlacementResult.SharesDestinationWithOtherPackages"/> 的调用方
    /// （解压管线：它决定"目录已存在且非空"要不要让开、其余物要不要按包名分层）
    /// 必须走这一个，**不许**自己再推一遍落点。
    /// </para>
    ///
    /// <para>
    /// 用户 2026-09-24 第 13 条之后，"选中对象是文件还是文件夹"也在这里喂给
    /// <see cref="OutputPlacement.ResolveDestinationDirectory"/>：事实记在
    /// <see cref="ArchiveTask.SourceSelectionKind"/> / <see cref="ArchiveTask.SourceSelectionRoot"/> 上
    /// （导入那一刻由 <c>FileScanService</c> 写下）。
    /// </para>
    ///
    /// <para>
    /// 「解压到当前文件夹」（<see cref="ExtractOptions.ExtractIntoSourceFolder"/>）**只从选项对象上读**：
    /// 早先它另有一个同名形参，于是同一个开关有了两个入口 —— 实测立刻踩到
    /// <c>BuildOutputPath(task, options)</c> 那条路（界面「输出目录」列走它）不认这个选项，
    /// 而管线那条路认：同一个任务在两处显示两个落点。现在整个 <see cref="ExtractOptions"/>
    /// 就是"这次按什么选项解压"的唯一载体，落点推导只认它。
    /// </para>
    /// </summary>
    public OutputPlacementResult ResolveOutputPlacement(
        ArchiveTask task,
        ExtractOptions options,
        string? volumeGroupBaseName = null)
    {
        if (task == null)
        {
            return new OutputPlacementResult
            {
                Success = false,
                Error = OutputPlacementError.EmptySourcePath,
                Message = "没有任务，无法判断输出落点"
            };
        }

        options ??= new ExtractOptions();
        options.Normalize();

        /*
         * 续解出来的内层包：落点就是**父任务那一个**最终目录，绝不再套一层。
         *
         * 这是"一个源包 = 一个最终目录"的第一道闸（用户诉求）。
         * 放在这里而不是调用方：MainViewModel.RefreshOutputPaths（界面"输出目录"列）、
         * 解压管线的落点计算全都走这一个方法，谁都不会算出第二个答案。
         */
        if (!string.IsNullOrWhiteSpace(task.ParentOutputDirectory))
        {
            return new OutputPlacementResult
            {
                Success = true,
                DestinationRoot = task.ParentOutputDirectory,
                DestinationDirectory = task.ParentOutputDirectory,
                Message = "内层包沿用父任务的落点：" + task.ParentOutputDirectory
            };
        }

        string archivePath = task.CurrentPath;

        if (string.IsNullOrWhiteSpace(archivePath))
        {
            archivePath = task.OriginalPath;
        }

        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return new OutputPlacementResult
            {
                Success = false,
                Error = OutputPlacementError.EmptySourcePath,
                Message = "源包路径为空，无法判断输出落点"
            };
        }

        OutputPlacementMode mode = options.PlacementMode
            ?? OutputPlacement.FromLegacyFlags(
                options.ExtractToOriginalDirectory,
                options.KeepArchiveNameFolder,
                options.CustomOutputDirectory);

        /*
         * 第 1 卷（用户 2026-10-05 落点口径："落在**第一卷的位置**"，而且他点名了现场：
         * "你又把 `111.zip` 移到 `111(4)`，然后你当时又在 `111(4)` 里面解压的"）。
         * 跨盘 ZIP 族 = 第 1 片 `.z01`、7z 族 = `.001`、RAR 族 = 第 1 卷 —— 判据只有一处
         * （`Detection.GroupVolumeDirectory`，转调 `VolumeGroupDetector` / `FileNameHelper` 那几把既有尺子），
         * 本方法只把结论喂给落点那一处实现。
         *
         * ⛔ 只对**分卷组**问这件事；⛔ 判不出（第 1 卷还压在包里 / 那一单不是卷成员）⇒ 传空串
         * ⇒ `ResolveDestinationDirectory` 原样退回源包自己那一层（行为与改动前逐字相同）。
         */
        string? entryArchivePath = null;

        if (task.IsVolumeGroup)
        {
            var members = new List<string> { archivePath };
            members.AddRange(task.VolumePaths.Where(path => !string.IsNullOrWhiteSpace(path)));

            GroupVolumeDirectory.Entry entry = GroupVolumeDirectory.Resolve(members);

            if (entry.Path.Length > 0)
            {
                entryArchivePath = entry.Path;
            }
            else
            {
                /*
                 * ===== 入口包**还没被解出来**：落点跟"产出它的那一单"那条链（真机 CCCC） =====
                 *
                 * 用户 2026-10-06 连着追问的那一条：任务表里第一单是散在 `111(4)` 的 `111.z0删除3`，
                 * 而这一组的入口包（跨盘 ZIP 的末片 `111.zip`）正压在 `111.rar` 里 ⇒ 上面那一句
                 * 如实回空 ⇒ 落点退回旧口径（源包自己那一层 + 包名）⇒ `111(4)\111`，
                 * 产物与"其余物"全挂到 `111(4)` 上。
                 *
                 * 判据：这一组**自己那几片**都不是入口 ⇒ 去**任务表**里找"同一个包基名、
                 * 又不是本组成员"的那一单（真机 = `111.rar`）⇒ 落点就取**它**算出来的目标目录
                 * （`CCCC\111\111`）。⛔ 不靠名字猜格式、⛔ 不自己拼路径（转调同一个
                 * `ResolveOutputPlacement`），⛔ 找不到 ⇒ 原样退回旧口径（行为与改动前逐字相同）。
                 * ⛔ 这一档**只影响「未指定位置」那一档的目标根**（与 `entryArchivePath` 同一条路）。
                 */
                entryArchivePath = ResolveProducerEntryArchivePath(task, options, archivePath);
            }
        }

        OutputPlan plan = OutputPlan.Create(
            archivePath,
            mode,
            options.CustomOutputDirectory,
            volumeGroupBaseName,
            task.SourceSelectionKind,
            task.SourceSelectionRoot,
            options.ExtractIntoSourceFolder,
            entryArchivePath,
            sourceRoot: FileNameHelper.GetDirectoryName(archivePath));

        return plan.Placement;
    }

    /// <summary>
    /// **"产出入口包的那一单"推得出来的入口包路径**（唯一出口，真机 `CCCC` 2026-10-06）。
    ///
    /// <para>给定时（由 <c>ExtractionCoordinator</c> 在批首挂上）：这一组的入口包还不在盘上时，
    /// 用它**预判**入口包将来落在哪 —— 落点于是跟"产出它的那一单"那条链走，
    /// ⛔ 不再退回"任务表里这一组第一单所在目录"那个旧口径（真机就是它把产物带到 `111(4)\111`）。</para>
    ///
    /// <para>返回**空**（没挂 / 判不出 / 产出方自己也在等它的入口包）⇒ 一个字都不改，
    /// 落点与改动前逐字相同。</para>
    /// </summary>
    public Func<ArchiveTask, ExtractOptions, string, string>? GroupProducerEntryResolver { get; set; }

    /// <summary>
    /// 入口包不在盘上时问一次"产出它的那一单"（见 <see cref="GroupProducerEntryResolver"/>）。
    /// 判不出 ⇒ <c>null</c>（= 不传，落点退回源包自己那一层）。
    /// </summary>
    /// <summary>
    /// 预判链里的诊断出口（可选；由协调器挂上自己的日志）。
    ///
    /// <para>为什么要有它：这条预判跑在**落点解析里面**，而落点解析是纯函数层、拿不到日志服务；
    /// 一旦抛异常，下面的 `catch` 会把它悄悄吞掉（兜底"什么都不做"是对的，但**排障时看不见**——
    /// 实测踩过：真机形状的用例绿着，日志里却什么都没有，追了半天才发现异常被吃了）。</para>
    /// </summary>
    public Action<string>? DiagnosticLog { get; set; }

    private string? ResolveProducerEntryArchivePath(ArchiveTask task, ExtractOptions options, string archivePath)
    {
        Func<ArchiveTask, ExtractOptions, string, string>? resolver = GroupProducerEntryResolver;

        if (resolver == null)
        {
            return null;
        }

        try
        {
            string resolved = resolver(task, options, archivePath);

            return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
        }
        catch (Exception ex)
        {
            /*
             * 兜底落在"什么都不做"那一档：预判失败只能让落点回到老口径，绝不能让它把落点算崩。
             * ⛔ 但**必须留下痕迹**（WARN + 异常原话）—— 静默吞掉异常会让"没生效"看起来跟"没跑"一样。
             */
            DiagnosticLog?.Invoke(
                $"{task.FileName}：入口包落点预判这一步没算成（{ex.GetType().Name}：{ex.Message}）—— "
                + "落点退回源包自己那一层。");

            return null;
        }
    }

    /// <summary>
    /// 获取压缩包基础名。
    /// </summary>
    public string GetArchiveBaseName(string filePath)
    {
        return FileNameHelper.GetArchiveBaseName(filePath);
    }

    /// <summary>
    /// 本任务的私有工作区目录：<c>&lt;work&gt;\&lt;任务名&gt;-&lt;源路径短哈希&gt;</c>。
    ///
    /// 沿用既有工作区规范（<c>.data\work\&lt;taskId&gt;\</c>，绝不落 C 盘），只加了一段源路径哈希：
    /// 两个**同名但不同目录**的包（<c>111\222\1.rar</c> 与 <c>333\1.rar</c>）在批量处理里很常见，
    /// 只用文件名当 taskId 会让它们共用同一个暂存目录 —— 并发解压时互相踩，
    /// 串行时前一个失败留下的产物会被后一个当成自己的产物搬运出去。
    /// 哈希只取决于**源文件全路径**，所以同一个包的多次尝试仍然落在同一个目录（失败后接着看、接着清）。
    /// </summary>
    public string BuildTaskWorkDirectory(ArchiveTask task)
    {
        if (task == null || string.IsNullOrWhiteSpace(WorkDirectory))
        {
            return string.Empty;
        }

        string name = string.IsNullOrWhiteSpace(task.FileName)
            ? FileNameHelper.GetFileName(task.CurrentPath)
            : task.FileName;

        string safeName = FileNameHelper.SanitizeFileName(name);

        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "task";
        }

        string identity = string.IsNullOrWhiteSpace(task.CurrentPath)
            ? task.OriginalPath
            : task.CurrentPath;

        return Path.Combine(WorkDirectory, $"{safeName}-{BuildPathIdentityHash(identity)}");
    }

    /// <summary>
    /// 本任务的暂存目录：<c>&lt;work&gt;\&lt;taskId&gt;\stage</c>（契约 §2.1）。
    /// 所有中间动作（抠内嵌归档之后的解压、第一层、内层分卷、递归）都落在这里。
    /// </summary>
    public string BuildTaskStageDirectory(ArchiveTask task)
    {
        string taskDirectory = BuildTaskWorkDirectory(task);

        return string.IsNullOrWhiteSpace(taskDirectory)
            ? string.Empty
            : Path.Combine(taskDirectory, StageDirectoryName);
    }

    /// <summary>
    /// 源文件全路径的稳定短哈希（FNV-1a，8 位十六进制）。
    /// 不用 <c>string.GetHashCode()</c>：它跨进程不稳定，而工作区目录要能被"下一次启动"
    /// 认出来（失败留下的暂存区、启动时的未完成工作区提示都依赖这个稳定性）。
    /// </summary>
    private static string BuildPathIdentityHash(string path)
    {
        unchecked
        {
            uint hash = 2166136261;

            foreach (char c in path.ToUpperInvariant())
            {
                hash ^= c;
                hash *= 16777619;
            }

            return hash.ToString("x8");
        }
    }

    /// <summary>
    /// 获取安全文件名。
    /// </summary>
    public string SanitizeFileName(string name)
    {
        return FileNameHelper.SanitizeFileName(name);
    }

    /// <summary>
    /// 确保目录存在。
    /// </summary>
    public bool EnsureDirectoryExists(string path)
    {
        return SafePathHelper.EnsureDirectoryExists(path);
    }

    /// <summary>
    /// 确保基础目录存在（日志 / 临时 / 内置工具）。
    ///
    /// <para>⚠ <b>工作区根刻意不在这里建</b>（用户 2026-09-24 拍板）：默认档下它跟着**输出盘**走，
    /// 而"哪块盘"要等这一批的任务算完才知道 —— 启动时就无脑建一个，只会建在错的盘上
    /// （那正是要改掉的老行为）。它在批首解析那一刻按需创建，建不出来时由解析器给出中文原因
    /// （见 <see cref="WorkspaceRootResolver"/>）。</para>
    /// </summary>
    public void EnsureBaseDirectories()
    {
        SafePathHelper.EnsureDirectoryExists(LogsDirectory);
        SafePathHelper.EnsureDirectoryExists(TempDirectory);
        SafePathHelper.EnsureDirectoryExists(SevenZipDirectory);
    }

    /// <summary>
    /// 自动重命名文件路径。
    /// </summary>
    public string AutoRenameFilePath(string path)
    {
        return SafePathHelper.AutoRenameFilePath(path);
    }

    /// <summary>
    /// 自动重命名目录路径。
    /// </summary>
    public string AutoRenameDirectoryPath(string path)
    {
        return SafePathHelper.AutoRenameDirectoryPath(path);
    }

    /// <summary>
    /// 打开目录。
    /// </summary>
    public bool OpenDirectory(string path)
    {
        return SafePathHelper.OpenDirectory(path);
    }

    /// <summary>
    /// 在资源管理器中选中文件。
    /// </summary>
    public bool OpenFileInExplorer(string filePath)
    {
        return SafePathHelper.OpenFileInExplorer(filePath);
    }

    /// <summary>
    /// 检查 7z.exe 是否存在。
    /// </summary>
    public bool SevenZipExeExists()
    {
        return File.Exists(SevenZipExePath);
    }

    /// <summary>
    /// 检查 7z.dll 是否存在。
    /// </summary>
    public bool SevenZipDllExists()
    {
        return File.Exists(SevenZipDllPath);
    }

    /// <summary>
    /// 检查输出目录是否可写。
    /// </summary>
    public bool CanWriteOutputDirectory(string directory)
    {
        return SafePathHelper.CanWriteToDirectory(directory);
    }

    /// <summary>
    /// 判断路径是否过长。
    /// </summary>
    public bool IsPathTooLong(string path)
    {
        return SafePathHelper.IsPathTooLong(path);
    }

    /// <summary>
    /// 处理目录冲突（旧签名，保留给「只想问一句落点在哪」的调用方）。
    ///
    /// <para>判定表与两阶段落位都在 <see cref="PathConflictResolver"/>；本方法只保留公开 API，
    /// 一个字都不再自己判。</para>
    ///
    /// <para>⚠ <c>Ask</c> 档不静默改名：没拿到用户答案时原路径返回，调用方按「跳过」处理
    /// （见 <see cref="PathConflictResolver.ResolveConflict"/>）。</para>
    /// </summary>
    public string ResolveDirectoryConflict(string targetDirectory, string conflictAction)
    {
        return PathConflictResolver.ResolveDirectoryConflict(targetDirectory, conflictAction);
    }

    /// <summary>
    /// 处理文件路径冲突（旧签名，保留）。语义与
    /// <see cref="PathConflictResolver.ResolveDirectoryConflict"/> 完全一致。
    /// </summary>
    public string ResolveFileConflict(string targetPath, string conflictAction)
    {
        return PathConflictResolver.ResolveFileConflict(targetPath, conflictAction);
    }

    /// <summary>
    /// 同名冲突解析的入口（保留公开 API）。判定表只有一处实现
    /// （<see cref="PathConflictResolver.ResolveConflict"/>），这里只是薄转发。
    /// </summary>
    /// <param name="targetPath">落点（调用方已经或即将往里写东西的那个路径）。</param>
    /// <param name="kind">目标是文件还是目录（决定自动改名用哪套规则）。</param>
    /// <param name="conflictAction">设置项 <see cref="AppSettings.ConflictAction"/> 的值；
    /// 空 / 非法一律按 <c>AutoRename</c>（不变量 3）。</param>
    /// <param name="decision">用户对这次冲突的答案（Ask 档才需要；非 Ask 档传 null）。</param>
    public ConflictResolution ResolveConflict(
        string targetPath,
        ConflictTargetKind kind,
        string conflictAction,
        ConflictDecision? decision = null)
    {
        return PathConflictResolver.ResolveConflict(targetPath, kind, conflictAction, decision);
    }

    /// <summary>
    /// 覆盖落位的入口（保留公开 API）。两阶段（先挪到临时名 → 落位 → 再删）的实现与
    /// 「旧数据现在在哪」的口径都在 <see cref="PathConflictResolver.TryOverwriteLanding"/>。
    /// </summary>
    /// <param name="sourcePath">新条目（暂存区里那一份）。</param>
    /// <param name="targetPath">落点（此刻确实有同名条目占着）。</param>
    /// <param name="isDirectory">新条目是不是目录。</param>
    /// <param name="error">失败原因（成功时为空）。</param>
    /// <param name="note">成功时的一句话说明（旧条目删掉了 / 删不掉留在哪）。</param>
    public bool TryOverwriteLanding(
        string sourcePath,
        string targetPath,
        bool isDirectory,
        out string error,
        out string note)
    {
        return PathConflictResolver.TryOverwriteLanding(sourcePath, targetPath, isDirectory, out error, out note);
    }

    /// <summary>
    /// 获取相对程序目录的路径。
    /// </summary>
    public string GetAppRelativePath(params string[] parts)
    {
        if (parts == null || parts.Length == 0)
        {
            return AppBaseDirectory;
        }

        string[] allParts = new string[parts.Length + 1];
        allParts[0] = AppBaseDirectory;
        Array.Copy(parts, 0, allParts, 1, parts.Length);

        return Path.Combine(allParts);
    }

    /// <summary>
    /// 尝试用默认程序打开文件。
    /// </summary>
    public bool OpenFile(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            };

            using Process? process = Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

}
