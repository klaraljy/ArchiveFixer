namespace ArchiveFixer.Models
{
    /// <summary>
    /// 任务状态、操作、后缀状态、密码状态等界面文本的统一定义。
    /// 所有状态字符串统一从这里引用，避免各处散落字面量不一致。
    /// </summary>
    public static class StatusText
    {
        // 进度文本
        public const string ProgressWaiting = "-";
        public const string ProgressProcessing = "处理中";
        public const string ProgressCompleted = "完成";
        public const string ProgressFailed = "失败";
        public const string ProgressSkipped = "跳过";
        public const string ProgressCancelled = "已取消";

        // 操作
        public const string OpWaiting = "等待";
        public const string OpScan = "扫描";
        public const string OpRename = "改名";
        public const string OpTest = "测试";
        public const string OpExtract = "解压";
        public const string OpSkip = "跳过";
        public const string OpCancel = "取消";
        public const string OpPrepare = "准备";

        // 任务状态
        public const string WaitingScan = "等待扫描";
        public const string Scanning = "扫描中";
        public const string Recognized = "已识别";
        public const string UnknownFormat = "格式未知";
        public const string RenameSuccess = "改名成功";
        public const string RenameFailed = "改名失败";
        public const string Renaming = "改名中";
        public const string WaitingRename = "等待改名";
        public const string TestPassed = "测试通过";
        public const string TestFailed = "测试失败";
        public const string Testing = "测试中";
        public const string WaitingTest = "等待测试";
        public const string ExtractSuccess = "解压成功";
        public const string ExtractFailed = "解压失败";
        public const string Extracting = "解压中";
        public const string WaitingExtract = "等待解压";
        public const string WrongPassword = "密码错误";
        public const string Corrupted = "文件损坏";
        public const string AccessDenied = "权限不足";
        public const string OutputConflict = "输出路径冲突";
        public const string VolumeMissing = "分卷缺失";
        public const string PathTooLong = "路径过长";
        public const string UnknownError = "未知错误";
        public const string SevenZipMissing = "7z不存在";

        /// <summary>
        /// 达到密码尝试上限（AGENTS.md §9.2：每层、每任务、每批次都要有尝试上限）。
        ///
        /// **不是"密码错误"**：候选密码根本还没试完就按硬上限停了，
        /// 包本身可能完全正常，只是正确密码排在候选表更靠后的位置。
        /// 两者混为一谈时，用户会去反复核对密码本，而真正该做的是补上密码或调大上限。
        /// </summary>
        public const string PasswordAttemptLimitReached = "达到密码尝试上限";

        public const string Cancelled = "已取消";
        public const string Skipped = "已跳过";
        public const string Overwritten = "已覆盖";
        public const string Success = "成功";
        public const string NotArchive = "非压缩包";

        /// <summary>
        /// 部分完成：解出来了一部分，但因为上限 / 密码 / 损坏停在中途。
        /// **不得当成成功**（AGENTS.md §6 第 6 条），单独一类用户才知道还要不要接着弄。
        /// </summary>
        public const string PartiallyCompleted = "部分完成";

        /// <summary>
        /// 归档**加密了文件名**（RAR <c>-hp</c> / 7z <c>-mhe</c>）：连条目名都读不出来，
        /// 所以"里面有什么、缺没缺"当前**无法判定**。
        ///
        /// 为什么必须单独一类：这种包以前会落到"密码错误 / 文件损坏"里，给用户的结论是**错的** ——
        /// 包可能完全正常，只是需要正确密码。反过来它也**不能算成功**（不变量 6）。
        ///
        /// 判定只在"列目录失败"时成立（见 <c>SevenZipOutputParser.LooksLikeEncryptedHeaders</c>）：
        /// 我们自己产出的内层 <c>-mhe</c> 分卷在给了正确密码时列目录是成功的，
        /// 所以正常可解的加密包不会被误判成这个状态。
        /// </summary>
        public const string EncryptedHeaders = "文件名已加密";

        // 后缀状态
        public const string NotChecked = "未检测";
        public const string ExtensionNormal = "后缀正常";
        public const string ExtensionMissing = "后缀缺失";
        public const string ExtensionMismatch = "后缀不匹配";
        public const string ExtensionMultiFake = "多重后缀疑似伪装";

        /// <summary>分卷文件的后缀：<c>xxx.7z.001</c>。它不是伪装，单独一类（设计.md §七）。</summary>
        public const string ExtensionVolume = "分卷后缀";

        /// <summary>
        /// 内嵌归档：文件本身不是压缩包，尾部却拼着一整个 ZIP（前面是视频等正常数据）。
        ///
        /// 后缀一栏给这一类，是为了让用户一眼看出"**不要改后缀**"：
        /// ZIP 的内部偏移相对它自己，而前置数据远超 7-Zip 的容忍上限（实测 8 MiB），
        /// 所以改成 <c>.zip</c> 之后 7z 仍然打不开，改名的唯一效果是让用户以为已经修好了。
        /// 真正要做的是按偏移把尾部那段取出来。
        /// </summary>
        public const string ExtensionEmbedded = "内嵌归档";

        // 密码状态
        public const string PasswordCorrect = "密码正确";
        public const string PasswordNotNeeded = "不需要密码";
        public const string PasswordNeed = "需要密码";

        // 改名预览状态
        public const string RenameReady = "可改名";
        public const string RenameCannot = "无法改名";
        public const string RenameWillSkip = "将跳过";
        public const string TargetExists = "目标已存在";
        public const string WillAutoRename = "将自动重命名";

        // ================================================================
        // 提示文案（**不是状态**）
        // ================================================================
        //
        // ⚠ 下面这一档是**提示文案**，不是 Status 值 —— 别按 AGENTS.md §7 的"三件套"照搬：
        //    · 它**不进状态机**：没有任何代码拿它跟 task.Status 比；
        //    · 它**不参与配色**：StatusToBrushConverter 一行都不用改；
        //    · 它**不参与统计**：TaskSummaryService 的分桶（成功/失败/跳过…）与它无关。
        //    新增一条真正的**状态**（例如"疑似卡住"）才需要同时改那三处；
        //    这里只是"把一句话放进唯一的文案来源里"，免得又散落回中文字面量。

        /// <summary>
        /// 引擎很久没有任何输出时的提示语（默认阈值 90 秒，见
        /// <c>EngineOutputActivityMonitor.DefaultStallThreshold</c>）。
        ///
        /// <para>
        /// 它对应的是用户反复抱怨的"卡死"：界面只有"处理中/完成"两态时，长时间零输出看起来就是死了。
        /// 落在任务上时**只提示、绝不改结论** —— 任务的成败仍由引擎退出码与错误分类决定，
        /// 也不杀进程（要不要中止由用户点「取消当前」决定，不变量 9 / 6）。
        /// 有新的进度或引擎输出时，协调器会把它清回空串。
        /// </para>
        ///
        /// <para>
        /// 它原来临时住在 <c>ArchiveTask.NoResponseHintText</c>（上一批"不许改 StatusText"授权下的权宜之计），
        /// 2026-09-22 归位到这里：<c>ArchiveTask</c> 只保留字段本身。
        /// </para>
        /// </summary>
        public const string LongTimeNoResponse = "长时间无响应";

        /// <summary>
        /// 任务列表上方那条蓝色提示的正文：**界面与命令提示共用的唯一一句话**。
        ///
        /// <para>
        /// 为什么必须共用：2026-09-22 真机验收抓到的缺陷是"提示说一套、代码做一套" ——
        /// 蓝字写着「移除选中」等命令只作用于**勾选**，而清理类命令（删除其余物 / 清理空文件夹）
        /// 实际看的是 DataGrid 的**当前行**（<c>MainViewModel.SelectedTask</c>）：
        /// 用户明明勾了任务，点「删除其余物…」却收到"请先在列表里选中一个任务"，
        /// 只能靠手动点一下那一行才work。把这句话放进唯一的文案来源，XAML 用
        /// <c>{x:Static}</c> 引用、命令提示由 <see cref="PickTaskPromptFormat"/> 构造，
        /// 两边就不可能再各写一套。
        /// </para>
        /// <para>
        /// 作用域口径（与 <c>MainViewModel.ResolveCleanupTargets</c> 的实际行为一一对应）：
        /// 勾选为准 → 一个都没勾时退化为当前行 → 两者都没有才提示。
        /// </para>
        /// </summary>
        public const string SelectionScopeHint =
            "「一键处理 / 智能修正 / 移除选中 / 删除其余物 / 清理空文件夹」以勾选为准（最左侧一列）；" +
            "一个都没勾时按当前点中的那一行办；两者都没有会提示你先选一个。" +
            "右键菜单只作用于当前这一行；「清空列表」是整表操作（与勾选无关）。";

        /// <summary>
        /// "两者都没有"时的提示模板（<c>{0}</c> = 命令名，例如「删除其余物」）。
        ///
        /// 措辞必须与 <see cref="SelectionScopeHint"/> 同一套词（"勾选" / "点中"），
        /// 否则用户会以为勾选没用 —— 那正是这次要修的缺陷。
        /// </summary>
        public const string PickTaskPromptFormat = "请先勾选或点中一个任务，再执行「{0}」。";
    }
}
