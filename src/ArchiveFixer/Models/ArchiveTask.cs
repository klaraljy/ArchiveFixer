using ArchiveFixer.Extraction;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 源包搬运（一键处理的"移入其余物"，决策 D-9/D-11/D-12）在**本任务**上的记账状态。
    ///
    /// <para>
    /// 为什么必须是一个显式状态，而不是"看源文件还在不在"：
    /// </para>
    /// <list type="number">
    /// <item><description><b>幂等</b>：源包搬运有两条路径 —— 本轮定稿完内容物就搬（原有路径），
    /// 以及"最外层那一轮没有内容物、留到整条续解链跑完之后再补搬"（2026-09-22 修复的真实缺陷）。
    /// 两条路径必须互相看得见，否则同一个源包会被搬两次（第二次会落进 <c>其余物\222(1).mp4</c>）。
    /// 靠"文件还在不在"猜是不行的：搬运失败、被跳过、半途放弃都会让文件"还在原地"，
    /// 于是每次都会再试一遍，用户看到的是"每次运行都多一份"。</description></item>
    /// <item><description><b>延期</b>：用户那个真实文件（<c>222.mp4</c> = 假 MP4 头 + 尾部 ZIP + 内层加密分卷）
    /// 第一层解出来的**只有待续解的过程物**，此时没有任何"内容物已定稿"的事实，不能搬；
    /// 要记下"留到链结束后"，否则补搬没有触发点。</description></item>
    /// </list>
    /// </summary>
    public enum SourcePackageMoveState
    {
        /// <summary>还没轮到（也没有延期语义）：两条路径都可以按自己的判据处理。</summary>
        NotAttempted = 0,

        /// <summary>本轮没有内容物可定稿：源包搬运**留到整条续解链跑完之后补做**。</summary>
        DeferredToChainEnd = 1,

        /// <summary>
        /// 源包**已经搬进其余物**（或压根没有可搬的）：两条路径都不许再动它。
        ///
        /// ⚠ 搬失败时**不落成这一档**（保持原状态）：那说明源包还完整地留在原地，
        /// 任务已经标成「部分完成」并写明原因，下一次运行还有机会补上。
        /// 把"尝试过"也记成"搬过了"，用户就再也修不好那一次失败。
        /// </summary>
        Done = 2
    }

    /// <summary>
    /// 任务的**机器可判终态**（见 <see cref="ArchiveTask.Outcome"/>）。
    ///
    /// <para>
    /// 只有"删源 / 搬源 / 续解下一层 / 危险模式删其余物"这几条**不可逆或会放大**的裁决读它。
    /// 中文文案（<c>StatusText</c>）继续只做显示，两套东西各管一头，谁也不许拿另一头当判据。
    /// </para>
    /// </summary>
    public enum TaskOutcome
    {
        /// <summary>还没有结论（含"在定稿之前就结束"的那些分支）。所有门都不放行这一档。</summary>
        Pending = 0,

        /// <summary>产物已定稿、输出校验通过、源包处理也没出问题：**唯一**允许继续/删除的档。</summary>
        Succeeded = 1,

        /// <summary>做了一部分（源包没能搬进其余物 / 递归停在需要用户决定的层）。不得当成成功。</summary>
        PartiallyCompleted = 2,

        /// <summary>失败（含校验未通过 / 越界 / 超预算 / 密码试完）。</summary>
        Failed = 3,

        /// <summary>用户取消。</summary>
        Cancelled = 4,

        /// <summary>跳过（不是压缩包 / 同名冲突按用户选择跳过）。</summary>
        Skipped = 5
    }

    /// <summary>
    /// 空间门拦下这一单时**已经算好的**那三个数（用户 2026-09-30 第 1 条：批末清单要"点名差多少"）。
    ///
    /// <para><b>为什么要把它们记在任务上</b>：这三个数在空间门那一刻就算出来了
    /// （<c>SpaceGate.Check</c> / <c>ScheduledExtractionItem.RequiredBytes</c>），
    /// 但它们当时只进了日志与 <c>ExtractionCoordinator</c> 的一个私有列表
    /// （<c>_spaceBlockedTasks</c>，批首会被清掉、批末只写日志）——
    /// 批末那个汇总框要"点名差多少"时，手上只有任务清单，于是只能重算一遍。
    /// 重算就是第二套口径（可用空间还是不是当时那个数？预算一样吗？），
    /// 所以这里把**已经算出来的**三个数原样记下来，诊断清单只读它，一个数都不重算。</para>
    ///
    /// <para>类型放在 Models 层：它只装数字，没有任何盘上动作（AGENTS.md §4 的分层铁律）。</para>
    /// </summary>
    public sealed class SpaceBlockedFacts
    {
        /// <summary>当时的**需求**（<c>TaskSpaceEstimate.FreeSpaceDemandBytes</c>：内容物 + 过程物）。</summary>
        public long RequiredBytes { get; init; }

        /// <summary>当时的可用空间；<c>-1</c> = 取不到（与 <c>SpaceReservationLedger.UnknownAvailable</c> 同一口径）。</summary>
        public long AvailableBytes { get; init; } = -1L;

        /// <summary>差多少字节（<c>SpaceGateDecision.ShortfallBytes</c>）。</summary>
        public long ShortfallBytes { get; init; }
    }

    /// <summary>
    /// 一个待处理压缩包任务。
    /// 
    /// 注意：
    /// 1. OriginalPath 永远保存最初导入路径。
    /// 2. CurrentPath 保存当前真实路径，改名后需要更新。
    /// 3. Password 允许保存在内存中，但不能写入日志。
    /// 4. UI 绑定此类，所以实现 INotifyPropertyChanged。
    /// </summary>
    public class ArchiveTask : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        private int _index;
        private string _originalPath = string.Empty;
        private string _currentPath = string.Empty;
        private string _fileName = string.Empty;
        private string _directoryPath = string.Empty;
        private string _currentExtension = "无";
        private string _detectedFormat = "Unknown";
        private string _suggestedExtension = string.Empty;
        private string _extensionStatus = StatusText.NotChecked;
        private string _renamePreviewPath = string.Empty;
        private string _password = string.Empty;
        private string _passwordStatus = StatusText.NotChecked;
        private string _outputPath = string.Empty;
        private string _operation = StatusText.OpWaiting;
        private string _status = StatusText.WaitingScan;
        private string _progressText = "-";
        private string _errorMessage = string.Empty;
        private bool _isArchive;
        private bool _isEncrypted;
        private DateTime? _startTime;
        private DateTime? _endTime;
        private string _elapsedText = "-";
        private DateTime _lastUpdatedTime = DateTime.Now;
        private int _progressPercent = NoProgress;
        private string _progressEntry = string.Empty;
        private string _responsivenessHint = string.Empty;

        /// <summary>"还没有进度"的哨兵值。<b>不要</b>用 0 —— 0% 是"确实刚开始解"。</summary>
        public const int NoProgress = -1;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 是否选择此任务参与处理。
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        /// <summary>
        /// 任务序号。
        /// </summary>
        public int Index
        {
            get => _index;
            set => SetProperty(ref _index, value);
        }

        /// <summary>
        /// 最初导入时的路径。
        /// 即使改名后也不改变。
        /// </summary>
        public string OriginalPath
        {
            get => _originalPath;
            set
            {
                if (SetProperty(ref _originalPath, value ?? string.Empty))
                {
                    RememberOwnFile(_originalPath, allowReplace: _ownFilePath.Length == 0);
                }
            }
        }

        /// <summary>
        /// 当前真实处理路径。
        /// 改名成功后必须更新。
        /// </summary>
        public string CurrentPath
        {
            get => _currentPath;
            set
            {
                if (SetProperty(ref _currentPath, value ?? string.Empty))
                {
                    RefreshPathRelatedProperties();

                    /*
                     * 只有**同一个目录里**的改写才算"用户那个文件的新名字"（改名 / 组内换卷）。
                     * 跨目录改写（批末补判把起点指到入口包 `…\111\111\111.zip`）⛔ 一律不采纳 ——
                     * 那一份是过程物，采纳了列表那一行就变成"48MB 的入口包"（用户 2026-10-07 当场报的）。
                     */
                    if (_ownFilePath.Length == 0
                        || IsSameDirectory(_ownFilePath, _currentPath))
                    {
                        RememberOwnFile(_currentPath, allowReplace: true);
                    }
                }
            }
        }

        /// <summary>
        /// 当前文件名。
        /// 改名后应显示最新文件名。
        /// </summary>
        public string FileName
        {
            get => _fileName;
            set => SetProperty(ref _fileName, value ?? string.Empty);
        }

        /// <summary>
        /// 当前文件所在目录。
        /// </summary>
        public string DirectoryPath
        {
            get => _directoryPath;
            set => SetProperty(ref _directoryPath, value ?? string.Empty);
        }

        /// <summary>
        /// 这次导入时用户选中的是**文件**还是**文件夹**（用户 2026-09-24 第 13 条四条规则的第 1 个维度）。
        ///
        /// <para>
        /// 为什么必须记在任务上：用户在「添加文件夹」里选的**不是这些包本身，而是一个容器**。
        /// 指定位置时落点用那个文件夹的名字（<c>BBB\222\</c>），这从任务自己的路径里推不出来
        /// （任务只是文件夹里的某一个包）。导入那一刻（<c>FileScanService</c>）把事实记下来，
        /// 落点解析（<c>OutputPlacement</c>）只读它，不在别处猜。
        /// </para>
        /// <para>
        /// 默认是 <see cref="Extraction.SourceSelectionKind.File"/>：单个文件 / 递归内层包 / 测试里手搓的任务
        /// 都按"文件"这一档算，落点行为与用户第 1 条规则一致。
        /// </para>
        /// </summary>
        public SourceSelectionKind SourceSelectionKind { get; set; } = SourceSelectionKind.File;

        private long _sourceSizeBytes;

        /// <summary>
        /// 源文件大小（字节）。0 = 还没采到 / 文件不在（两种情况都不编数字）。
        /// 由 <see cref="RefreshSourceSize"/> 采集。
        /// </summary>
        public long SourceSizeBytes
        {
            get => _sourceSizeBytes;
            set
            {
                if (SetProperty(ref _sourceSizeBytes, value))
                {
                    OnPropertyChanged(nameof(SourceSizeText));
                }
            }
        }

        /// <summary>
        /// 列表里那一列「大小」显示的文字（用户 2026-09-27 要求排在**文件名**后面）。
        /// 采不到时显示一个短横，⛔ 不显示"0 字节"（那会让人以为文件是空的）。
        /// </summary>
        public string SourceSizeText => _sourceSizeBytes > 0 ? FormatSizeText(_sourceSizeBytes) : "-";

        /// <summary>
        /// 这一行**在列表里显示谁**（见 <see cref="ShowUserFileIdentity"/>）；空 = 与
        /// <see cref="FileName"/> / <see cref="SourceSizeText"/> 逐字相同（绝大多数任务都是这一档）。
        /// </summary>
        private string _userFacingFilePath = string.Empty;

        /// <summary>「缺卷待批末判」这个**显示/统计事实位**的存储（见 <see cref="IsVolumeDeficitDeferred"/>）。</summary>
        private bool _isVolumeDeficitDeferred;

        /// <summary>
        /// **用户那个文件的最新已知位置**（改名跟着走、被搬进其余物跟着走、被删也照旧记着）——
        /// 只给显示用，⛔ 不参与任何判据。
        ///
        /// <para>为什么需要它（用户 2026-10-07：「文件名、大小、后缀、检测格式、状态，每个都有问题，
        /// 现在居然还是 <c>111.rar</c>，你是不是一直锁定到原包，根本就没有看最新的东西」）：
        /// 批末补判会把这一单的**起点**改写到入口包上（真机那一行就成了 48.35 MiB 的
        /// <c>111\111\111.zip</c>）；那一份**不是用户的文件**，可它一改写就把
        /// <see cref="FileName"/> / <see cref="SourceSizeText"/> 整行刷新成过程物。
        /// 老写法在"记着的那条已经不在了"时正是回落到这些被改写的值 ⇒ 列表里那一行变成 48MB 的入口包。</para>
        ///
        /// <para>维护点只有三处（⛔ 不新增第四个写入点）：构造时 = 最初导入路径；<see cref="CurrentPath"/>
        /// **同目录**改写时跟着走（改名 / 组内换卷）；<see cref="ShowUserFileIdentity"/> 采纳提示时。
        /// 跨目录改写（补判指向入口包）**一律不采纳** —— 那已经不是用户那个文件了。</para>
        /// </summary>
        private string _ownFilePath = string.Empty;

        /// <summary>上一条那个文件最后一次被看到时的字节数（0 = 从来没看到过）。</summary>
        private long _ownFileSizeBytes;
        /// <summary>
        /// 这一刻**真正拿来显示**的那份文件：按"最新的盘上事实"逐条问，问不到才算空。
        ///
        /// <para>⛔ 为什么要核盘（用户 2026-10-06：「列表里面显示的没有一个是对的」「后缀也不同步」）：
        /// 记下来的路径会**过期**（改名 / 被搬走 / 被删）—— 拿一个已经不存在的名字去显示，
        /// 那一行就成了"名字是旧的、别的列是新的"的自相矛盾。</para>
        ///
        /// <para>⛔ 为什么要**跟着文件走**（用户 2026-10-07：「你是不是一直锁定到原包，根本就没有看最新的
        /// 东西」）：这一行属于**用户导入的那个文件**，而它在程序手里会改名（改回标准卷名）、
        /// 会按设置搬进它自己的「其余物」—— 只认"最初导入那一刻的那条路径"就会一直显示旧名字、旧位置。
        /// 判定顺序（先用先赢，⛔ 只读、一个字节都不改盘上任何东西）：① 记着的那条（还在 ⇒ 就是它）→
        /// ② 最初导入那条 → ③ 当前处理路径（**只在同一目录里**才认：批末补判会把起点改写到别的目录的
        /// 过程物上，那已经不是用户那个文件了）→ ④ 它自己的「其余物」目录里同名的那一份。</para>
        /// </summary>
        private string DisplaySourcePath
        {
            get
            {
                if (_userFacingFilePath.Length > 0 && ExistsOnDisk(_userFacingFilePath))
                {
                    return _userFacingFilePath;
                }

                if (ExistsOnDisk(OriginalPath))
                {
                    return OriginalPath;
                }

                string originalDirectory = GetDirectorySafe(OriginalPath);

                if (originalDirectory.Length > 0
                    && ExistsOnDisk(CurrentPath)
                    && string.Equals(GetDirectorySafe(CurrentPath), originalDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    return CurrentPath;
                }

                return FindOwnFileInRestDirectory();
            }
        }

        /// <summary>④ 源包按设置搬进**它自己的**「其余物」之后，用户那一份就在那儿（搬运不改名）。</summary>
        private string FindOwnFileInRestDirectory()
        {
            if (string.IsNullOrWhiteSpace(RestDirectoryPath))
            {
                return string.Empty;
            }

            /*
             * ⛔ 名字要试**两个**：最初导入那个名字（`OriginalPath`）与**最新已知的那个名字**
             * （`_ownFilePath` —— 改名之后就是它）。只试前者会漏掉"先改名、后被搬进其余物"那一档，
             * 于是那一行还停在旧位置（真机 2026-10-07：`111.z03` 已经在 `…\其余物\111.z03`，
             * 那一行却还写着 `111(4)\111.z03` ⇒ 用户说"显示没同步"）。
             */
            foreach (string name in new[]
                     {
                         Path.GetFileName(_ownFilePath),
                         Path.GetFileName(OriginalPath)
                     })
            {
                if (name.Length == 0)
                {
                    continue;
                }

                try
                {
                    string candidate = Path.Combine(RestDirectoryPath, name);

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // 路径形状不合法 ⇒ 试下一个名字。
                }
            }

            return string.Empty;
        }

        private static bool ExistsOnDisk(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static string GetDirectorySafe(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetDirectoryName(path) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsSameDirectory(string? left, string? right)
        {
            string leftDirectory = GetDirectorySafe(left);

            return leftDirectory.Length > 0
                && string.Equals(leftDirectory, GetDirectorySafe(right), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 记住"用户那个文件"的最新位置与体积（<see cref="_ownFilePath"/> 的**唯一**写入实现）。
        ///
        /// <para>体积只在文件真的在时更新（被搬走 / 被删之后保留最后一次看到的那个数 ——
        /// 那一格要写的是"你那个文件多大"，⛔ 不是"现在盘上还剩什么"）。任何 IO 意外都吞掉：
        /// 这是显示，⛔ 绝不允许因为读不到就影响解压。</para>
        /// </summary>
        private void RememberOwnFile(string? path, bool allowReplace)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (!allowReplace && _ownFilePath.Length > 0)
            {
                return;
            }

            bool changed = !string.Equals(_ownFilePath, path, StringComparison.OrdinalIgnoreCase);

            _ownFilePath = path!;

            try
            {
                if (File.Exists(path))
                {
                    _ownFileSizeBytes = new FileInfo(path!).Length;
                }
            }
            catch
            {
                // 读不到就保留上一次记下的体积。
            }

            if (changed)
            {
                // 显示那几列是计算属性（读 _ownFilePath）⇒ 必须显式发通知，否则行停在旧名字上。
                NotifyDisplayIdentityChanged();
            }
        }

        /// <summary>列表「文件名」那一格显示什么（默认 = <see cref="FileName"/>）。</summary>
        public string DisplayFileName =>
            DisplaySourcePath is { Length: > 0 } path
                ? Path.GetFileName(path)
                : (_ownFilePath.Length > 0 ? Path.GetFileName(_ownFilePath) : FileName);

        /// <summary>列表「大小」那一格显示什么（默认 = <see cref="SourceSizeText"/>）。</summary>
        public string DisplaySizeText =>
            DisplaySourcePath is { Length: > 0 } path
                ? DescribeSize(path)
                : (_ownFileSizeBytes > 0 ? FormatSizeText(_ownFileSizeBytes) : SourceSizeText);

        /// <summary>「文件名」那一格悬停提示 / 「完整路径」那一列（默认 = <see cref="CurrentPath"/>）。</summary>
        public string DisplayPathToolTip =>
            DisplaySourcePath is { Length: > 0 } path
                ? path
                : (_ownFilePath.Length > 0 ? _ownFilePath : CurrentPath);

        /// <summary>「完整路径」那一列显示什么（同上：这一行属于**用户自己那个文件**）。</summary>
        public string DisplayPath => DisplayPathToolTip;

        /// <summary>「当前后缀」那一列显示什么（⛔ 必须与 <see cref="DisplayFileName"/> 同步：
        /// 显示 `111.z02` 却把后缀写成 `.zip` 就是自相矛盾 —— 用户 2026-10-06 当场报的"后缀也不同步"）。</summary>
        public string DisplayExtension
        {
            get
            {
                string name = DisplayFileName;

                if (name.Length == 0)
                {
                    return CurrentExtension;
                }

                return Path.GetExtension(name) is { Length: > 0 } extension ? extension : "无";
            }
        }

        /// <summary>
        /// **显示那几列一起发变更通知**（⛔ 一处改动、五列同步）。
        ///
        /// <para>为什么必须显式发：这几列是**计算属性**（依赖 <see cref="_userFacingFilePath"/> 与当前路径），
        /// 而改名 / 重扫走的是 <see cref="RefreshPathRelatedProperties"/> —— 它只会为
        /// `FileName` / `DirectoryPath` / `CurrentExtension` 发通知，计算属性收不到
        /// ⇒ **那一行就停在旧名字上**（用户 2026-10-06 截图里第 2、3 行正是如此：
        /// 盘上已改成 `111.z02` / `111(2)_.zip`，列表还显示脏名）。</para>
        /// </summary>
        /// <summary>
        /// 「错误信息」那一列显示什么：**被借去当单元的那一行不显示单元的内部过程详情**
        /// （用户 2026-10-07 原话：「列表里面还是显示 111.z03 解压了三次」—— 他看到的正是那一格里的
        /// 「已完成 3 层递归解压…」）。
        ///
        /// <para>口径①：这一行属于**你导入的那个文件**，它的结论就是"这一组解压成功"；
        /// 递归分几层、产物从哪个工作区搬出来，属于**单元的过程详情**，⛔ 不该占用户这一行
        /// （要看全过程去①页「详情 / 右键复制任务信息」或日志）。⛔ 只改显示，`ErrorMessage` 一个字不动。</para>
        /// </summary>
        public string DisplayErrorMessage =>
            _userFacingFilePath.Length > 0 && Status == StatusText.ExtractSuccess
                ? string.Empty
                : ErrorMessage;

        public void NotifyDisplayIdentityChanged()
        {
            OnPropertyChanged(nameof(DisplayFileName));
            OnPropertyChanged(nameof(DisplaySizeText));
            OnPropertyChanged(nameof(DisplayPathToolTip));
            OnPropertyChanged(nameof(DisplayPath));
            OnPropertyChanged(nameof(DisplayExtension));
            OnPropertyChanged(nameof(StatusDisplayText));
            OnPropertyChanged(nameof(DisplayErrorMessage));
        }

        /// <summary>
        /// **这一行仍旧显示"用户自己那个文件"**（真机 CCCC 2026-10-06，用户原话：
        /// 「48MB 在列表里面显示到了 111(4)，也就是列表里面第一个……这个你到目前还没改回来」
        /// 「也就是你没有真正的移动只是显示问题的情况，赶紧修」）。
        ///
        /// <para><b>现场</b>：批末补判把这一单的**起点**改写到"别的包解出来的入口包"上
        /// （`…\111(4)\111.z03` → `…\111\111\111.zip`，48.35 MB）—— `CurrentPath` 的 setter 顺手把
        /// 名称 / 目录 / 体积整行刷新成那一份过程物 ⇒ 用户导入的那个文件在列表里"变成了 48MB 的东西"。</para>
        ///
        /// <para>⛔ **只改显示**：<see cref="CurrentPath"/>（真正拿去解压的起点）、`Outcome`、
        /// 借片账、落点……一个字节都不动；⛔ 起点与用户自己的文件**在同一层**（同一组里自己人的另一卷，
        /// `set.7z.002` → `set.7z.001`）时显示照旧；⛔ 认不出用户那个文件（哪条路径都不在盘上）⇒
        /// 什么都不做（显示照旧，⛔ 不猜）。</para>
        /// </summary>
        /// <param name="startPointPath">改写之后的起点（那一份过程物）；与它同名的那条路径不会被选中。</param>
        /// <param name="hintPath">
        /// 调用方替我们找到的"用户自己那一份现在在哪"（改名之后 `OriginalPath` 会过期，
        /// 而账上那几卷可能散在别的目录 ⇒ 只有调用方拿得到"同目录 + 同族"那个判据）。
        /// 它会被**优先**采用；⛔ 仍然要求它在最初导入的那个目录里。
        /// </param>
        public void ShowUserFileIdentity(string? startPointPath, string? hintPath = null)
        {
            if (string.IsNullOrWhiteSpace(OriginalPath))
            {
                return;
            }

            string originalDirectory = Path.GetDirectoryName(OriginalPath) ?? string.Empty;
            string startDirectory = string.IsNullOrWhiteSpace(startPointPath)
                ? string.Empty
                : Path.GetDirectoryName(startPointPath) ?? string.Empty;

            if (string.Equals(originalDirectory, startDirectory, StringComparison.OrdinalIgnoreCase))
            {
                // 起点就在自己旁边（自己人另一卷）⇒ 显示照旧（用户要看的是"程序真去解的那一份"）。
                return;
            }

            foreach (string path in EnumerateOwnPathsForDisplay().Prepend(hintPath ?? string.Empty))
            {
                if (string.IsNullOrWhiteSpace(path)
                    || string.Equals(path, startPointPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                /*
                 * ⛔ **只认"和最初导入那一份同一个目录"的候选**：用户可以改名
                 * （`111.z0删除2` → `111.z02`），但那一份东西一直在他放的那个目录里；
                 * 账上那几卷可能散在**别的目录**（别的单的片）—— 拿它们顶替就成了"这一行显示别人"。
                 */
                if (!string.Equals(
                        Path.GetDirectoryName(path) ?? string.Empty,
                        originalDirectory,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                _userFacingFilePath = path;
                RememberOwnFile(path, allowReplace: true);
                OnPropertyChanged(nameof(DisplayFileName));
                OnPropertyChanged(nameof(DisplaySizeText));
                OnPropertyChanged(nameof(DisplayPathToolTip));
                OnPropertyChanged(nameof(DisplayPath));
                OnPropertyChanged(nameof(DisplayExtension));
                return;
            }
        }

        /// <summary>这一单"自己那个文件"可能在哪几条路径上（最初导入的那一份 + 账上那几卷 + 当前路径）。</summary>
        private IEnumerable<string> EnumerateOwnPathsForDisplay()
        {
            yield return OriginalPath;

            foreach (string path in VolumePaths)
            {
                yield return path;
            }

            yield return CurrentPath;
        }

        /// <summary>读一个文件的体积文字（读不到 ⇒ 短横，⛔ 不显示 0 字节）。</summary>
        private static string DescribeSize(string path)
        {
            try
            {
                return File.Exists(path) ? FormatSizeText(new FileInfo(path).Length) : "-";
            }
            catch
            {
                return "-";
            }
        }

        /// <summary>
        /// 人读的字节数（列表列宽有限，所以最多两位小数、单位取最合适的那一档）。
        /// 单位与 <c>Storage/TaskSpaceEstimate.FormatSize</c> **完全一致**（KiB / MiB / GiB / 字节）——
        /// 界面上两处对同一块盘报出两种单位，用户会以为程序在算两个不同的数（历史上被投诉过）。
        /// 这里自带一份实现是为了**不让 Models 层反向依赖 Storage 层**（分层铁律）。
        /// </summary>
        private static string FormatSizeText(long bytes)
        {
            string[] units = { "字节", "KiB", "MiB", "GiB", "TiB" };
            double value = bytes;
            int unit = 0;

            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0
                ? $"{bytes} {units[0]}"
                : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
        }

        /// <summary>
        /// 这次导入时用户选中的那个根：选文件时是文件全路径，选文件夹时是**文件夹全路径**
        /// （同一个文件夹扫出来的任务共用它）。空 = 没记下（落点解析会回落到包基名，绝不落到别处）。
        /// </summary>
        public string SourceSelectionRoot { get; set; } = string.Empty;

        /// <summary>
        /// 当前最后一个后缀。
        /// 没有后缀时为“无”。
        /// </summary>
        public string CurrentExtension
        {
            get => _currentExtension;
            set => SetProperty(ref _currentExtension, string.IsNullOrWhiteSpace(value) ? "无" : value);
        }

        /// <summary>
        /// 文件头检测格式。
        /// ZIP / 7Z / RAR4 / RAR5 / GZIP / BZIP2 / XZ / TAR / Unknown。
        /// </summary>
        public string DetectedFormat
        {
            get => _detectedFormat;
            set
            {
                if (SetProperty(ref _detectedFormat, string.IsNullOrWhiteSpace(value) ? "Unknown" : value))
                {
                    OnPropertyChanged(nameof(DetectedFormatDisplay));
                }
            }
        }

        /// <summary>
        /// 这一单开工前，程序**自动把它（或它那一组）的分卷名改回了标准名**（用户 2026-09-28）。
        ///
        /// <para>为什么要记：改名是**解压前的准备步骤**，"跳过 / 失败"是**解压这一单的结论** ——
        /// 真机上用户看到的就是"界面说跳过、日志说跳过，可名字确实被改了"，读起来自相矛盾。
        /// 记下来之后状态那一列会写成 <c>已跳过（已改回标准名）</c>，一眼能看出"改过名"与"没解"是两件事。</para>
        /// </summary>
        public bool VolumeNameAutoRenamed { get; set; }

        /// <summary>
        /// **把这一组解开的那一单**的名字（显示名）—— 收场那一刻发现"这一片已经被整组接手"时记下，
        /// 空 = 没这回事（绝大多数任务都是空）。
        ///
        /// <para>为什么要有这个结构化事实（用户 2026-10-07：「列表里面最下面的两个，从来没有变化」）：
        /// 那两行（`111.rar` / `111(2)_.zip`）的状态格原来永远写「已跳过 100%」—— 判据只剩文案可读，
        /// 而项目规矩 ⛔ 不许拿中文文案当判据。记下"是谁解的"之后，状态格能如实说
        /// 「这一片随整组解开（由「111.z03」那一单解的）」，⛔ `Status` / `Outcome` 不动。</para>
        /// </summary>
        public string SettledWithGroupUnitName { get; set; } = string.Empty;

        /// <summary>
        /// 收场那一刻抄下来的**另一半事实**：这一单**自己那一趟已经跑完并定稿成功**，
        /// 之后它产出的那一片才被整组接手（<see cref="SettledWithGroupUnitName"/> 非空）。
        ///
        /// <para>用户 2026-10-10 拍板「两句都写」：真机 `111(3).rar` 自己「已完成 1 层递归解压 → 定稿完成
        /// → 输出校验通过」（产物 `111(3)\111(3)\111.z02` 就在盘上），批末又按"片被整组接手"收场 ⇒
        /// 状态格只写「这一片随整组解开」，把"你自己那份其实解成了"这半句吃掉了。</para>
        ///
        /// <para>⛔ 判据只在收场那一处给（与批末账目同一个出口：还在"缺卷待批末判"名单里的说明它自己停在中途）；
        /// ⛔ 名单是过程事实、会被清 ⇒ 必须当场抄成这个持久事实，显示才不会随名单抖动；
        /// ⛔ 它**只喂显示** —— 统计 / 删除闸门 / 清单一个字都不许读它（统计只认 <see cref="Outcome"/>）。</para>
        /// </summary>
        public bool OwnRunSucceededBeforeGroupSettlement { get; set; }

        /// <summary>
        /// **这一单已经进了"缺卷待批末判"名单**（结构化事实位，不是状态）。
        ///
        /// <para>用户 2026-10-08 口径：「没有到最后一步都是先跳过」——批中间不许把「分卷缺失」
        /// 当结论摆出来（缺的那几片可能被同批别的包补上），只有批末那一站才判。</para>
        ///
        /// <para>⛔ 它**不参与任何写盘判据**（删源包 / 搬其余物 / 落点 / 工作区一律读 <c>Outcome</c>
        /// 与校验枚举）：只喂两处显示/统计 —— ①页状态格（<see cref="StatusDisplayText"/>）与
        /// 批末计数（<c>BatchOutcomeTally</c>）。写它 / 清它的地方只有协调器那两处。</para>
        /// </summary>
        public bool IsVolumeDeficitDeferred
        {
            get => _isVolumeDeficitDeferred;
            set
            {
                if (_isVolumeDeficitDeferred == value)
                {
                    return;
                }

                _isVolumeDeficitDeferred = value;
                OnPropertyChanged(nameof(IsVolumeDeficitDeferred));
                OnPropertyChanged(nameof(StatusDisplayText));
                OnPropertyChanged(nameof(StatusColorKey));
            }
        }

        /// <summary>
        /// 「检测格式」那一列**给用户看的**说法（用户 2026-09-28：续卷显示 Unknown 会读成"没认出来"）。
        ///
        /// <para>续卷（`x.7z.002`）是裸切块、**没有文件头魔数**，所以内容格式本来就判不了 ——
        /// 那是"物理上判不了"，不是"我们不认识"。后缀状态那一列才是结论（分卷后缀）。
        /// 这里只改**显示**：格式 Unknown + 后缀状态是分卷 → 说「分卷续卷（无文件头）」。
        /// 日志里照旧写 Unknown（机器事实不改），免得排查时对不上。</para>
        /// </summary>
        public string DetectedFormatDisplay =>
            string.Equals(_detectedFormat, "Unknown", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ExtensionStatus, StatusText.ExtensionVolume, StringComparison.Ordinal)
                ? "分卷续卷（无文件头）"
                : _detectedFormat;

        /// <summary>
        /// 建议后缀。
        /// 例如 .zip / .7z / .rar / .gz / .bz2 / .xz / .tar。
        /// </summary>
        public string SuggestedExtension
        {
            get => _suggestedExtension;
            set => SetProperty(ref _suggestedExtension, value ?? string.Empty);
        }

        /// <summary>
        /// 后缀状态。
        /// 后缀正常 / 后缀缺失 / 后缀不匹配 / 多重后缀疑似伪装 / 格式未知 / 非压缩包。
        /// </summary>
        public string ExtensionStatus
        {
            get => _extensionStatus;
            set
            {
                if (SetProperty(ref _extensionStatus, string.IsNullOrWhiteSpace(value) ? StatusText.NotChecked : value))
                {
                    // 后缀状态决定「检测格式」那一列该怎么念（续卷要改口径，见 DetectedFormatDisplay）。
                    OnPropertyChanged(nameof(DetectedFormatDisplay));
                }
            }
        }

        /// <summary>
        /// 改名前预览路径。
        /// </summary>
        public string RenamePreviewPath
        {
            get => _renamePreviewPath;
            set => SetProperty(ref _renamePreviewPath, value ?? string.Empty);
        }

        /// <summary>
        /// 单任务密码。
        /// 只允许存在内存中。
        /// 严禁写入日志。
        /// </summary>
        public string Password
        {
            get => _password;
            set => SetProperty(ref _password, value ?? string.Empty);
        }

        /// <summary>
        /// 密码状态。
        /// 未检测 / 不需要密码 / 需要密码 / 已提供密码 / 密码错误 / 密码正确。
        /// </summary>
        public string PasswordStatus
        {
            get => _passwordStatus;
            set => SetProperty(ref _passwordStatus, string.IsNullOrWhiteSpace(value) ? StatusText.NotChecked : value);
        }

        /// <summary>
        /// 输出目录。
        /// </summary>
        public string OutputPath
        {
            get => _outputPath;
            set => SetProperty(ref _outputPath, value ?? string.Empty);
        }

        /// <summary>
        /// 当前操作。
        /// 等待 / 扫描 / 改名 / 测试 / 解压 / 跳过 / 完成。
        /// </summary>
        public string Operation
        {
            get => _operation;
            set => SetProperty(ref _operation, string.IsNullOrWhiteSpace(value) ? StatusText.OpWaiting : value);
        }

        /// <summary>
        /// 当前状态。
        /// 等待扫描 / 扫描中 / 已识别 / 格式未知 / 等待改名 / 改名成功 / 改名失败 /
        /// 等待测试 / 测试中 / 测试通过 / 测试失败 /
        /// 等待解压 / 解压中 / 解压成功 / 解压失败 /
        /// 密码错误 / 文件损坏 / 权限不足 / 输出路径冲突 / 分卷缺失 /
        /// 已跳过 / 已取消 / 未知错误。
        /// </summary>
        public string Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, string.IsNullOrWhiteSpace(value) ? StatusText.WaitingScan : value))
                {
                    OnPropertyChanged(nameof(StatusDisplayText));
                    OnPropertyChanged(nameof(DisplayErrorMessage));
                }
            }
        }

        /// <summary>
        /// 进度文本。
        /// 第一阶段可以为：
        /// - / 处理中 / 完成。
        /// </summary>
        public string ProgressText
        {
            get => _progressText;
            set => SetProperty(ref _progressText, string.IsNullOrWhiteSpace(value) ? "-" : value);
        }

        /// <summary>
        /// 引擎报上来的**百分比**（0–100）；<see cref="NoProgress"/> = 当前没有可见进度。
        ///
        /// <para>
        /// 由 <c>ExtractionCoordinator</c> 把引擎层节流后的 <c>ArchiveProgress</c> 落到这里
        /// （节流在引擎层做，见 <c>ArchiveProgressReporter</c>：最多 250ms 一次）。
        /// 它是**瞬时状态**：任务一收尾就必须清掉，否则已完成/失败的任务还会挂着"解压中 45%"
        /// （不变量 6：失败 / 部分完成不得显示成成功，反过来也不许把收尾后的进度留在界面上）。
        /// </para>
        /// </summary>
        public int ProgressPercent
        {
            get => _progressPercent;
            set
            {
                if (SetProperty(ref _progressPercent, value))
                {
                    OnPropertyChanged(nameof(HasLiveProgress));
                    OnPropertyChanged(nameof(StatusDisplayText));
                    OnPropertyChanged(nameof(DisplayErrorMessage));
                    OnPropertyChanged(nameof(ProgressDetail));
                }
            }
        }

        /// <summary>当前正在处理的条目名（引擎给的；未知时为空串）。</summary>
        public string ProgressEntry
        {
            get => _progressEntry;
            set
            {
                if (SetProperty(ref _progressEntry, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(ProgressDetail));
                }
            }
        }

        /// <summary>
        /// "很长时间没有收到任何引擎输出"的提示（默认空 = 没有这条提示）。
        ///
        /// <para>
        /// 它对应的是用户反复抱怨的"卡死"：界面只有"处理中/完成"两态时，长时间零输出看起来就是死了。
        /// 这里只**提示**，不改变任务状态、不杀进程 —— 要不要中止由用户点「取消当前」决定（不变量 9）。
        /// 有新的进度 / 输出时由协调器清回空串。
        /// </para>
        /// </summary>
        public string ResponsivenessHint
        {
            get => _responsivenessHint;
            set
            {
                if (SetProperty(ref _responsivenessHint, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasResponsivenessHint));
                    OnPropertyChanged(nameof(StatusDisplayText));
                    OnPropertyChanged(nameof(DisplayErrorMessage));
                    OnPropertyChanged(nameof(ProgressDetail));
                }
            }
        }

        /// <summary>是否有"长时间无响应"这条提示。</summary>
        public bool HasResponsivenessHint => !string.IsNullOrWhiteSpace(_responsivenessHint);

        /// <summary>
        /// 现在是不是**真的在跑并且有进度可看**。
        ///
        /// <para>
        /// 判据是"有百分比" **且** "任务还没结束"（<see cref="EndTime"/> 为空）。
        /// 用 <see cref="EndTime"/> 而不是枚举状态，是因为所有收尾路径
        /// （成功 / 失败 / 取消 / 跳过 / 部分完成）都会写它 —— 一条判据盖住全部，
        /// 不会因为将来新增一种失败状态就漏掉一处"失败任务还显示着 45%"。
        /// </para>
        /// </summary>
        public bool HasLiveProgress => _progressPercent >= 0 && _endTime == null;

        /// <summary>
        /// 主界面「状态」列显示的那一格：**不新增列**，把百分比紧凑地并进状态文案。
        ///
        /// <para>
        /// 例：<c>解压中 45%</c>；有长时间无响应提示时是 <c>解压中 45% · 长时间无响应</c>。
        /// 任务一收尾就退回纯状态文案（见 <see cref="HasLiveProgress"/>）。
        /// </para>
        /// <para>
        /// ⚠ 它**不新造任何状态字符串**：前缀永远是既有的 <see cref="Status"/> 值（来自
        /// <c>StatusText</c> 常量），这里只做拼接，所以统计与配色（都看 <see cref="Status"/>）完全不受影响。
        /// </para>
        /// </summary>
        /// <summary>
        /// **状态那一格配色读的键**（⛔ 不是新状态常量）：默认就是 <see cref="Status"/>；
        /// 只有"缺卷待批末判"那一档换成中性档 <see cref="StatusText.Skipped"/>
        /// —— 用户 2026-10-08 点名的错：「为什么跳过要标红，这是非常错误的行为，这到了最后一步才判断的」。
        ///
        /// <para>为什么要有它：那一格的**文字**读 <see cref="StatusDisplayText"/>（批中间说"跳过"），
        /// 而**颜色**原先绑的是机器状态 ⇒ 待判那一行说着"跳过"却顶着红色。
        /// 现在两处（`TaskTab.xaml` / `TaskDetailWindow.xaml`）都绑这个键，⛔ 不再各绑各的。</para>
        /// </summary>
        public string StatusColorKey => IsVolumeDeficitDeferred ? StatusText.Skipped : Status;

        public string StatusDisplayText
        {
            get
            {
                /*
                 * ===== 口径①（用户 2026-10-06 拍板）：一行只讲"你导进来的那个文件" =====
                 *
                 * 批末会有一行被"借去"当**解开这一组的那个单元**（起点被改写到入口包上，
                 * 见 <see cref="_userFacingFilePath"/> 的说明）。那一行顶上写的是你的文件，
                 * 状态却写"解压成功" —— 读起来像"你这一片被单独解成功了"。⇒ 这一档改说
                 * **这一组**（显示文案而已，`Status` 一个字不动 ⇒ 判据 / 名单 / 配色全不受影响）。
                 */
                string text = _userFacingFilePath.Length > 0 && Status == StatusText.ExtractSuccess
                    ? StatusText.ExtractSuccessAsGroup
                    : Status;

                /*
                 * ===== 「这一片已经被整组接手」的行不许永远停在「已跳过」=====
                 *
                 * 用户 2026-10-07 原话：「列表里面最下面的两个，从来没有变化」—— `111.rar` 与
                 * `111(2)_.zip` 两行各自只吐出一片、那片被接进整组解开了，可它们的状态格一直写着
                 * 「已跳过 100%」，读起来像"程序什么都没干"。⇒ 有那条**结构化事实**
                 * （<see cref="SettledWithGroupUnitName"/> 非空 = 收场那一刻记下的"是谁把整组解开的"）
                 * 时，状态格如实说这件事。⛔ `Status` / `Outcome` 一个字不动 ⇒ 统计、名单、配色不受影响。
                 */
                if (SettledWithGroupUnitName.Length > 0 && Status == StatusText.Skipped)
                {
                    /*
                     * ⚠ 2026-10-10（用户拍板「两句都写」）：这一单自己那一趟**也**跑成了的时候
                     * （<see cref="OwnRunSucceededBeforeGroupSettlement"/>，收场那一刻抄下来的事实），
                     * 只写后半句就把"你自己那份其实解成了"吃掉了 —— 两个事实都写。
                     * ⛔ 措辞不另造：前半句复用 `StatusText.ExtractSuccess` 的说法，后半句复用原来那一句。
                     */
                    text = string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        OwnRunSucceededBeforeGroupSettlement
                            ? StatusText.ExtractSucceededThenPieceSettledWithGroupFormat
                            : StatusText.PieceSettledWithGroupFormat,
                        SettledWithGroupUnitName);
                }

                /*
                 * ===== 批中间撞上缺卷的那一档：**先跳过，不许把结论摆出来** =====
                 *
                 * 用户 2026-10-08 口径：「没有到最后一步都是先跳过」——缺的那几片完全可能被同一批
                 * 别的包解出来（真机 `111.z01` 就是这样补上的），批末那一站才该下结论。
                 *
                 * ⚠ 这一档**连「部分完成」也要盖掉**（用户 2026-10-08 第二句：「批中间连部分完成也不许显示，
                 * 一律跳过（等最后判）」）：递归停在缺卷、但前面已经解出过东西时，机器状态就是「部分完成」——
                 * 那个结论同样是**下早了**（缺的那几片可能被同批别单补上）。
                 *
                 * ⛔ `Status` / `Outcome` 一个字不动（发布链、其余物、删除闸门读的都是它）——
                 * 这里只把**显示**改说成"跳过（缺卷，等批末再判）"；批末定稿时协调器把事实位清掉，
                 * 那一格随即回到真结论。
                 */
                if (IsVolumeDeficitDeferred &&
                    (Status == StatusText.VolumeMissing || Status == StatusText.PartiallyCompleted))
                {
                    text = StatusText.VolumeDeficitPendingText;
                }

                /*
                 * 百分比什么时候显示（用户 2026-09-27 真机："上面怎么都显示解压成功 99%，
                 * 连一个 100% 的都没有"）：
                 *
                 * · 正在跑（HasLiveProgress）→ 显示实时百分比；
                 * · **已经成功** → 一律显示 100%。
                 *   为什么成功这一档要**写死 100**、而不是读存下来的百分比：
                 *   ① 引擎报的最后一帧常常只到 99（`Everything is Ok` 之前那一次）；
                 *   ② 收尾有好几条路（MarkSuccess / 定稿收尾 / 各分支早退），其中有的会
                 *      `ClearProgress()` 把百分比清成 -1 —— 读字段就会一会儿 99、一会儿没有。
                 *   成功的判据是**机器终态**（Outcome），不是那个瞬时字段，所以这里按终态说话。
                 *
                 * ⛔ 失败 / 部分完成 / 取消**不显示**百分比：配上 100% 会自相矛盾
                 *   （"解压失败 100%"读起来像成功了，正是不变量 6 最忌讳的那种形态）。
                 */
                int? percent = HasLiveProgress
                    ? _progressPercent
                    : Outcome == TaskOutcome.Succeeded ? 100 : null;

                /*
                 * 续解链的第几层说在括号里（用户 2026-10-07：「111.z03 显示解压了三遍 100%」）：
                 * 递归每开一层都会补一帧 0%（否则进度条会停在上一层的 100%），行里看不出"换层了"
                 * ⇒ 同一行就像被解了三遍。层号是从引擎进度那一帧里**结构化**传上来的
                 * （<see cref="ApplyProgress"/> 的 layer），⛔ 不去解析条目文字。
                 */
                if (HasLiveProgress && _liveLayer > 0)
                {
                    text += string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.LiveLayerSuffixFormat,
                        _liveLayer);
                }

                if (percent.HasValue)
                {
                    text += $" {percent.Value}%";
                }

                if (HasResponsivenessHint)
                {
                    text += $" · {_responsivenessHint}";
                }

                /*
                 * "改过名"和"这一单没解成"是两件事（用户 2026-09-28 真机："界面上显示跳过，
                 * 日志上也显示跳过，但是确实是改好了名字了，这是什么情况"）。
                 * 分卷名是**解压前**自动改好的；解压这单照样可能跳过 / 失败 —— 说清楚，别让人以为白改了。
                 */
                if (VolumeNameAutoRenamed)
                {
                    text += "（已改回标准名）";
                }

                /*
                 * 最后把**这一行经历过的变换**接上（用户 2026-10-07：「我们最后想看到 111.rar 通过解压缩
                 * 两层变换到 111.zip，用户如果看到你什么都不动就会以为你什么都没改变」）——
                 * 链本身只由 <see cref="DisplayTransformationText"/> 从程序记录里拼，⛔ 这里不编内容。
                 */
                if (DisplayTransformationText.Length > 0)
                {
                    text += StatusText.TransformationChainSeparator + DisplayTransformationText;
                }

                return text;
            }
        }

        /// <summary>
        /// 任务详情窗口「进度」那一行：**百分比 + 当前条目**（没有实时进度时退回既有的进度文案）。
        ///
        /// 已用时间不在这里拼 —— 详情窗口本来就有「耗时」一行，协调器在每次进度上报时
        /// 顺带刷新 <see cref="ElapsedText"/>，于是它也会跟着实时走。
        /// </summary>
        public string ProgressDetail
        {
            get
            {
                if (!HasLiveProgress)
                {
                    return ProgressText;
                }

                string percent = $"{_progressPercent}%";

                if (string.IsNullOrWhiteSpace(_progressEntry))
                {
                    return HasResponsivenessHint ? $"{percent} · {_responsivenessHint}" : percent;
                }

                return HasResponsivenessHint
                    ? $"{percent} · {_progressEntry} · {_responsivenessHint}"
                    : $"{percent} · {_progressEntry}";
            }
        }

        /// <summary>
        /// 把引擎报上来的进度落到任务上（只允许在 UI 线程调用 —— 协调器负责投递）。
        /// </summary>
        /// <param name="layer">
        /// 这一帧属于续解链的第几层（&gt;0 才认；0 = 不是递归层 / 不知道）。
        /// 一旦记下就**粘住**到收尾（<see cref="ClearProgress"/> 才清）：引擎后续那些帧不带层号，
        /// 不粘住的话状态格里那半句"（整组第 2 层）"会一闪就没。
        /// </param>
        public void ApplyProgress(int percent, string? entry, int layer = 0)
        {
            if (percent >= 0)
            {
                ProgressPercent = percent > 100 ? 100 : percent;
            }

            if (!string.IsNullOrWhiteSpace(entry))
            {
                ProgressEntry = entry.Trim();
            }

            if (layer > 0 && layer != _liveLayer)
            {
                _liveLayer = layer;

                /*
                 * 变换链的第几步：**只记程序自己那一帧说的**（`第 N 层：<那一层的包>`），
                 * ⛔ 不在这里推算、不猜名字（用户 2026-10-07：「谁和你说第一层得到 111.part1.rar，
                 * 你不要给我瞎猜，看程序怎么弄」）。
                 */
                string step = string.IsNullOrWhiteSpace(entry)
                    ? string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.LiveLayerSuffixFormat,
                        layer)
                    : entry.Trim();

                if (_transformationSteps.Count == 0 || !string.Equals(_transformationSteps[^1], step, StringComparison.Ordinal))
                {
                    _transformationSteps.Add(step);
                }

                OnPropertyChanged(nameof(StatusDisplayText));
                OnPropertyChanged(nameof(DisplayTransformationText));
            }
            else if (layer > 0)
            {
                _liveLayer = layer;
                OnPropertyChanged(nameof(StatusDisplayText));
            }

            // 有进度就说明引擎还活着：把上一次的"长时间无响应"提示撤掉。
            if (HasResponsivenessHint)
            {
                ResponsivenessHint = string.Empty;
            }
        }

        /// <summary>清掉实时进度（任务收尾 / 重跑之前调用）。</summary>
        public void ClearProgress()
        {
            ProgressPercent = NoProgress;
            ProgressEntry = string.Empty;
            ResponsivenessHint = string.Empty;
            _liveLayer = 0;
        }

        /// <summary>续解链当前在第几层（0 = 不在递归层里 / 不知道）。见 <see cref="ApplyProgress"/>。</summary>
        private int _liveLayer;

        /// <summary>
        /// **日志与面向用户的文案里该写哪个名字**（唯一出口）= 与①页那一行**同一个名字**
        /// （<see cref="DisplayFileName"/>）。
        ///
        /// <para>来由（用户 2026-10-07）：「为什么是 `111.z03` 不是 `111.zip`」—— 上一版①页写的是
        /// 你自己那个文件（`111.z03`），日志前缀却还在用**内部名**（批末补判把起点改写到入口包后的
        /// `111.zip`），同一件事在屏幕上和日志里是两个名字。⛔ `FileName` / `CurrentPath` 一个字不动，
        /// 内部逻辑照旧读它们；只有"写给人看的那一处"走这里。</para>
        /// </summary>
        public string LogName => string.IsNullOrWhiteSpace(DisplayFileName) ? FileName : DisplayFileName;

        /// <summary>
        /// **这一行"你的文件"经历过的变换**（改名 / 续解每一层），一步一步攒起来，**全是程序自己的记录**：
        /// 改名那一步 = 「最初导入的名字 → 最新已知的名字」（两个名字都是任务账上的事实字段），
        /// 续解那几步 = 递归每层开头报上来的那一帧原文（`第 N 层：<那一层的包>`）。
        ///
        /// <para>来由（用户 2026-10-07）：「我们最后想看到 `111.rar` 通过解压缩两层变换到 `111.zip`，
        /// 用户如果看到你什么都不动就会以为你什么都没改变」；同一轮他还点名 ⛔ 不许我复述或猜测这条链
        /// （「谁和你说第一层得到 `111.part1.rar`，你不要给我瞎猜，看程序怎么弄」）。
        /// ⇒ 只做拼接，⛔ 一个字都不编。</para>
        /// </summary>
        private readonly List<string> _transformationSteps = new();

        /// <summary>
        /// **记下一步变换**（唯一写入出口）：调用方必须是"程序自己那条记录"的持有者 ——
        /// 改名 = <c>RenameCoordinator</c> 里那条 <c>OriginalFileName → NewFileName</c> 记录；
        /// 续解 = 递归每层报上来的那一帧（本类内部在 <see cref="ApplyProgress"/> 里记）。
        /// ⛔ 不许拿"账上两个字段比对"推出来当记录（用户 2026-10-07：「你不要给我瞎猜，看程序怎么弄」）。
        /// </summary>
        public void AppendTransformationStep(string step)
        {
            if (string.IsNullOrWhiteSpace(step))
            {
                return;
            }

            string trimmed = step.Trim();

            if (_transformationSteps.Count > 0
                && string.Equals(_transformationSteps[^1], trimmed, StringComparison.Ordinal))
            {
                return;
            }

            _transformationSteps.Add(trimmed);
            OnPropertyChanged(nameof(DisplayTransformationText));
            OnPropertyChanged(nameof(StatusDisplayText));
        }

        /// <summary>
        /// 变换链给用户看的那一行（空 = 这一步什么都没发生，不写"无"）。
        /// 例：<c>111.z0删除3 → 111.z03；第 1 层：111.rar → 第 2 层：111.zip；由「111.z03」那一单解开整组</c>。
        /// </summary>
        public string DisplayTransformationText
        {
            get
            {
                var parts = new List<string>();

                // ① 改名 / 续解每一步：**程序自己记下来的**（唯一写入出口见 AppendTransformationStep）。
                if (_transformationSteps.Count > 0)
                {
                    parts.Add(string.Join(" → ", _transformationSteps));
                }

                // ② 这一片被整组接手（收场那一刻记下的事实）。
                if (SettledWithGroupUnitName.Length > 0)
                {
                    parts.Add(string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.SettledWithGroupChainFormat,
                        SettledWithGroupUnitName));
                }

                return parts.Count == 0 ? string.Empty : string.Join("；", parts);
            }
        }

        /// <summary>
        /// 错误信息。
        /// 不能包含明文密码。
        /// </summary>
        public string ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value ?? string.Empty);
        }

        /// <summary>
        /// 是否识别为支持的压缩格式。
        /// </summary>
        public bool IsArchive
        {
            get => _isArchive;
            set => SetProperty(ref _isArchive, value);
        }

        /// <summary>
        /// 是否检测到可能需要密码。
        /// </summary>
        public bool IsEncrypted
        {
            get => _isEncrypted;
            set => SetProperty(ref _isEncrypted, value);
        }

        /// <summary>
        /// 任务开始时间。
        /// </summary>
        public DateTime? StartTime
        {
            get => _startTime;
            set
            {
                if (SetProperty(ref _startTime, value))
                {
                    UpdateElapsedText();
                }
            }
        }

        /// <summary>
        /// 任务结束时间。
        /// </summary>
        public DateTime? EndTime
        {
            get => _endTime;
            set
            {
                if (SetProperty(ref _endTime, value))
                {
                    UpdateElapsedText();

                    // 任务一收尾就再也没有"实时进度"这回事（不变量 6 的反面同样成立：
                    // 跑完的任务不该还挂着"解压中 45%"）。
                    OnPropertyChanged(nameof(HasLiveProgress));
                    OnPropertyChanged(nameof(StatusDisplayText));
                    OnPropertyChanged(nameof(DisplayErrorMessage));
                    OnPropertyChanged(nameof(ProgressDetail));
                }
            }
        }

        /// <summary>
        /// 耗时显示。
        /// 例如 00:00:03。
        /// </summary>
        public string ElapsedText
        {
            get => _elapsedText;
            set => SetProperty(ref _elapsedText, string.IsNullOrWhiteSpace(value) ? "-" : value);
        }

        /// <summary>
        /// 最后更新时间。
        /// </summary>
        public DateTime LastUpdatedTime
        {
            get => _lastUpdatedTime;
            set => SetProperty(ref _lastUpdatedTime, value);
        }

        /// <summary>
        /// 是否存在单任务密码。
        /// </summary>
        public bool HasTaskPassword => !string.IsNullOrEmpty(Password);

        /// <summary>
        /// 是否存在错误信息。
        /// </summary>
        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
        /// <summary>
        /// 本任务是否代表"一组分卷"（AGENTS.md §9.3）。
        /// 一组分卷 = 一个任务：只从第一卷启动解压，其余卷不作为独立任务出现。
        /// </summary>
        public bool IsVolumeGroup { get; set; }

        /// <summary>分卷组的归组键（目录 + 基名）。失败重试与清理源包时靠它找回同一组。</summary>
        public string VolumeGroupKey { get; set; } = string.Empty;

        /// <summary>这一组已找到的分卷文件（含第一卷），按卷序排列。</summary>
        public List<string> VolumePaths { get; } = new();

        /// <summary>已找到的卷数。</summary>
        public int VolumeCount => VolumePaths.Count;

        /// <summary>卷序是否从 1 连续、无缺口。</summary>
        public bool IsVolumeComplete { get; set; } = true;

        /// <summary>缺失的卷文件名（总数无法确定时为空）。</summary>
        public List<string> MissingVolumeNames { get; } = new();

        /// <summary>分卷情况的一句话说明，直接显示给用户。</summary>
        public string VolumeInfoText { get; set; } = string.Empty;

        /// <summary>
        /// 「分卷」那一列**显示**什么（用户 2026-10-07：三列过期里点名的「分卷」—— 已经解开整组的行
        /// 上还留着扫描那一刻的「共 1 卷，缺 111.zip、111.z01…」）。
        ///
        /// <para>判据只用**结构化事实**：这一片被整组接手过（<see cref="SettledWithGroupUnitName"/> 非空，
        /// 收场那一刻记下的）或它本身就是跟班卷、且已按"完成"收场 ⇒ 那一组已经不缺卷了，如实说一声。
        /// ⛔ 不重跑归组、⛔ 不动 <see cref="VolumePaths"/>（归组结果是收场判据的输入）、⛔ 不比中文文案。</para>
        /// </summary>
        public string DisplayVolumeInfoText =>
            SettledWithGroupUnitName.Length > 0
            || (IsVolumeGroupFollower && Outcome == TaskOutcome.Succeeded)
                ? StatusText.VolumeInfoSettledWithGroupText
                : VolumeInfoText;

        /// <summary>
        /// 被**空间门**拦下时那一刻的三个数（需求 / 可用 / 差多少）；<c>null</c> = 这一单不是被空间门拦下的。
        ///
        /// <para>写入点只有一处：<c>ExtractionCoordinator.MarkSpaceBlockedCore</c>（启动前那道门与解压前
        /// 预检共用的落点）。批末诊断清单读它来"点名差多少"—— ⛔ 谁都不许重算这三个数
        /// （重算就是第二套口径，见 <see cref="SpaceBlockedFacts"/> 的说明）。</para>
        /// </summary>
        public SpaceBlockedFacts? SpaceBlocked { get; set; }

        /// <summary>
        /// 解压后的落盘结果是否通过校验（条目数 / 总大小）。
        /// **清理源包必须以此为前置条件**：没校验通过就不许删（AGENTS.md §9.5）。
        ///
        /// <para>
        /// 它现在只是 <see cref="OutputVerification"/> 的布尔视图（写法保持兼容），
        /// **机器判定一律读枚举**：字符串/布尔都能被后来某一行代码改掉，枚举不会。
        /// </para>
        /// </summary>
        public bool IsOutputVerified
        {
            get => OutputVerification == OutputVerificationOutcome.Passed;

            /*
             * 写 false 时落成 NotAttempted（"没有通过校验"），**不是** Failed：
             * 那些 false 来自"越界 / 超预算 / 定稿失败 / 跳过"这类**定稿之前的结论**，
             * 说成"校验未通过"会给用户一个错的理由（他该看的是那一句真正的原因）。
             */
            set => OutputVerification = value
                ? OutputVerificationOutcome.Passed
                : OutputVerificationOutcome.NotAttempted;
        }

        /// <summary>
        /// 产物校验的**机器可判**结论（AGENTS.md §7）。
        ///
        /// <para>
        /// 为什么必须落在任务上：用户 2026-09-24 的真机日志里，校验已经判否、状态却仍是「解压成功」——
        /// 那一刻"删源 / 搬源 / 续解下一层 / 危险模式删其余物"四条裁决全都会误判。
        /// 现在这四条一律读它（<c>== Passed</c>），读的是**校验那一刻的事实**。
        /// </para>
        /// </summary>
        public OutputVerificationOutcome OutputVerification { get; set; } = OutputVerificationOutcome.NotAttempted;

        /// <summary>
        /// 那一次产物校验**到底有没有拿可信清单逐条核对过**（L4 完整性分类的判据之一）。
        ///
        /// <para><see cref="OutputVerification"/> 为 <see cref="OutputVerificationOutcome.Passed"/>
        /// 有两种含义：核对过（可证完整）与只做了非空底线校验（**判不出**完整性）。
        /// 老口径下两者在任务上长得一模一样，删源包那道不可逆的闸门分不开它们 ——
        /// 用户 2026-09-30 定的红线是"判不出 ⇒ 一律不删源"，所以这个事实必须落在任务上。</para>
        ///
        /// <para>唯一写入点：<c>ExtractionCoordinator.RunPostProcessWork</c> 里从
        /// <c>OutputVerificationResult.ManifestCrossChecked</c> 抄下来的那一行。</para>
        /// </summary>
        public bool OutputManifestCrossChecked { get; set; }

        /// <summary>
        /// 这一次的 L3 预期清单**来自哪一层**（或者为什么取不到）—— 唯一写入点是
        /// <c>ExtractionCoordinator.PostProcessSuccessAsync</c> 里
        /// <c>ChainManifestResolver.Resolve</c> 的那一行（⛔ 别处不许再判一遍层数）。
        ///
        /// <para>为什么要落在任务上：L4 的唯一出口 <c>ResultCompletenessClassifier</c> 从任务上读它，
        /// 于是"源包为什么被留下"这条结论能一路带到每一个删除闸门的日志里 ——
        /// 用户 2026-09-30 报的现场里，日志只写了"没有可用的归档清单可核对"，
        /// 一个字都没说"到底是哪一层、为什么"，他只能猜。</para>
        /// </summary>
        public ManifestExpectation ManifestExpectation { get; set; } = ManifestExpectation.None;

        /// <summary>
        /// 任务的**机器可判终态**（AGENTS.md §7 要求的那类"能被机器判定"的状态）。
        ///
        /// <para>
        /// 与 <see cref="Status"/> 的分工：那个是**给人看的**中文文案（配色、列表显示、汇总文案），
        /// 这个是给代码判的。凡是"要不要删用户的源文件 / 要不要继续往下解"这种不可逆或会放大的决定，
        /// 一律读这个枚举，⛔ **不许**再写 <c>task.Status == "解压成功"</c> 这类字符串比较
        /// （用户 2026-09-24 明确要求：删除的裁决只准看事实，不准看状态字符串）。
        /// </para>
        /// <para>
        /// 默认 <see cref="TaskOutcome.Pending"/> = "还没有结论"，而所有门都要求
        /// <see cref="TaskOutcome.Succeeded"/>，所以**漏设时的方向是保守的**（宁可不删、不续解）。
        /// </para>
        /// </summary>
        public TaskOutcome Outcome { get; set; } = TaskOutcome.Pending;

        /// <summary>校验结论的一句话说明。</summary>
        public string VerifyMessage { get; set; } = string.Empty;

        /// <summary>
        /// 这一次是**定稿搬运整体失败**（暂存区 → 输出目录一条内容物都没搬过去）。
        ///
        /// <para>为什么要单独一个事实位（用户 2026-10-04 真机）：这类失败的结论与原因**不是引擎报的**
        /// （引擎那一步早就成功了、校验也通过了），于是批末诊断把它兜底归进「其他失败」，
        /// 「下一步」再让人去"看引擎原话" —— 而它根本没有引擎原话，用户被指错了方向。
        /// 批末诊断靠这一位把它单独点出来（<c>BatchSummaryDiagnosticsRules</c>，⛔ 那里不比中文文案）。</para>
        ///
        /// <para>唯一写入点：<c>ExtractionCoordinator</c> 处理 <c>PostProcessWorkResult.CommitFailed</c>
        /// 的那一支；唯一清零点 = <c>ExtractionCoordinator.ExtractSingleTaskAsync</c> 开工时清
        /// "上一轮结论"的那一处（任务对象是复用的）。</para>
        /// </summary>
        public bool CommitMoveFailed { get; set; }

        /// <summary>
        /// **这一单的其余物为什么原封不动地留着**（非空 = 没按「删除操作」处理过，文字里带路径与原因）。
        ///
        /// <para>为什么要单列一条（用户 2026-10-04 真机）：日志 271/272 明明写着
        /// 「<c>Sociology.7z：链尾的其余物不处理（链上的「老王.apk」没有成功…）</c>」，
        /// 而①页、批末诊断、失败清单里**一个字都没有** —— 他在界面上看到的是
        /// "解压成功、其余物还在"，只能自己去猜为什么。</para>
        ///
        /// <para>写法：唯一出口 = <c>ExtractionCoordinator.DescribeRestKeptNote</c>（那一句就是
        /// <c>StatusText.RestKeptNoteFormat</c>），三处消费者读的是**同一份文字**；
        /// 唯一清零点 = <c>ExtractSingleTaskAsync</c> 开工时清"上一轮结论"的那一处。</para>
        /// </summary>
        public string RestKeptReason { get; set; } = string.Empty;

        /// <summary>
        /// 引擎对"这到底是不是能解的容器"下的结论（例如"7-Zip 命令行 26.01 能打开：16 个文件"）。
        /// 魔数说不认识时，以引擎的结论为准 —— 这也是"结果可追溯"的一部分（不变量 14）。
        /// </summary>
        public string EngineVerdict { get; set; } = string.Empty;

        /// <summary>
        /// 内嵌归档在 <see cref="CurrentPath"/> 里的起始偏移，**0 表示没有内嵌归档**。
        ///
        /// 由识别阶段写进来，解压阶段据此把 <c>[偏移, EOF)</c> 抠出来再交给引擎：
        /// 这种文件的 ZIP 内部偏移相对它自己，前置数据超过 7-Zip 的容忍上限（实测 8 MiB）时就报
        /// "Cannot open the file as archive"，而真实文件前面垫了 17 MB 级的数据，必然落在拒绝区。
        /// **不要**用它去改 <see cref="CurrentPath"/> —— 源文件路径是改名、清理、统计的共同依据。
        /// </summary>
        public long EmbeddedArchiveOffset { get; set; }

        /// <summary>
        /// 内嵌归档在 <see cref="CurrentPath"/> 里的**结束位置（不含）**：<c>EOCD + 22 + 注释长度</c>。
        ///
        /// <para>
        /// 为什么偏移之外还要记一个终点：真实资源包是"视频 + 完整 ZIP + 十几 KB 正常数据"，
        /// EOCD 并不在文件末尾（2026-09-24 用户机器上 5 个真文件实测 14,350–17,424 字节）。
        /// 抠取范围是 <c>[<see cref="EmbeddedArchiveOffset"/>, EmbeddedArchiveEnd)</c> ——
        /// EOCD 之后那些字节属于别的东西，不该混进产物。
        /// </para>
        ///
        /// **0（或 ≤ 起点）表示"不知道，按文件末尾算"**：这条默认值保证老路径与旧行为逐字节一致。
        /// </summary>
        public long EmbeddedArchiveEnd { get; set; }

        /// <summary>
        /// 这个内嵌归档能不能**直读**（识别阶段用 <c>Extraction/EmbeddedZipStreamExtractor</c> 只读探过一次）。
        ///
        /// <para><b>它只用于空间核算</b>：直读不产生那份等大的临时副本，估算就不该预留它
        /// （用户 2026-09-24 需求第 7 条：账面必须与真实动作一致）。真正解压之前会再探一次，
        /// 以那一刻的结论为准 —— 所以这个字段为 false 也**不会**让一个本来能直读的包失去直读机会。</para>
        /// </summary>
        public bool EmbeddedDirectReadSupported { get; set; }

        /// <summary>直读不支持时的原因（进日志与任务详情，说明"为什么要抠那一份副本"）。</summary>
        public string EmbeddedDirectReadReason { get; set; } = string.Empty;

        /// <summary>
        /// 「这一卷的名字被改坏了」时给出的**标准改名建议**（用户 2026-09-25 第 41 条；空 = 没有这个建议）。
        ///
        /// <para>为什么要在任务上记一份：①它是"①页那个「按建议改名并重试」按钮此刻该不该亮"的
        /// **机器判据**（⛔ 不许靠比对中文状态文案，AGENTS.md §7）；②它就是失败清单里写给用户的那个名字，
        /// 两者必须是同一份值（推法只有一处：<c>Extraction/VolumeNameRepair</c>）。</para>
        ///
        /// <para>重新识别（<see cref="ApplyDetectResult"/>）时一律清空 —— 那一轮有没有这个毛病，
        /// 由那一轮的判决说了算，绝不沿用上一轮的结论。</para>
        /// </summary>
        public string VolumeRenameSuggestion { get; set; } = string.Empty;

        /// <summary>
        /// **第 42 条**：这一单有没有"把容器里的第 1 卷与外面的后续卷接起来"（**默认关**的那个开关）。
        ///
        /// <para>为什么要在任务上记一份：那是**动过盘的动作**（在工作区里造名字 / 跨盘复制），
        /// 而日志的默认档"成功就丢"会把过程细节全丢掉 —— 记在这里，收尾那一行摘要就能带上它，
        /// 用户不必改设置就能看到"这一单接过分卷、用的是硬链接还是复制"。
        /// 拼装没做 / 没做成时是空串（原因照旧写在日志的 WARN 上）。</para>
        /// </summary>
        public string SplitVolumeAssemblyNote { get; set; } = string.Empty;

        /// <summary>结果归集后的最终位置（没有归集时为空，表示还是 OutputPath）。</summary>
        public string CollectedPath { get; set; } = string.Empty;

        /// <summary>
        /// 本任务定稿时**搬出去的内容物文件数**（0 = 这一层只出了过程物 / 什么都没出）。
        ///
        /// <para>
        /// 为什么要单独记（2026-09-27，落点模型 v2）：续解层"该不该给这一层建一个包名目录"的判据里有一条
        /// "父任务自己产出了内容物吗"（出了就是分支，那一层照建）。这条事实**不能拿
        /// <see cref="ContentDirectoryPath"/> 代替** —— 后者是"内容物那一层在哪"，而只出过程物的那一层
        /// 也会被算成"落点目录本身"，实测因此把简洁档判成了忠实档（端到端测试当场红）。
        /// 唯一权威来源是定稿计划的 <c>ContentFileCount</c>（`StageCommitResult`）。
        /// </para>
        /// </summary>
        public int ContentFileCount { get; set; }

        /// <summary>
        /// 源包搬运（一键处理 / 手动「只解压」把整组源包移入 <c>其余物</c>）在**本任务**上的记账。
        ///
        /// <para>
        /// 两个用途，缺一不可（见 <see cref="SourcePackageMoveState"/> 的说明）：
        /// <b>幂等</b>（搬过就绝不再搬第二次，不靠"文件还在不在"猜）与
        /// <b>延期</b>（第一层只出过程物时，把搬运留到整条续解链跑完之后补做）。
        /// </para>
        ///
        /// 它**不参与任何界面显示**：只是流水线的记账字段（与 <see cref="OutputPath"/> /
        /// <see cref="CollectedPath"/> 同一类），所以刻意不做成通知属性。
        /// </summary>
        public SourcePackageMoveState SourcePackageMove { get; set; } = SourcePackageMoveState.NotAttempted;

        /// <summary>
        /// 本任务**这一轮定稿实际使用的「其余物」目录**（归集之后的位置）——链结束后补搬源包时的目标根。
        ///
        /// <para>
        /// 为什么要记下来而不是到时候重算：定稿计划算出来的其余物目录在三种情况下都算不出来 ——
        /// 内容物那一层正好也叫「其余物」（会退让成 <c>其余物(1)</c>）、共用输出根模式下按包名分的那一层、
        /// 以及结果归集把整个产物目录搬走之后的落点。重算出来的路径一旦不准，
        /// 源包就会被搬到一个用户找不到的地方（或者凭空多出一个 <c>其余物</c> 目录）。
        /// </para>
        /// </summary>
        public string RestDirectoryPath { get; set; } = string.Empty;

        /// <summary>
        /// 本任务定稿成功那一刻，**内容物实际落地的那一层目录**（2026-09-25 第 35 条新加）。
        ///
        /// <para>为什么需要它：<see cref="ParentOutputDirectory"/>（续解产物的落点）以前直接取父任务的
        /// <see cref="OutputPath"/>。在"添加文件夹 + 指定位置"那种**多个包共用同一个输出根**的批次里，
        /// 父任务的 OutputPath 是那个共用根（如 <c>BBB\111</c>），而它的内容物其实落在**包名那一层**
        /// （<c>BBB\111\2222\</c>，因为包里自带一个同名文件夹）—— 于是下一层的产物被放到了共用根上，
        /// 与包名目录平级：用户看到的正是「<c>BBB\111\222</c> 应该放进 <c>BBB\111\2222</c> 里面」。</para>
        ///
        /// <para>空 = 还没定稿过（或这一轮没成功定稿）；调用方要按 <see cref="OutputPath"/> 回落。</para>
        /// </summary>
        public string ContentDirectoryPath { get; set; } = string.Empty;

        /// <summary>
        /// 本任务**所属的输出根**，即把它解出来的那个父任务的最终输出目录。
        ///
        /// 为什么要有这个字段（用户诉求"一个源包 = 一个最终目录"）：
        /// 一键处理会续解内层包，旧的续解方式把内层包当成**新任务**，于是它按自己的路径算落点
        /// （<c>22569473.7z.001</c> → <c>&lt;id&gt;.7z\内容物</c>），源目录旁边就多出好几个平级目录 ——
        /// 用户的原话是"多弄了四个文件夹、分卷文件你居然又解压到外面来了、文件一多根本分不清"。
        ///
        /// 现在内层包带着父任务的落点走：<see cref="Services.PathService.BuildOutputPath"/> 见到它
        /// 就直接返回这个目录，**不再套一层**。链上所有任务的产物因此归到同一个目录里。
        ///
        /// 空 = 最外层源包（落点由设置与源包路径算出来）。
        /// </summary>
        public string ParentOutputDirectory { get; set; } = string.Empty;

        /// <summary>父任务的名字（只为日志与报告里说清"这个内层包属于谁"，不参与任何路径计算）。</summary>
        public string ParentTaskName { get; set; } = string.Empty;

        /// <summary>
        /// 这条续解链**根任务的身份**（= 根任务那一刻的规范化绝对路径）。
        ///
        /// <para><b>为什么必须按路径认链</b>（2026-09-30 真机）：链尾「删除操作」那一档原先拿
        /// <c>ParentOutputDirectory</c>（= "是不是续解任务"）当链成员判据，而调用方传进来的是
        /// **整批**的任务清单 —— 于是 A 目录那个 333 包的链尾，被 B 目录里同名的
        /// <c>111.part1.rar</c>（另一个包、另一个目录）的失败挡住：
        /// 日志原文「<c>333-Rar4.part1.rar：链尾的其余物不处理（链上的「111.part1.rar」没有成功</c>）」。
        /// 名字相同 ≠ 同一条链，判据只能是规范化后的绝对路径。</para>
        ///
        /// <para>空 = 自己就是根任务（链身份回落到它自己的 <see cref="CurrentPath"/>）。
        /// 赋值点只有一处：<c>OneClickCoordinator.AddInnerTasksAsync</c>。</para>
        /// </summary>
        public string RootSourcePath { get; set; } = string.Empty;

        /// <summary>
        /// 这条续解链的**链身份**（只用于比较）：带明确链身份就用它，否则回落到自己的路径。
        /// <para>链尾「删除操作」那一档按它判断"这个任务是不是本链成员" —— 判据只能是路径，
        /// ⛔ 不许用名字（2026-09-30 真机：另一个目录里同名的包把本链的链尾挡下了）。</para>
        /// </summary>
        public string ChainRootIdentity => string.IsNullOrWhiteSpace(RootSourcePath) ? CurrentPath : RootSourcePath;

        /// <summary>
        /// 本任务**这一次实际用的「本次选项」**（一键处理面板选的那一组：落点 / 终端落法 / 源包处理），
        /// 一句话形式，由 <c>OneClickRunOptions.Describe()</c> 给出；空 = 这一次没走过一键处理。
        ///
        /// <para>
        /// 为什么要在任务上留一份（规格 <c>docs/输出与整理模型.md</c> §9.2 硬要求⑥）：
        /// 用户要能回答"这次为什么解到这里"。日志里有一行，但日志会被后面的批次冲走；
        /// 任务上的这一份会跟着任务走到失败清单第二级与「复制任务信息」里，
        /// 事后回头看也能立刻知道当时用的是哪一档。
        /// </para>
        /// <para>
        /// 与 <see cref="OutputPath"/> 的分工：那个是"落到哪"（实际值，可能是 <c>xxx(1)</c>），
        /// 这个是"为什么落那儿"（选项依据）。两者都要有，缺一个都答不全。
        /// 它不参与任何界面绑定，所以刻意不做成通知属性（与 <see cref="SourcePackageMove"/> 同一类）。
        /// </para>
        /// </summary>
        public string RunOptionsNote { get; set; } = string.Empty;

        /// <summary>
        /// 解压前**那一遍 list** 里顺带统计出来的"可执行 / 脚本类条目"提示（设置项
        /// <c>ReportDangerousEntries</c>，默认开）。没有可疑条目时为空字符串。
        ///
        /// <para>
        /// <b>只提示，绝不阻断</b>（<c>docs/WinRAR功能参考.md</c> §2 F 组）：真实资源包里安装器 / 补丁
        /// 经常**就是**内容物，所以不做全局硬排除掩码；这条只是让用户知道"这个包里有 N 个可执行文件"。
        /// </para>
        /// <para>
        /// 为什么是一个独立字段而不是塞进 <see cref="EngineVerdict"/> 或 <see cref="VerifyMessage"/>：
        /// 那两处各有明确的语义（引擎对"能不能打开"的结论、输出校验的结论），
        /// 而且 <see cref="VerifyMessage"/> 会在收尾时被校验结论覆盖（提示会被静默吃掉）。
        /// 独立字段还能让统计它的那一遍 list 与显示它的地方各自演进，互不干扰。
        /// </para>
        /// </summary>
        public string DangerousEntriesWarning { get; set; } = string.Empty;

        /// <summary>
        /// 解压前的**路径长度**预警（<c>Security/PathLengthPreflight</c> 算出来的结构化结论）。
        ///
        /// <para>
        /// 为什么要落到任务上而不是只写一行日志：7-Zip 报的是英文错（<c>The filename or extension is too long</c>），
        /// 而且往往解到一半才报 —— 用户回头查"为什么少了几个文件"时，任务详情里得有这一句中文解释。
        /// </para>
        /// <para>
        /// 它是**预警不是拒绝**：路径过长时 7z 可能只解出一部分，但"要不要继续"仍由解压结果决定
        /// （部分成功会落到 <c>PartiallyCompleted</c>，不会显示成成功）。
        /// 与 <see cref="DangerousEntriesWarning"/> 一样，它取自**同一次** list，绝不为此再 list 一遍。
        /// </para>
        /// </summary>
        public string PathLengthWarning { get; set; } = string.Empty;

        /// <summary>
        /// **识别那一刻**的源文件快照（大小 + 修改时间 + 当时在不在），分卷组覆盖整组
        /// —— 不变量 11 的基准（AGENTS.md §6 第 11 条）。
        ///
        /// <para>
        /// 为什么挂在这里而不是一个 <c>Dictionary&lt;任务, 快照&gt;</c>：任务本身是长命的
        /// （识别 → 改名 → 解压可以隔很久、用户还可以中间去泡杯茶），而协调器上的字段是**批级**的；
        /// 挂错地方会让"换了一批之后基准丢了"，那时就只剩"没有基准就补拍一次"这条退路 ——
        /// 于是不变量 11 在最需要它的那条路上静默失效。
        /// </para>
        /// <para>
        /// <b>null = 没有基准</b>（老任务 / 测试直接 <c>new</c> 出来的任务 / 从没扫描过）：
        /// 这时**不拦任务**，而是在开工前补拍一次（见协调器的 <c>EnsureSourceSnapshot</c>）。
        /// 类型在 <c>Storage/</c>：Disk stat 归 Storage 层，Models 只放纯模型（AGENTS.md §4）。
        /// </para>
        /// </summary>
        public SourceFileSnapshot? SourceSnapshot { get; set; }

        /// <summary>
        /// 上面那份快照是什么时候拍的（<b>本地时间</b>，给人看 / 写日志用；快照内部按 UTC 存）。
        /// 为 null 表示从来没拍过。
        /// </summary>
        public DateTime? SourceSnapshotTime { get; set; }

        /// <summary>有没有可用的基准（没有就该在开工前补拍，而不是判"变了"）。</summary>
        public bool HasSourceSnapshot => SourceSnapshot != null;

        /// <summary>
        /// 本任务要记进快照的那一组路径：**主文件在前，分卷整组在后**（顺序即比对顺序）。
        ///
        /// 顺序稳定是刚需：比对是**按位**做的，这样"改名了 / 源包被搬进其余物了"
        /// 这种"路径变了、文件没变"的情形不会被误判成"源文件被换了" ——
        /// 路径不是不变量 11 要保护的东西，大小与修改时间才是。
        ///
        /// <para>⛔ <b>同一份文件只许出现一次</b>（2026-10-10 真机 EEEE）："1 卷组"那一档里
        /// <see cref="VolumePaths"/> 装的就是这一单自己的文件（归组服务在识别**之后**才写进去），
        /// 于是"按位比对"会看到**第 2 条是快照里没有的新文件** ⇒ 报「源文件已变化」把这一单拦下、
        /// 一次引擎都不调。运行期读数（debug-mcp 停在 `ExtractionCoordinator.cs:498`）：
        /// <c>CurrentPath == VolumePaths[0]</c>、<c>VolumePaths.Count == 1</c>、
        /// <c>SourceSnapshot.Files.Count == 1</c>、<c>comparison.Changes[0].IsVolume == true</c>、
        /// <c>PreviousLength == 0</c>、<c>PreviousLastWriteTimeUtc.Ticks == 0</c>（`before == null` 那一支）。
        /// 守门用例 <c>SourceSnapshotDedupTests</c>（2 条，含"真的多出另一个文件"的哨兵）。</para>
        /// </summary>
        public List<string> GetSnapshotPaths()
        {
            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(CurrentPath) && seen.Add(CurrentPath))
            {
                paths.Add(CurrentPath);
            }

            foreach (string volume in VolumePaths)
            {
                if (!string.IsNullOrWhiteSpace(volume) && seen.Add(volume))
                {
                    paths.Add(volume);
                }
            }

            return paths;
        }

        /// <summary>
        /// 现在就拍一份快照并挂在任务上（<b>只 stat，不读内容</b>）。
        ///
        /// 调用时机只有两个：识别完成之后（正常基准），以及开工前发现"没有基准"时补拍。
        /// </summary>
        public SourceFileSnapshot CaptureSourceSnapshot()
        {
            SourceFileSnapshot snapshot = SourceFileSnapshot.Capture(GetSnapshotPaths());
            SourceSnapshot = snapshot;
            SourceSnapshotTime = snapshot.CapturedAt.ToLocalTime();
            return snapshot;
        }

        /// <summary>
        /// 与当前磁盘状况比一遍；<b>没有基准时返回 <c>null</c></b>（调用方补拍，不许当成"变了"）。
        /// </summary>
        public SourceChangeResult? CompareWithSourceSnapshot() =>
            SourceSnapshot?.Compare(GetSnapshotPaths());

        /// <summary>
        /// 是不是"续解出来的内层包"（而不是用户直接给的源包）。
        ///
        /// 区分这两类很关键：内层包的源文件是**我们自己产出的过程物**（已经归到
        /// <c>过程物</c> 里），既不是用户的源包、也不该被当成"输出目录冲突"重新起一个目录。
        /// </summary>
        public bool IsContinuationTask => !string.IsNullOrWhiteSpace(ParentOutputDirectory);

        /// <summary>
        /// **同一分卷组的后续卷、整组已经由首卷那一单负责** —— 这一单不参与解压，也不参与链尾裁决。
        ///
        /// <para>用户 2026-10-01 真机（`AAA` 那批）定的口径：一个包的两卷是**两个任务**
        /// （扫描期按名字归组，而那一批名字被网盘改坏了，认不出来），改名之后整组由首卷那一单
        /// 从第一卷启动解一次，后续卷那一单落「已跳过」。</para>
        ///
        /// <para><b>为什么要单独记一位、而不是让消费方各自去推</b>：链尾那两道门槛读的都是"任务"这个集合，
        /// 而跟班任务**没有产物**（它压根没解压）—— 拿它去比"链上的输出校验"，必然得出
        /// "链里有一层校验没过"，于是整组的源包一个都不删（真机现象：源包全留在原地）。
        /// 判据只有写在产生它的那一刻才是事实，绕一圈去推必然推歪。</para>
        ///
        /// <para>每次开工都会重算（任务对象是复用的）：由
        /// <c>ExtractionCoordinator.SkipWhenAnotherTaskOwnsThisVolumeGroup</c> 一处写。</para>
        /// </summary>
        public bool IsVolumeGroupFollower { get; set; }

        /// <summary>
        /// 这一单**算不算"这一批要做的事"**（用户 2026-10-01 第三报：批末那句"跳过 4 个"让他以为还有活没干）。
        ///
        /// <para><b>为什么跟班卷不算</b>：同一分卷组的后续卷（<see cref="IsVolumeGroupFollower"/>）
        /// 不是"一件没做成的事" —— 它是**这一组的一部分**，已经由首卷那一单整组负责了；
        /// 把它算进批末的"跳过 / 部分完成"，用户看到的就是"还有 4 个没弄完"，
        /// 而实际上**那一批该做的全做完了**。用户原话：「这四个应该是要跳过的，我绝对没必要（提醒），
        /// 你这样会让用户觉得还有任务没弄完」。</para>
        ///
        /// <para>⛔ 只排除跟班卷这一档：用户在同名冲突框里选的"跳过"、部分完成、取消、失败
        /// 一律照旧计入（那些确实意味着"有东西没做成"）。</para>
        /// </summary>
        public bool CountsTowardBatchOutcome => !IsVolumeGroupFollower;

        /// <summary>
        /// 这一单按「部分完成」发布了多少个文件（<c>0</c> = 没发布）—— 用户 2026-10-02 那一档。
        ///
        /// <para><b>为什么记在任务上</b>：批末诊断要说清"这一单不是全丢，已经救回来多少"，
        /// 而那一刻发布早就做完了（判据只有写在产生它的那一刻才是事实）。
        /// 由 <c>ExtractionCoordinator.TryPublishPartialProductsAsync</c> 一处写；每次开工由它自己重置。</para>
        /// </summary>
        public int PartialPublishedCount { get; set; }

        /// <summary>
        /// 部分完成的发布落点（<c>&lt;目标&gt;\&lt;包名&gt;\部分完成</c>）；没发布时为空串。
        /// 与 <see cref="PartialPublishedCount"/> 同时写、同时清。
        /// </summary>
        public string PartialPublishDirectoryPath { get; set; } = string.Empty;

        /// <summary>
        /// 创建任务。
        /// </summary>
        public ArchiveTask()
        {
        }

        /// <summary>
        /// 根据文件路径创建任务。
        /// </summary>
        public ArchiveTask(string filePath, int index = 0)
        {
            Index = index;
            OriginalPath = filePath ?? string.Empty;
            CurrentPath = filePath ?? string.Empty;
            Status = StatusText.WaitingScan;
            Operation = StatusText.OpWaiting;
            ProgressText = "-";
            PasswordStatus = StatusText.NotChecked;
            ExtensionStatus = StatusText.NotChecked;
            DetectedFormat = "Unknown";
            LastUpdatedTime = DateTime.Now;

            // 单文件构造 = "用户选中的就是这个文件"（第 13 条四条规则的第 1 条）。
            // 「添加文件夹」扫出来的任务由 FileScanService 覆写成 Folder + 文件夹路径。
            SourceSelectionKind = SourceSelectionKind.File;
            SourceSelectionRoot = OriginalPath;

            RefreshPathRelatedProperties();
        }

        /// <summary>
        /// 标记任务开始。
        /// </summary>
        public void MarkStarted(string operation, string status)
        {
            Operation = operation;
            Status = status;
            ProgressText = StatusText.ProgressProcessing;
            ErrorMessage = string.Empty;
            StartTime = DateTime.Now;
            EndTime = null;

            // 重跑一个任务时，上一次留下的百分比与"长时间无响应"必须清干净，
            // 否则新的一轮会从"上一轮的 87%"开始显示。
            ClearProgress();

            LastUpdatedTime = DateTime.Now;
        }

        /// <summary>
        /// 标记任务成功。
        /// </summary>
        public void MarkSuccess(string operation, string status)
        {
            Operation = operation;
            Status = status;
            ProgressText = StatusText.ProgressCompleted;
            ErrorMessage = string.Empty;
            EndTime = DateTime.Now;
            LastUpdatedTime = DateTime.Now;
            ClearProgress();
            UpdateElapsedText();
        }

        /// <summary>
        /// 标记任务失败。
        /// </summary>
        public void MarkFailed(string operation, string status, string errorMessage)
        {
            Operation = operation;
            Status = status;
            ProgressText = StatusText.ProgressFailed;
            ErrorMessage = errorMessage ?? string.Empty;
            EndTime = DateTime.Now;
            LastUpdatedTime = DateTime.Now;
            ClearProgress();
            UpdateElapsedText();
        }

        /// <summary>
        /// 标记任务跳过。
        /// </summary>
        public void MarkSkipped(string reason)
        {
            Operation = StatusText.OpSkip;
            Status = StatusText.Skipped;
            ProgressText = StatusText.OpSkip;
            ErrorMessage = reason ?? string.Empty;
            EndTime = DateTime.Now;
            LastUpdatedTime = DateTime.Now;
            ClearProgress();
            UpdateElapsedText();
        }

        /// <summary>
        /// 标记任务取消。
        /// </summary>
        public void MarkCancelled(string reason = "用户取消")
        {
            Operation = StatusText.OpCancel;
            Status = StatusText.Cancelled;
            ProgressText = StatusText.Cancelled;
            ErrorMessage = reason;
            EndTime = DateTime.Now;
            LastUpdatedTime = DateTime.Now;
            ClearProgress();
            UpdateElapsedText();
        }

        /// <summary>
        /// 更新路径相关属性（并**顺手刷新源文件大小**）。
        /// </summary>
        public void RefreshPathRelatedProperties()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(CurrentPath))
                {
                    FileName = string.Empty;
                    DirectoryPath = string.Empty;
                    CurrentExtension = "无";
                    SourceSizeBytes = 0;
                    return;
                }

                FileName = Path.GetFileName(CurrentPath);
                DirectoryPath = Path.GetDirectoryName(CurrentPath) ?? string.Empty;

                string ext = Path.GetExtension(CurrentPath);
                CurrentExtension = string.IsNullOrWhiteSpace(ext) ? "无" : ext;
            }
            catch
            {
                FileName = CurrentPath;
                DirectoryPath = string.Empty;
                CurrentExtension = "无";
                SourceSizeBytes = 0;
            }

            RefreshSourceSize();

            // ⛔ 显示那几列是计算属性：这里不显式通知，改名之后列表会停在旧名字上（真机 2026-10-06 实测）。
            NotifyDisplayIdentityChanged();
        }

        /// <summary>
        /// 采集源文件大小（用户 2026-09-27："应该改在列表管理里面显示选中文件的大小，
        /// 就排在文件名后面，这样用户就能更好的察觉"）。
        ///
        /// <para>为什么挂在"路径相关属性"这一个出口上：任务创建、改名、重新扫描三条路都会调
        /// <see cref="RefreshPathRelatedProperties"/>，在那里采一次就不会出现"某条路忘了填、列表里空着"。
        /// 读不到（文件没了 / 权限）时如实记 0，⛔ 不抛、也不编一个数出来。</para>
        ///
        /// <para>⚠ 分卷组：任务只认**自己那一个文件**（第一卷），整组体积由空间估算那边另算，
        /// 这里不做加法 —— 列表里显示的就该是"这一行文件的大小"。</para>
        /// </summary>
        public void RefreshSourceSize()
        {
            long size = 0;

            try
            {
                if (!string.IsNullOrWhiteSpace(CurrentPath) && File.Exists(CurrentPath))
                {
                    size = new FileInfo(CurrentPath).Length;
                }
            }
            catch
            {
                size = 0;
            }

            SourceSizeBytes = size;
        }

        /// <summary>
        /// 应用识别结果到任务。
        /// </summary>
        public void ApplyDetectResult(DetectResult result, string extensionStatus)
        {
            if (result == null)
            {
                DetectedFormat = "Unknown";
                SuggestedExtension = string.Empty;
                IsArchive = false;
                IsEncrypted = false;
                EmbeddedArchiveOffset = 0;
                EmbeddedArchiveEnd = 0;
                EmbeddedDirectReadSupported = false;
                EmbeddedDirectReadReason = string.Empty;
                VolumeRenameSuggestion = string.Empty;
                SplitVolumeAssemblyNote = string.Empty;
                ExtensionStatus = StatusText.UnknownFormat;
                Status = StatusText.UnknownFormat;
                Operation = StatusText.OpScan;
                ProgressText = StatusText.ProgressCompleted;
                LastUpdatedTime = DateTime.Now;
                return;
            }

            DetectedFormat = result.Format;
            SuggestedExtension = result.SuggestedExtension;
            IsArchive = result.IsArchive;
            IsEncrypted = result.IsProbablyEncrypted;

            // 重扫时也要跟着刷新：上一次的偏移对新文件没有意义（识别结果是会被覆盖的）。
            EmbeddedArchiveOffset = result.EmbeddedArchiveOffset;
            EmbeddedArchiveEnd = result.EmbeddedArchiveEnd;
            EmbeddedDirectReadSupported = result.EmbeddedDirectReadSupported;
            EmbeddedDirectReadReason = result.EmbeddedDirectReadReason ?? string.Empty;

            // 改名建议同理：它是"上一轮那次解压判决"的产物，识别结果一刷新就必须作废。
            VolumeRenameSuggestion = string.Empty;
            SplitVolumeAssemblyNote = string.Empty;
            ExtensionStatus = extensionStatus;

            if (result.IsArchive)
            {
                Status = StatusText.Recognized;
            }
            else if (result.IsKnownFormat)
            {
                Status = StatusText.Recognized;
            }
            else
            {
                Status = StatusText.UnknownFormat;
            }

            Operation = StatusText.OpScan;
            ProgressText = StatusText.ProgressCompleted;
            ErrorMessage = result.Message ?? string.Empty;
            LastUpdatedTime = DateTime.Now;
        }

        /// <summary>
        /// 更新耗时文本。
        /// </summary>
        public void UpdateElapsedText()
        {
            if (StartTime == null)
            {
                ElapsedText = "-";
                return;
            }

            DateTime end = EndTime ?? DateTime.Now;
            TimeSpan span = end - StartTime.Value;

            if (span.TotalMilliseconds < 0)
            {
                ElapsedText = "-";
                return;
            }

            ElapsedText = span.ToString(@"hh\:mm\:ss");
        }

        protected bool SetProperty<T>(
            ref T storage,
            T value,
            [CallerMemberName] string? propertyName = null)
        {
            if (Equals(storage, value))
            {
                return false;
            }

            storage = value;
            OnPropertyChanged(propertyName);

            if (propertyName != nameof(LastUpdatedTime))
            {
                _lastUpdatedTime = DateTime.Now;
                OnPropertyChanged(nameof(LastUpdatedTime));
            }

            if (propertyName == nameof(Password))
            {
                OnPropertyChanged(nameof(HasTaskPassword));
            }

            if (propertyName == nameof(ErrorMessage))
            {
                OnPropertyChanged(nameof(HasError));
            }

            return true;
        }

        /// <summary>
        /// 属性变更通知 —— ⛔ **显示那几列在这里统一"顺带"通知**（用户 2026-10-07：
        /// 「一直都是界面的问题，我看着日志还好好的，你为什么没有同步」）。
        ///
        /// <para>为什么必须在这一处统一做：`DisplayFileName / DisplaySizeText / DisplayExtension /
        /// DisplayPath / StatusDisplayText / DisplayErrorMessage` 都是**计算属性**
        /// （读 `_userFacingFilePath` + 当前路径 + 状态 + 错误信息）—— 绑定只认 `PropertyChanged`，
        /// 而改动底层字段的地方很多（收场、批末补判、改名、重扫、清校验…）。靠"每个调用点记得叫一声"
        /// 一定会漏（实测就漏了：日志是现算的、永远最新，界面留着旧值）。
        /// ⇒ 判据放在这里：**只要动到这几列的输入，就把这几列一起通知**。
        /// ⛔ 通知列表里不许再出现触发项自己（否则自激）。</para>
        /// </summary>
        private static readonly string[] DisplayColumnTriggers =
        {
            nameof(Status), nameof(ErrorMessage), nameof(FileName), nameof(DirectoryPath),
            nameof(CurrentExtension), nameof(SourceSizeBytes), nameof(CurrentPath), nameof(Outcome)
        };

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

            if (DisplayColumnTriggers.Contains(propertyName, StringComparer.Ordinal))
            {
                // ⛔ 只发那几列（不递归、不发触发项自己）。
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayFileName)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplaySizeText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayExtension)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayPath)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayPathToolTip)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusDisplayText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayErrorMessage)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayTransformationText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayVolumeInfoText)));
            }
        }
    }
}