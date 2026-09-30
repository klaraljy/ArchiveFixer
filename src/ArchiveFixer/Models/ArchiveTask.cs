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
            set => SetProperty(ref _originalPath, value ?? string.Empty);
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
        public string StatusDisplayText
        {
            get
            {
                string text = Status;

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
        public void ApplyProgress(int percent, string? entry)
        {
            if (percent >= 0)
            {
                ProgressPercent = percent > 100 ? 100 : percent;
            }

            if (!string.IsNullOrWhiteSpace(entry))
            {
                ProgressEntry = entry.Trim();
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
        /// </summary>
        public List<string> GetSnapshotPaths()
        {
            var paths = new List<string>();

            if (!string.IsNullOrWhiteSpace(CurrentPath))
            {
                paths.Add(CurrentPath);
            }

            foreach (string volume in VolumePaths)
            {
                if (!string.IsNullOrWhiteSpace(volume))
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

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
