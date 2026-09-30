using System;
using System.Diagnostics;
using System.IO;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 同名冲突的目标类型：文件还是目录。
    ///
    /// 为什么要分开：覆盖一个**文件**是"挪开 → 落位 → 删旧的"；覆盖一棵**目录树**是同一套动作，
    /// 但代价与风险差一个量级（整棵树进回收站都没有的"临时名"）。类型也对不上时（文件撞目录）
    /// 一律不覆盖 —— 删掉一棵目录树只为给一个文件让位，收益与风险不成比例。
    /// </summary>
    public enum ConflictTargetKind
    {
        File = 0,
        Directory = 1
    }

    /// <summary>一次同名冲突的处置方式（用户的答案，或由 <see cref="ConflictActions"/> 的档位直接推出）。</summary>
    public enum ConflictChoice
    {
        /// <summary>跳过这一项：已存在的文件一个字节都不动。</summary>
        Skip = 0,

        /// <summary>覆盖这一项：走"先挪到临时名 → 落位 → 再删"两阶段（不变量 3）。</summary>
        Overwrite = 1,

        /// <summary>自动重命名这一项：新产物落成 <c>名字(1)</c>，绝不覆盖。</summary>
        AutoRename = 2,

        /// <summary>取消本批（等价于既有的"停止后续 + 取消当前"取消语义）。</summary>
        CancelBatch = 3
    }

    /// <summary>
    /// 用户对同名冲突的一次决定。
    ///
    /// <para>
    /// <see cref="ApplyToAll"/> 是这套设计的**关键位**（决策 D-4：用户场景是 50–200+ 个包，
    /// 逐个问等于不可用）：勾上"对后面所有同名冲突都照此办理"之后，本批内**不得再问第二次**，
    /// 后续冲突一律照这个 Choice 办。
    /// </para>
    /// </summary>
    public readonly struct ConflictDecision
    {
        public ConflictDecision(ConflictChoice choice, bool applyToAll = false)
        {
            Choice = choice;
            ApplyToAll = applyToAll;
        }

        public ConflictChoice Choice { get; }

        /// <summary>true = 本批后续同名冲突一律照此办理（不再询问）。</summary>
        public bool ApplyToAll { get; }

        /// <summary>取消本批：调用方按既有取消语义处理，**不得**继续落位。</summary>
        public bool IsCancel => Choice == ConflictChoice.CancelBatch;

        public static ConflictDecision Once(ConflictChoice choice) => new(choice, applyToAll: false);

        public static ConflictDecision ForAll(ConflictChoice choice) => new(choice, applyToAll: true);

        /// <summary>
        /// 保守档：**没有界面宿主 / 没问到答案**时的兜底。
        ///
        /// 取"自动重命名"而不是"覆盖"：产物照样落地（不丢内容），旧文件一个字节都不动，
        /// 而且不需要谁来点确认 —— 这正是不变量 3 要的"默认不得覆盖"。
        /// </summary>
        public static ConflictDecision Conservative => Once(ConflictChoice.AutoRename);

        /// <summary>写日志用的一句话（必须能看出用户选了哪一档、是不是整批）。</summary>
        public string Describe() =>
            ApplyToAll ? $"全部{DescribeChoice(Choice)}（本批不再询问）" : DescribeChoice(Choice);

        public static string DescribeChoice(ConflictChoice choice) => choice switch
        {
            ConflictChoice.Skip => "跳过",
            ConflictChoice.Overwrite => "覆盖",
            ConflictChoice.AutoRename => "自动重命名",
            _ => "取消本批"
        };
    }

    /// <summary>
    /// 一次冲突的解析结论：**怎么处理** + **落到哪个路径**。
    ///
    /// 把它和"显示用的字符串"分开，是因为旧 API 只返回一个路径字符串 —— 于是"跳过"和"覆盖"
    /// 都只能靠"路径没变"来表达，调用方分不清两者，最终就退化成了"反正一样，都走自动改名"。
    /// </summary>
    public readonly struct ConflictResolution
    {
        public ConflictResolution(ConflictChoice choice, string targetPath, bool conflicted, bool decidedByUser)
        {
            Choice = choice;
            TargetPath = targetPath;
            Conflicted = conflicted;
            DecidedByUser = decidedByUser;
        }

        public ConflictChoice Choice { get; }

        /// <summary>按结论该用的落点：Skip / Overwrite 时就是原路径，AutoRename 时是 <c>名字(1)</c>。</summary>
        public string TargetPath { get; }

        /// <summary>解析时目标确实已存在（false = 没有冲突，原样使用 <see cref="TargetPath"/>）。</summary>
        public bool Conflicted { get; }

        /// <summary>Ask 档是否真的拿到了用户的答案（false = 走了保守兜底）。</summary>
        public bool DecidedByUser { get; }
    }

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
        /// 用户数据目录（%AppData%\ArchiveFixer）。
        /// </summary>
        public string DataRootDirectory { get; set; } = DefaultDataRootDirectory;

        /// <summary>默认数据根目录：程序目录下的 data。缓存绝不默认写 C 盘。</summary>
        public static string DefaultDataRootDirectory =>
            Path.Combine(AppContext.BaseDirectory, "data");

        /// <summary>
        /// 日志目录。
        /// </summary>
        public string LogsDirectory => Path.Combine(DataRootDirectory, "logs");

        /// <summary>
        /// 临时目录。
        /// </summary>
        public string TempDirectory => Path.Combine(DataRootDirectory, "temp");

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
        /// <para>没有解析过时的取值 = <c>&lt;数据根&gt;\work</c>，也就是**2026-09-30 之前的老位置**：
        /// 它现在只服务于启动时"③ 页要扫哪些根"那一档（老位置里可能还留着升级前的残留）。</para>
        /// </summary>
        public string WorkDirectory
        {
            get => string.IsNullOrWhiteSpace(_workspaceRootOverride)
                ? Path.Combine(DataRootDirectory, WorkspaceRootResolver.LegacyWorkspaceSubDirectoryName)
                : _workspaceRootOverride;
            set => _workspaceRootOverride = value ?? string.Empty;
        }

        /// <summary>
        /// 本批工作区根是否已经被解析过（解析发生在每批开工前；为 false 时 <see cref="WorkDirectory"/>
        /// 取的是老位置 <c>&lt;数据根&gt;\work</c>）。
        /// </summary>
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

            OutputPlacementMode mode = OutputPlacement.FromLegacyFlags(
                options.ExtractToOriginalDirectory,
                options.KeepArchiveNameFolder,
                options.CustomOutputDirectory);

            return OutputPlacement.ResolveDestinationDirectory(
                archivePath,
                mode,
                options.CustomOutputDirectory,
                volumeGroupBaseName,
                driveExists: null,
                selectionKind: task.SourceSelectionKind,
                selectionRoot: task.SourceSelectionRoot,
                flattenIntoSourceFolder: options.ExtractIntoSourceFolder);
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
            if (task == null)
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
        /// 处理目录冲突（旧签名，保留给"只想问一句落点在哪"的调用方）。
        ///
        /// ⚠ <c>Ask</c> 档**不再**落进自动重命名分支（这正是本轮修掉的缺陷：界面给了「询问」，
        /// 代码却把它和 <c>AutoRename</c> 并到同一个分支，用户选了询问、程序静默自动改名）。
        /// 没拿到用户答案时这个方法**不替用户决定**：原路径返回，调用方按"跳过"处理；
        /// 要真的询问、要覆盖、要改名，用 <see cref="ResolveConflict"/> 并带上用户的
        /// <see cref="ConflictDecision"/>。
        /// </summary>
        public string ResolveDirectoryConflict(string targetDirectory, string conflictAction)
        {
            return ResolveConflict(targetDirectory, ConflictTargetKind.Directory, conflictAction).TargetPath;
        }

        /// <summary>
        /// 处理文件路径冲突（旧签名，保留）。语义与
        /// <see cref="ResolveDirectoryConflict"/> 完全一致（<c>Ask</c> 不再静默改名）。
        /// </summary>
        public string ResolveFileConflict(string targetPath, string conflictAction)
        {
            return ResolveConflict(targetPath, ConflictTargetKind.File, conflictAction).TargetPath;
        }

        /// <summary>
        /// 同名冲突的**唯一解析处**：档位（或用户的答案）+ 目标路径 → 处置方式 + 落点。
        ///
        /// <para>
        /// 判定表（<see cref="ConflictActions"/> 的四档，一行都不许在别处再写一遍）：
        /// </para>
        /// <list type="table">
        /// <item><description><c>Skip</c> → 跳过，路径不变；</description></item>
        /// <item><description><c>Overwrite</c> → 覆盖，路径不变（**落位必须走
        /// <see cref="TryOverwriteLanding"/> 的两阶段**，禁止先删后移）；</description></item>
        /// <item><description><c>AutoRename</c> → <c>名字(1)</c>，绝不覆盖；</description></item>
        /// <item><description><c>Ask</c> + 有 <paramref name="decision"/> → 按用户的选择办；</description></item>
        /// <item><description><c>Ask</c> + 没有答案 → **保守档（跳过）**：绝不静默改名、更不覆盖
        /// （没有人回答过的问题，程序不许替用户拍板）。</description></item>
        /// </list>
        /// </summary>
        /// <param name="targetPath">落点（调用方已经或即将往里写东西的那个路径）。</param>
        /// <param name="kind">目标是文件还是目录（决定自动改名用哪套规则）。</param>
        /// <param name="conflictAction">
        /// 设置项 <see cref="AppSettings.ConflictAction"/> 的值；空 / 非法一律按 <c>AutoRename</c>（不变量 3）。
        /// </param>
        /// <param name="decision">用户对这次冲突的答案（Ask 档才需要；非 Ask 档传 null）。</param>
        public ConflictResolution ResolveConflict(
            string targetPath,
            ConflictTargetKind kind,
            string conflictAction,
            ConflictDecision? decision = null)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return new ConflictResolution(ConflictChoice.AutoRename, targetPath, false, false);
            }

            bool conflicted = Exists(targetPath);

            if (!conflicted)
            {
                return new ConflictResolution(ConflictChoice.AutoRename, targetPath, false, false);
            }

            string action = ConflictActions.Normalize(conflictAction);

            if (!ConflictActions.IsAsk(action))
            {
                // 非 Ask 档：一个字都不问，直接由档位推出结论（默认 AutoRename 会换成 名字(1)）。
                ConflictChoice choice = ActionChoice(action);

                return new ConflictResolution(choice, ResolveTarget(targetPath, kind, choice), true, false);
            }

            if (decision is { } answered && !answered.IsCancel)
            {
                return new ConflictResolution(answered.Choice, ResolveTarget(targetPath, kind, answered.Choice), true, true);
            }

            /*
             * Ask 档但没人回答：**不替用户决定**。
             *
             * 旧实现把 Ask 和 AutoRename 并到同一个分支（等于"问了也白问"，用户看到的是静默改名）——
             * 那正是"界面说一套、代码做一套"。这里退回保守档：路径原样返回，调用方按"跳过"处理，
             * 产物留在暂存目录里（一个字节都不丢），日志里会写明原因。
             */
            return new ConflictResolution(ConflictChoice.Skip, targetPath, true, false);
        }

        /// <summary>档位 → 处置方式（只有 AutoRename 会改路径，其余两档路径不变）。</summary>
        private static ConflictChoice ActionChoice(string action) => action switch
        {
            ConflictActions.Skip => ConflictChoice.Skip,
            ConflictActions.Overwrite => ConflictChoice.Overwrite,
            _ => ConflictChoice.AutoRename
        };

        /// <summary>按处置方式算落点：只有自动重命名会换名字。</summary>
        private static string ResolveTarget(string targetPath, ConflictTargetKind kind, ConflictChoice choice)
        {
            if (choice != ConflictChoice.AutoRename)
            {
                return targetPath;
            }

            return kind == ConflictTargetKind.Directory
                ? SafePathHelper.AutoRenameDirectoryPath(targetPath)
                : SafePathHelper.AutoRenameFilePath(targetPath);
        }

        private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        /// <summary>
        /// 两阶段覆盖落位（AGENTS.md §6 第 3 条）：
        /// <c>把占位者挪到临时名 → 新条目落位 → 落位成功后才删掉那个临时名</c>。
        ///
        /// <para>
        /// <b>为什么绝不能"先 File.Delete 再 Move"</b>（基线就是这么写的）：中间任何一步失败
        /// （跨卷、被占用、权限、磁盘满）都会让**被覆盖的那一份永久消失**，而新条目还没到位 ——
        /// 用户丢的正是他本来想保留的那个文件。名称交换 <c>A ↔ B</c> 同理，必须走临时名两阶段。
        /// </para>
        /// <para>
        /// 临时名放在**目标同一个目录**里（<c>名字.af-vacating.ext</c>，与 <c>RenameService</c> 同一套命名）：
        /// 换目录就可能是换卷，跨卷的 <c>Move</c> 不再是原子操作。
        /// 落位失败时把临时名挪回原位 —— 结果只可能是"旧的原样"或"新的就位"，绝不会"两个都没了"。
        /// </para>
        /// </summary>
        /// <param name="sourcePath">新条目（暂存区里那一份）。</param>
        /// <param name="targetPath">落点（此刻确实有同名条目占着）。</param>
        /// <param name="isDirectory">新条目是不是目录。</param>
        /// <param name="error">失败原因（成功时为空）。</param>
        /// <param name="note">成功时的一句话说明（旧条目删掉了 / 删不掉留在哪）—— 日志要能看出覆盖留了什么痕。</param>
        public bool TryOverwriteLanding(
            string sourcePath,
            string targetPath,
            bool isDirectory,
            out string error,
            out string note)
        {
            error = string.Empty;
            note = string.Empty;

            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetPath))
            {
                error = "源路径或目标路径为空";
                return false;
            }

            if (SafePathHelper.PathEquals(sourcePath, targetPath))
            {
                error = "源与目标是同一个路径";
                return false;
            }

            bool targetIsDirectory = Directory.Exists(targetPath) && !File.Exists(targetPath);

            if (targetIsDirectory != isDirectory)
            {
                // 类型对不上（文件撞目录 / 目录撞文件）：不动那棵树，交给调用方按"不覆盖"处理。
                error = $"同名目标类型不同（已存在的是{(targetIsDirectory ? "目录" : "文件")}）";
                return false;
            }

            string vacatedPath = BuildVacatingPath(targetPath, targetIsDirectory);

            // ── 阶段 1：把占位者挪到临时名（此时旧数据仍然完整，只是换了个名字）──
            if (!TryMove(targetPath, vacatedPath, targetIsDirectory, out string vacateError))
            {
                error = $"挪开旧{(targetIsDirectory ? "目录" : "文件")}失败（原样未动）：{vacateError}";
                return false;
            }

            // ── 阶段 2：新条目落位 ──
            if (!TryMove(sourcePath, targetPath, isDirectory, out string landError))
            {
                // 落位失败：把临时名挪回去。挪不回去也必须说清旧数据现在在哪（绝不静默）。
                if (!TryMove(vacatedPath, targetPath, targetIsDirectory, out string backError))
                {
                    error = $"落位失败且旧{(targetIsDirectory ? "目录" : "文件")}没能挪回原位" +
                            $"（旧数据在 {vacatedPath}，未丢失）：{landError}；挪回失败：{backError}";
                    return false;
                }

                error = $"落位失败（旧{(targetIsDirectory ? "目录" : "文件")}已挪回原位）：{landError}";
                return false;
            }

            // ── 阶段 3：这时才允许删旧的（反过来的顺序就是"先删后移"）──
            bool deleted = TryDelete(vacatedPath, targetIsDirectory);

            note = deleted
                ? $"旧{(targetIsDirectory ? "目录" : "文件")}已删除，覆盖前它叫 {vacatedPath}"
                : $"旧{(targetIsDirectory ? "目录" : "文件")}删不掉，已留在 {vacatedPath}（新条目已就位）";

            return true;
        }

        /// <summary>
        /// 临时（腾位）路径：与目标同目录，命名沿用 <c>RenameService</c> 的 <c>.af-vacating</c> 惯例
        /// （不新造机制，出问题时用户与排障脚本一眼能认出来）。
        /// </summary>
        private static string BuildVacatingPath(string path, bool isDirectory)
        {
            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            string name = isDirectory ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
            string extension = isDirectory ? string.Empty : Path.GetExtension(path);

            for (int i = 0; i < 10000; i++)
            {
                string candidate = i == 0
                    ? Path.Combine(directory, name + ".af-vacating" + extension)
                    : Path.Combine(directory, $"{name}.af-vacating{i}{extension}");

                if (!Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(directory, $"{name}.af-vacating-{Guid.NewGuid():N}{extension}");
        }

        private static bool TryMove(string from, string to, bool isDirectory, out string error)
        {
            try
            {
                if (isDirectory)
                {
                    Directory.Move(from, to);
                }
                else
                {
                    File.Move(from, to);
                }

                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryDelete(string path, bool isDirectory)
        {
            try
            {
                if (isDirectory)
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return true;
            }
            catch
            {
                return false;
            }
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
}
