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
        /// **没有可用的解压引擎**（7-Zip 与 UnRAR 一个都没找到）。
        ///
        /// <para>
        /// 为什么要与 <see cref="SevenZipMissing"/>（「7z不存在」）分开：后者的判据是"某个具体引擎/文件
        /// 不在"，而这一条是"<b>任一引擎都不可用</b>"（<c>EngineRouter.IsAvailable</c>）。
        /// 默认优先级是 UnRAR → 7-Zip，用户完全可能只是缺 UnRAR、或者把内置件关掉了 ——
        /// 这时给他看「7z不存在」是**指错方向**：他会去修一个本来没问题的 7z 目录，
        /// 而真正该做的是"两者任装其一"。
        /// </para>
        /// <para>
        /// 失败类状态：配色与统计必须与既有失败口径一致（见 <c>StatusToBrushConverter</c> 的错误色、
        /// <c>TaskSummaryService</c> 的"解压失败"桶）——按 AGENTS.md §7 三处同改。
        /// </para>
        /// </summary>
        public const string NoEngineAvailable = "没有可用的解压引擎";

        /// <summary>
        /// 达到密码尝试上限（AGENTS.md §9.2：每层、每任务、每批次都要有尝试上限）。
        ///
        /// **不是"密码错误"**：候选密码根本还没试完就按硬上限停了，
        /// 包本身可能完全正常，只是正确密码排在候选表更靠后的位置。
        /// 两者混为一谈时，用户会去反复核对密码本，而真正该做的是补上密码或调大上限。
        /// </summary>
        public const string PasswordAttemptLimitReached = "达到密码尝试上限";

        /// <summary>
        /// **磁盘空间不足**（用户 2026-09-22 需求第 1 条：空间不够就**不启动**该任务）。
        ///
        /// <para>
        /// 它与"解压失败"分开的理由：解压失败是"这个包有问题"，而它连**开始都没开始** ——
        /// 用户要做的事完全不同（清空间 / 换盘 / 用危险模式），把两者混在一起，
        /// 用户会先去怀疑包坏了。<c>ErrorMessage</c> 里一定带具体数字（需要 X、可用 Y、差 Z）与建议动作。
        /// </para>
        /// <para>
        /// 失败类状态：配色与统计必须与既有失败口径一致（见 <c>StatusToBrushConverter</c> 的错误色、
        /// <c>TaskSummaryService</c> 的"解压失败"桶）——按 AGENTS.md §7 三处同改。
        /// </para>
        /// </summary>
        public const string DiskSpaceInsufficient = "磁盘空间不足";

        /// <summary>
        /// **源文件已变化**（AGENTS.md §6 不变量 11：源文件变化后不得继续使用旧识别结果）。
        ///
        /// <para>
        /// 识别（扫描）那一刻记下的大小 / 修改时间，在**真正调引擎之前**对不上了：
        /// 文件被改过、被换过、被删了，或者分卷组里动了任意一卷。这时手上那份识别结果
        /// **已经不是这个文件的结果**，继续解压等于拿旧结论处理新文件 ——
        /// 结局通常是"密码错误 / 文件损坏"这种**指错方向**的结论，用户会去反复核对密码本。
        /// </para>
        /// <para>
        /// 它与"解压失败"分开的理由：解压失败是"这个包有问题"，而它连**开始都没开始** ——
        /// 引擎一次都没被调用、源包原地不动、<c>其余物</c> 不生成、没有任何中间品。
        /// <c>ErrorMessage</c> 里一定点名"哪一个文件、哪一项变了"并给出一条出路
        /// （右键 →「重新扫描此文件」）。
        /// </para>
        /// <para>
        /// 失败类状态：配色与统计必须与既有失败口径一致（见 <c>StatusToBrushConverter</c> 的错误色、
        /// <c>TaskSummaryService</c> 的"解压失败"桶、<c>OneClickCoordinator.IsFailureStatus</c> 的
        /// 汇总分项）——按 AGENTS.md §7 同改。
        /// </para>
        /// </summary>
        public const string SourceChanged = "源文件已变化";

        /// <summary>差异里"大小变了"那一项（<c>{0}</c> = 当时，<c>{1}</c> = 现在）。</summary>
        public const string SourceChangeSizeFormat = "大小 {0} → {1}";

        /// <summary>差异里"修改时间变了"那一项（<c>{0}</c> = 当时，<c>{1}</c> = 现在）。</summary>
        public const string SourceChangeTimeFormat = "修改时间 {0} → {1}";

        /// <summary>差异里"文件不见了"那一项（文件都没了，不再说"大小也变了"）。</summary>
        public const string SourceChangeMissingText = "文件不见了";

        /// <summary>分卷的"文件不见了"：**先点名这是分卷**，免得用户以为整个包没了。</summary>
        public const string SourceChangeVolumeMissingText = "分卷 · 文件不见了";

        /// <summary>分卷的其它差异前缀（大小 / 修改时间）：报出**是哪个卷**变了。</summary>
        public const string SourceChangeVolumePrefix = "分卷 · ";

        /// <summary>时间显示格式：写清到秒（只写 <c>12:03</c> 时，同一天改了两次看不出先后）。</summary>
        public const string SourceChangeTimeDisplayFormat = "yyyy-MM-dd HH:mm:ss";

        /// <summary>拿不到时间（快照里那一项本来就不存在）时的占位文案。</summary>
        public const string SourceChangeUnknownTimeText = "（没有记录）";

        /// <summary>多项差异之间的分隔符。</summary>
        public const string SourceChangeItemSeparator = "；";

        /// <summary>
        /// 结论那句话的模板（<c>{0}</c> = 逐个文件的差异清单）。
        /// 与「分卷缺失」同一口径：说清"是什么"之后紧跟"怎么办"。
        /// </summary>
        public const string SourceChangeMessageFormat =
            "源文件已变化：{0}。识别结果作废，本次没有开始解压（引擎没有被调用，源包一个字节都没动）。"
            + "请右键该任务 →「重新扫描此文件」重新识别后再处理。";

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
        /// 实际看的是 DataGrid 的**当前行**（<c>MainViewModel.SelectedTask</c>）。
        /// 把这句话放进唯一的文案来源，XAML 用 <c>{x:Static}</c> 引用、命令提示由
        /// <see cref="NoCheckedTaskPromptFormat"/> 构造，两边就不可能再各写一套。
        /// </para>
        /// <para>
        /// 作用域口径（与 <c>MainViewModel.ResolveCleanupTargets</c> 的实际行为一一对应）：
        /// <b>一律只认勾选</b> —— 一个都没勾就什么都不做，只提示先勾选。
        /// ⛔ 用户 2026-09-24 第 12 条明确否掉了"一个都没勾时退回当前点中的那一行"这条兜底
        /// （原话："你只需要操作我选中的文件，其他的不用管"），**不要再加回来**。
        /// </para>
        /// </summary>
        public const string SelectionScopeHint =
            "「一键处理 / 只解压 / 智能修正 / 移除选中 / 删除其余物 / 清理空文件夹」**一律只认勾选**（最左侧一列）；" +
            "一个都没勾就什么都不做，只提示你先勾选。" +
            "右键菜单只作用于当前这一行；「清空列表」是整表操作（与勾选无关）。";

        /// <summary>
        /// "一个都没勾"时的唯一提示模板（<c>{0}</c> = 命令名，<c>{1}</c> = 列表里有多少个任务）。
        ///
        /// <para>用户 2026-09-24 第 12 条亲自定的口径：**只提示、什么都不做**。整句以
        /// "没有勾选任何任务"开头（用户原话里的说法），后半句说清两件事 ——
        /// 这些命令只处理勾选的、以及怎么勾。用它替代原来的 <c>PickTaskPromptFormat</c>
        /// （那句写着"请先勾选或点中一个任务"，教的正是已经被否掉的兜底）。</para>
        ///
        /// <para>用 <c>static readonly</c>（而不是 <c>const</c>）只因为拼了
        /// <see cref="Environment.NewLine"/>；它仍然只在这一个地方定义。</para>
        /// </summary>
        public static readonly string NoCheckedTaskPromptFormat =
            "没有勾选任何任务，已取消「{0}」（一个文件都没动）。" + Environment.NewLine + Environment.NewLine +
            "列表里有 {1} 个任务，当前一个都没勾。这个命令**只处理你勾选的任务**：" +
            "在最左侧一列勾上要处理的那些，再点一次（在列表上按 Ctrl+A 可以全选）。";

        /// <summary>配套的日志行（提示框与日志说同一件事，事后排查不会对不上）。</summary>
        public const string NoCheckedTaskLogFormat =
            "没有勾选任何任务：本次一个任务都不处理（列表里没有勾选项，已取消）。";

        /// <summary>列表本来就是空的时候的那句提示（与"没勾选"是两回事，别混）。</summary>
        public const string TaskListEmptyPrompt =
            "任务列表是空的。先点「添加文件 / 添加文件夹」把要处理的包加进来。";

        // ================================================================
        // 导入语义：替换 vs 追加（用户 2026-09-24 第 12 条补拍）
        // ================================================================
        //
        // ⚠ 这一组是**界面提示的唯一来源**：菜单项 ToolTip 用 {x:Static} 引用、
        //    导入日志由 ScanCoordinator 构造。**必须写清"要往现有列表里加，
        //    用『追加到列表』"** —— 这是用户拍板"添加 = 清空重来"时明确要求写进界面的那句话。

        /// <summary>「添加文件 / 添加文件夹」的界面提示（ToolTip + 日志共用）。</summary>
        public const string AddReplacesListHint =
            "添加 = 先**清空整张任务列表**再加入你这次选的内容（替换语义）。" +
            "要往现有列表里加，用「文件 → 追加到列表」。";

        /// <summary>「追加到列表」的界面提示。</summary>
        public const string AppendToListHint =
            "追加 = 保留列表里现有的任务，只把你这次选的内容加到末尾（与「添加」的替换语义相反）。";

        /// <summary>
        /// 导入日志：**替换**语义（<c>{0}</c> = "，已清掉原有 N 个任务"或空串）。
        /// 日志里也必须说清这次用的是哪一种语义 —— 用户事后要能回答"我上一批怎么不见了"。
        /// </summary>
        public const string ImportReplaceLogFormat =
            "开始导入路径（**替换**语义：先清空整张表再添加{0}）。要往现有列表里加，用「文件 → 追加到列表」。";

        /// <summary>导入日志：**追加**语义。</summary>
        public const string ImportAppendLogFormat =
            "开始导入路径（**追加**语义：保留列表里现有的任务，新任务加到末尾）。";

        /// <summary>导入日志：清掉了多少个旧任务（拼在 <see cref="ImportReplaceLogFormat"/> 里）。</summary>
        public const string ImportReplacedTasksTextFormat = "，已清掉原有 {0} 个任务";

        /// <summary>导入日志：新增了多少个任务。</summary>
        public const string ImportFinishedLogFormat = "导入完成，新增任务 {0} 个。";

        // ================================================================
        // 危险模式（红按钮）：文案与风险描述
        // ================================================================
        //
        // ⚠ 这一组是**界面文案的唯一来源**：确认框正文、②「解压方式」页高风险区的开关说明、
        //    自测通过后的提示都引用它，`docs/使用说明.md` 的「空间不够怎么办」一节照抄这份措辞。
        //    以前那种"确认框写一套、设置里写一套、文档里再写一套"的形态，改一处漏两处，
        //    而这里漏掉的每一句都是**用户拿不可逆操作换来的知情权**。

        /// <summary>这个模式在界面上的名字（按钮 / 开关 / 日志里都用它）。</summary>
        public const string DangerModeName = "危险模式 · 边解边彻底删其余物";

        /// <summary>
        /// 危险模式开着时，①任务页顶部那**一行小白字**（用户 2026-09-24 第 11 条：
        /// "这个危险操作这个红框多么多余啊，别放在主界面"）。
        ///
        /// <para>
        /// 红横幅搬去了②解压方式页的「高风险区」，主界面只留这一行的理由：
        /// **不可逆的档位必须一直看得见**（AGENTS.md §9.6 的红线），
        /// 但不必占掉半个屏幕。四句话的完整版仍然写在②页。
        /// </para>
        /// </summary>
        public const string DangerModeActiveOneLineHint =
            "⚠ 危险模式已开启：任务成功后它的源包与中间件会被永久删除（详情与关闭入口在「解压方式」页底部）。";

        /// <summary>确认框里"这是什么"的那一段。</summary>
        public const string DangerModeSummary =
            "并行解压，每个任务在「内容物已定稿并按落点策略排好 + 输出校验通过 + 未取消」之后，" +
            "立刻把它自己的「其余物」（源包 + 中间件）彻底删除（不进回收站），于是净占用基本不变 —— " +
            "这就是它能解决「空间不够」的原因。";

        /// <summary>
        /// 风险四条（用户 2026-09-22 逐条要求写清）。顺序就是他给的顺序，**不要重排**。
        /// </summary>
        public static readonly string[] DangerModeRiskLines =
        {
            "① 源包会被永久删除（不进回收站、无法还原，只能重新下载）。",

            "② 删除发生在「内容物已排好且校验通过」之后，但校验不等于你确认过内容 —— "
            + "程序只保证该解出来的都解出来了，不保证内容就是你想要的那一份。",

            "③ 中途断电 / 蓝屏 / 程序被强杀时，可能停在「源包已删、内容物未完成」的状态："
            + "那时这一份只能重新下载。",

            "④ 只在你确实没有空间时才用它；正常情况请用默认档（其余物移入回收站，可还原）。"
        };

        /// <summary>风险四条连成一段（确认框正文用）。</summary>
        public static readonly string DangerModeRisks = string.Join(Environment.NewLine, DangerModeRiskLines);

        /// <summary>
        /// 自测协议（用户原话：「拿几个文件先测试一遍，比如并行解压是五个，你就要拿十个文件做这样的测试，
        /// 测试成功才能告知用户可以一试，但风险还是有的」）。
        /// </summary>
        public const string DangerModeSelfTestProtocolText =
            "开启之前会先跑一次自测：拿「并发数 × 2」个文件（并发 5 → 10 个文件）真的跑一遍这个模式，" +
            "逐个检查「解压成功 + 输出校验通过 + 其余物按预期被彻底删除 + 空间曲线符合预期」。";

        /// <summary>自测自己也是不可逆的：它删的就是那几个文件的源包 —— 必须提前说清。</summary>
        public const string DangerModeSelfTestWarning =
            "⚠ 自测本身就会永久删除这几个文件的源包（这正是要验证的动作），不可撤销。" +
            "自测会从需求最小的文件开始挑样本。";

        /// <summary>自测通过之后才说这句话（用户原话的落点）。</summary>
        public const string DangerModeCanTry =
            "自测通过：可以一试，但风险还是有的 —— 源包会被永久删除，无法还原。";

        /// <summary>没有自测凭证时不许开启。</summary>
        public const string DangerModeNeedsSelfTest =
            "危险模式必须先通过一次自测才能开启（在设置里点「跑自测并开启…」）。" +
            "跳过自测就等于跳过了唯一一次「先拿几个文件试试」的机会。";

        /// <summary>
        /// 自测凭证**盖不住当前并发档**时的说明（凭证是并发 N 的，现在调到 M &gt; N）。
        ///
        /// <para>为什么要有这一条：协议要求"拿并发数 × 2 个文件真跑一遍"，
        /// 跑出来的结论只对它跑过的那一档成立。档位调高之后再拿旧凭证开这个模式，
        /// 等于**没测过就用了**，而这条路上的代价是不可逆的（源包永久删除）。
        /// 所以档位超出凭证覆盖范围时**本批不生效**（一个字节都不删），并在这里说清两条出路。</para>
        /// </summary>
        public const string DangerModeNotCoveredBySelfTest =
            "危险模式当前不生效：自测凭证只覆盖它跑过的那一档并发。" +
            "要么把「最大并发解压数」调回不超过凭证里的档位（本批立刻恢复生效），" +
            "要么在新档位下重新跑一次自测（会再永久删除一批样本源包，不可撤销）。";

        // ================================================================
        // 解压前的提醒（无用物 / 无可用密码）—— 用户 2026-09-22 需求第 8 条
        // ================================================================
        //
        // ⚠ 这一组是**弹窗正文 + 日志行 + 文档**的唯一措辞来源，而且三段话的顺序不能改：
        //    ① 这些文件是什么（打包者附带的说明 / 网址 / 工具 / 广告）→
        //    ② 本程序不会动它们（不删、不改名、不搬走）→
        //    ③ 解压完成后由用户自己判断要不要删。
        //    漏掉 ② 就会出现"程序要去删我的文件"这种与事实相反的暗示；
        //    漏掉 ③ 用户会以为必须当场做决定。

        /// <summary>提醒框的标题。</summary>
        public const string JunkReminderTitle = "解压前的提醒";

        /// <summary>主按钮：照常处理这一批。</summary>
        public const string JunkReminderYesText = "继续处理";

        /// <summary>次按钮：先不处理（回到列表，任务与源包一个字节都不会动）。</summary>
        public const string JunkReminderNoText = "先不处理";

        /// <summary>
        /// 可选项位：本次运行内不再弹这个提醒。
        /// **只存在内存里的一个标记**（<c>ExtractionCoordinator</c> 的实例字段），
        /// 绝不写进设置文件 —— 用户勾的是"这次别再烦我"，不是"以后永远别提醒"。
        /// </summary>
        public const string JunkReminderOptionText = "本次运行不再提示这类提醒";

        /// <summary>开场一句：先把性质说清（不是错误），免得用户以为出事了。</summary>
        public const string JunkReminderIntro = "这次解压动手之前，先说两件事（都不是错误，也不影响解压本身）：";

        /// <summary>① 段标题：源目录里那些"可能是打包者附带的文件"。</summary>
        public const string JunkReminderJunkHeader =
            "① 这些源目录里有一些文件，很可能是打包者附带的说明 / 网址 / 工具 / 广告之类的诱饵"
            + "（本程序只按文件名 + 魔数判了个大概，**不保证**它们真的没用）：";

        /// <summary>① 段结尾：**程序不会动它们** + 解压完由用户自己判断（用户点名要写清的两件事）。</summary>
        public const string JunkReminderJunkFooter =
            "上面这些文件本程序**一个都不会动**（不删、不改名、不搬走）；"
            + "解压完成后你可以自己看一眼，再决定要不要删。";

        /// <summary>① 段：这次一共认出多少个（列出来的最多 10 条，其余只报个数）。</summary>
        public const string JunkReminderJunkCountFormat = "这次一共认出 {0} 个：";

        /// <summary>无用物还有多少个没列出来（上限见 <c>SourceJunkScanner.MaxReportedItems</c>）。</summary>
        public const string JunkReminderJunkMoreFormat = "  …还有 {0} 个（无用物最多列 10 条）";

        /// <summary>撞到扫描上限（每个目录 2000 个文件 / 魔数体检预算）时如实说明，不假装扫全了。</summary>
        public const string JunkReminderTruncatedNote = "  （源目录里的文件太多，本次只核对了前一部分）";

        /// <summary>② 段标题：需要密码、但当前一个可用候选都没有的包。</summary>
        public const string JunkReminderPasswordHeaderFormat =
            "② 本批有 {0} 个包需要密码，但当前**一个可用候选都没有**"
            + "（密码本为空 / 映射式没命中 / 没设统一密码）：";

        /// <summary>② 段：说清继续会发生什么（"即使密码本已预加载也可能没覆盖到"的那种包）。</summary>
        public const string JunkReminderPasswordOutcome =
            "它们大概率会以「密码错误」或「达到密码尝试上限」结束 —— 既不是成功，也不是文件损坏。";

        /// <summary>② 段：没列出来的包还有多少个（列表最长 10 个）。</summary>
        public const string JunkReminderPasswordMoreFormat = "  …还有 {0} 个（最多列 10 个）";

        /// <summary>② 段：给出口（两个按钮各自会怎样 + 去哪儿补密码）。</summary>
        public const string JunkReminderPasswordExit =
            "两条路：① 点「" + JunkReminderNoText + "」回到列表，把它们从勾选里去掉（或者先补好密码）再重跑；"
            + "② 点「" + JunkReminderYesText + "」照常开始 —— 程序紧接着还会问你要不要手动输一个密码，"
            + "也可以先去菜单「导入密码本…」把它补进去。";

        /// <summary>批首那条日志的骨架：**两段结论都落进日志**（数量 + 前几条名字）。</summary>
        public const string JunkReminderLogFormat = "解压前提醒：① 无用物 {0}；② 无可用密码的包 {1}。";

        /// <summary>某一段"有 N 个，例如 …"的写法（喂给 <see cref="JunkReminderLogFormat"/>）。</summary>
        public const string JunkReminderLogFoundFormat = "{0} 个（例如 {1}）";

        /// <summary>某一段一个都没有。</summary>
        public const string JunkReminderLogNoneText = "无";

        /// <summary>
        /// 无界面宿主（单测 / 控制台宿主）时的那条日志。
        ///
        /// <para>它解释的是"为什么没人点过、这一批却照常跑了"：这条提醒的降级方向是
        /// **继续**（见 <c>DialogService.ShowReminderConfirm</c>），与破坏性确认的降级方向
        /// （false = 取消）正好相反 —— 否则所有无界面管线都会被这条纯提示拦住。</para>
        /// </summary>
        public const string JunkReminderNoHostLog =
            "当前宿主没有界面：解压前的提醒不弹窗、按「" + JunkReminderYesText + "」放行（只写日志）。";

        /// <summary>用户勾了「本次运行不再提示」。</summary>
        public const string JunkReminderOptionLog =
            "已记下「" + JunkReminderOptionText + "」：本次运行内不再弹这个提醒（只在内存里，不写设置文件）。";

        /// <summary>用户在提醒里选了「先不处理」：这一批没有开始。</summary>
        public const string JunkReminderDeclinedLog =
            "已按「" + JunkReminderNoText + "」处理：这一批没有开始（任务、源包、输出目录一个字节都没动）。";

        // ================================================================
        // 一键处理的**那一个**确认框（用户 2026-09-24 第 17 条）
        // ================================================================
        //
        // 用户原话："现在规定点击完一键处理，就只能有一个弹窗提醒，而且这个可以选中以后不弹出……
        // 弹窗里面的东西改为『内容物会生成在什么地方，而且其余物是否自动删除』……
        // 为什么我一键处理还没有开始确认，你的进度条就开始动了"
        //
        // ⚠ 这一组是那个框的**唯一措辞来源**（AGENTS.md §7）。三条纪律：
        //    ① 正文只回答他点名的那两件事（内容物落点 / 其余物是否自动删除），其余进折叠区；
        //    ② 「以后不再询问」是**落盘的**设置（SkipOneClickConfirm），界面上留开关能再打开；
        //    ③ 无用物与"没有可用密码"两段**并进这一个框**（不再弹第二个），措辞仍引用
        //       上面 JunkReminder* 那一组里的事实，不另写一套判据。

        /// <summary>确认框标题（← 旧面板叫「一键处理 · 本次选项」）。</summary>
        public const string OneClickConfirmTitle = "一键处理 · 确认";

        /// <summary>第一件事：内容物会生成在什么地方。</summary>
        public const string OneClickConfirmDestinationLabel = "内容物会生成在：";

        /// <summary>第二件事：其余物。</summary>
        public const string OneClickConfirmRestLabel = "其余物：";

        /// <summary>其余物这一档：危险模式已开 → 解压成功后自动彻底删掉。</summary>
        public const string OneClickConfirmRestAutoDelete = "解压成功后自动彻底删除（危险模式已开启）";

        /// <summary>其余物这一档：默认 → 留在输出目录的「其余物」里，不自动删。</summary>
        public const string OneClickConfirmRestKeep = "不自动删除，留在输出目录的「其余物」里";

        /// <summary>第三行（短）：源包怎么处理。</summary>
        public const string OneClickConfirmSourceLabel = "源包：";

        /// <summary>可选项位：以后不再询问。</summary>
        public const string OneClickConfirmSuppressText = "以后不再询问，按当前设置直接开始（可在「解压方式」页再打开）";

        /// <summary>折叠区标题：要改就展开（默认收起 —— 用户嫌旧面板啰嗦）。</summary>
        public const string OneClickConfirmExpanderHeader = "本次改一下（落点 / 终端落法 / 源包处理）";

        /// <summary>多包时那一行的后缀：每个包各建一个同名子文件夹。</summary>
        public const string OneClickConfirmMultiPerArchiveFormat = "（本批 {0} 个包，每个包各建一个同名子文件夹）";

        /// <summary>多包时那一行的后缀：所有包都落进同一层。</summary>
        public const string OneClickConfirmMultiSharedFormat = "（本批 {0} 个包都落在这里）";

        /// <summary>落点算不出来时的那一行（**不许**静默显示一个假路径）。</summary>
        public const string OneClickConfirmDestinationUnknownFormat = "暂时算不出来：{0}";

        /// <summary>B 段（没有可用密码）并进确认框的那一行。</summary>
        public const string OneClickConfirmNoPasswordFormat =
            "注意：本批有 {0} 个包没有可用密码（例如 {1}）—— 继续大概率以「密码错误」或「达到密码尝试上限」结束。";

        /// <summary>A 段（疑似无用物）并进确认框的那一行。</summary>
        public const string OneClickConfirmJunkFormat =
            "另外：源目录里认出 {0} 个疑似无用物（例如 {1}），本程序一个都不会动它们。";

        /// <summary>勾了「以后不再询问」并写进设置之后的日志。</summary>
        public const string OneClickConfirmSuppressSavedLog =
            "已记下「以后不再询问」：一键处理的确认框不再弹（可在「解压方式」页把开关打开）。";

        /// <summary>这一批的提醒已经并进确认框（解压协调器只写日志、不再弹第二个框）。</summary>
        public const string BatchReminderMergedLog =
            "解压前的提醒已并入一键处理的确认框（用户 2026-09-24 第 17 条：一键处理只允许一个弹窗），这里只写日志。";

        /// <summary>确认框里的两个按钮（与旧面板同一口径的措辞）。</summary>
        public const string OneClickConfirmStartText = "开始处理";

        public const string OneClickConfirmCancelText = "取消";

        // ================================================================
        // 工作区残留（用户 2026-09-24 第 22 条）
        // ================================================================
        //
        // 起因：用户实测在 <程序目录>\data\work 下攒了 10 个目录 / 5.7 GB，问"为什么失败后你会留下这个残留"。
        // 答案有两半，两半都要写进界面文案：
        //   ① **保留是故意的**：失败 / 取消时工作区里是用户唯一的一份产物线索（不变量 12/13），
        //      自动删掉是不可逆的错误；
        //   ② **但以前只写一行日志**（没体积、没入口），等于没告诉用户 —— 这一组文案把它补成"看得见、点得动"。

        /// <summary>启动 / 刷新时那条日志（几个 + 共多大 + 在哪）。</summary>
        public const string WorkspaceLeftoverLogFormat = "发现 {0} 个工作区残留（上次失败 / 取消留下的，共 {1}），位置：{2}";

        /// <summary>紧跟其后的一句：为什么不自动删、去哪儿清。</summary>
        public const string WorkspaceLeftoverHintLog =
            "程序**不会自动删**它们（里面可能是那批唯一解出来的一份产物）；要清就在「清理与删除」页点「清理工作区」，删前会再确认一次。";

        /// <summary>界面上那一行（常驻提示；没有残留时整行不显示）。</summary>
        public const string WorkspaceLeftoverBannerFormat = "工作区残留：{0} 个目录，共 {1}";

        /// <summary>清理确认框的标题。</summary>
        public const string WorkspaceCleanupConfirmTitle = "清理工作区";

        /// <summary>确认框正文（几个 + 共多大）。</summary>
        public const string WorkspaceCleanupConfirmFormat = "要删掉这 {0} 个工作区目录（共 {1}）吗？";

        /// <summary>确认框明细区的小标题。</summary>
        public const string WorkspaceCleanupConfirmDetailHeader = "会被删掉的目录（都在工作区根目录之下）：";

        /// <summary>确认框明细区的收尾：说清不动什么 + 不可还原。</summary>
        public const string WorkspaceCleanupConfirmDetailFooter =
            "删掉之后无法还原；工作区根目录下的散文件、日志、密码列表、设置一律不碰。";

        /// <summary>清理之后的日志（删掉几个 / 失败几个 / 释放多少）。</summary>
        public const string WorkspaceCleanupResultLogFormat = "工作区清理：删掉 {0} 个、失败 {1} 个，释放 {2}。";

        /// <summary>没有可清理的残留时的提示（用户点了按钮但确实没什么可清）。</summary>
        public const string WorkspaceCleanupNothingText = "现在没有可清理的工作区残留。";

        // ================================================================
        // 「继续解」（用户 2026-09-24 第 16 条追加：一键解到尽头 + 硬上限 10 层 + 到顶一键继续）
        // ================================================================

        /// <summary>到顶时那个按钮的文案（带还剩几个内层包）。</summary>
        public const string ContinueOneClickTextFormat = "继续解（还有 {0} 个内层包）";

        /// <summary>到顶时列表上方那一行提示（与按钮同一份事实）。</summary>
        public const string ContinueOneClickHintFormat =
            "已达到每批 {0} 层的上限，还剩 {1} 个内层包没解（已经勾好）—— 点「继续解」接着解。";

        // ================================================================
        // 导入后的无用物提醒 + 列表里删无用物（用户 2026-09-24 第 15 条）
        // ================================================================
        //
        // 用户原话："列表要能删无用物；每次操作的选完文件夹，就要出一个无用物提醒，
        // 用户可以选中关闭以后就不用触发了。"
        //
        // 与 §9.7「解压前的提醒」是**同一个判据**（SourceJunkScanner），只是时机提前到导入之后：
        // 选完文件夹立刻告诉他"这里有这些东西"，而不是等他点了处理才说。
        // 程序对无用物依旧**一个都不动**（不删/不改名/不搬）；"从列表里移除"只动任务列表。

        /// <summary>提醒框标题。</summary>
        public const string ImportJunkReminderTitle = "无用物提醒";

        /// <summary>开场：这些是什么 + 判据有多窄。</summary>
        public const string ImportJunkReminderIntro =
            "这次导入的文件夹里有一些文件，很可能是打包者附带的说明 / 网址 / 工具 / 广告之类的诱饵"
            + "（本程序只按文件名 + 魔数判了个大概，**不保证**它们真的没用）：";

        /// <summary>最多列这么多条（其余只报个数）。</summary>
        public const string ImportJunkReminderCountFormat = "这次一共认出 {0} 个：";

        /// <summary>还有多少个没列出来。</summary>
        public const string ImportJunkReminderMoreFormat = "  …还有 {0} 个（最多列 10 条）";

        /// <summary>撞到扫描上限时如实说明。</summary>
        public const string ImportJunkReminderTruncatedNote = "  （文件太多，本次只核对了前一部分）";

        /// <summary>收尾：程序不动它们 + "从列表里移除"动的只是列表。</summary>
        public const string ImportJunkReminderFooter =
            "本程序对上面这些文件**一个都不会动**（不删、不改名、不搬走）；"
            + "点「从列表里移除这些」只是把它们从任务列表里去掉，源文件照样留在原地。";

        /// <summary>主按钮：知道了（什么都不做）。</summary>
        public const string ImportJunkReminderKeepText = "知道了";

        /// <summary>次按钮：只把它们从任务列表里移除。</summary>
        public const string ImportJunkReminderRemoveText = "从列表里移除这些";

        /// <summary>可选项位：以后不再提醒（写进设置，界面上有开关能再打开）。</summary>
        public const string ImportJunkReminderOptionText = "以后不再提醒（可在「清理与删除」页把开关打开）";

        /// <summary>导入后提醒的日志（数量 + 前几个名字）。</summary>
        public const string ImportJunkReminderLogFormat = "导入后提醒：源目录里有 {0} 个疑似无用物（例如 {1}）。";

        /// <summary>无界面宿主：不弹窗、只写日志（与 §9.7 同一口径）。</summary>
        public const string ImportJunkReminderNoHostLog =
            "当前宿主没有界面：导入后的无用物提醒不弹窗、只写日志（什么都不删）。";

        /// <summary>用户勾了"以后不再提醒"并写进设置。</summary>
        public const string ImportJunkReminderSuppressedLog =
            "已记下「以后不再提醒无用物」：写进设置，可在「清理与删除」页把开关打开。";

        /// <summary>用户选了"从列表里移除这些"。</summary>
        public const string ImportJunkReminderRemovedLogFormat =
            "已按提醒里的选择，把 {0} 个无用物从任务列表里移除（源文件一个字节都没动）。";

        /// <summary>列表里手动移除任务（右键 / 「移除勾选的」）。</summary>
        public const string RemoveTasksLogFormat = "已从任务列表里移除 {0} 个（源文件一个字节都没动）。";

        /// <summary>一个都没勾时点「移除勾选的」。</summary>
        public const string RemoveCheckedTasksNoneText = "没有勾选任何任务，没有可移除的。";

        /// <summary>③「清理与删除」页那个开关的文案（第 15 条：导入后就提醒无用物）。</summary>
        public const string SettingsRemindJunkAfterImportLabel = "导入文件夹后提醒一次「疑似无用物」";

        /// <summary>它的说明（默认开；关掉 = 导入后一次都不提醒，判据与扫描都不跑）。</summary>
        public const string SettingsRemindJunkAfterImportHint =
            "默认开：每次选完文件夹就扫一遍源目录，把打包者常带的说明 / 网址 / 工具列出来，"
            + "并可以一键把它们从任务列表里去掉（**程序对这些文件一个都不会动**：不删、不改名、不搬走）。"
            + "关掉 = 导入后完全不提醒。";

        /// <summary>③ 页「工作区残留」那一组的标题。</summary>
        public const string WorkspaceLeftoverGroupHeader = "工作区残留（失败 / 取消留下的中间产物）";

        /// <summary>③ 页「工作区残留」那一组的说明（为什么以前只写日志、为什么程序不自动删）。</summary>
        public const string WorkspaceLeftoverGroupHint =
            "失败 / 取消 / 部分完成的任务会把中间产物留在工作区（那是那批唯一解出来的一份，"
            + "所以程序**不会自动删**）。这里列出它们占了多少空间，确认之后可以一次清掉 —— "
            + "只删工作区根目录下的那些任务目录，日志、密码、设置一律不碰。";

        /// <summary>② 「解压方式」页那个开关的文案（第 17 条：一键处理的那一个确认框可关）。</summary>
        public const string SettingsSkipOneClickConfirmLabel = "一键处理前不再弹确认框（直接按当前设置开始）";

        /// <summary>它的说明。</summary>
        public const string SettingsSkipOneClickConfirmHint =
            "默认关（每次都弹一个确认框，正文是「内容物会生成在什么地方」+「其余物是否自动删除」）。"
            + "勾上之后一键处理直接开始，一个框都不弹；这里可以随时取消勾选，把确认框要回来。";

        // ================================================================
        // 打包（⑤「打包」选项卡）
        // ================================================================
        //
        // ⚠ 这一组是**打包功能的文案唯一来源**（AGENTS.md §7）：窗口标题 / 步骤行 / 结论 /
        //    没有 Rar.exe 时的那段话都从这儿引，日志与失败原因也引同一份，
        //    免得"界面说一套、日志记一套"。
        //
        // 用户 2026-09-22 需求第 10 条：文件夹 A 的内容 → 7z 加密分卷 → 放进文件夹 B →
        //    B 压成带密码的 rar → 结果 = 压缩包 B。设计见 docs/打包功能.md。

        /// <summary>功能名（菜单项、窗口标题、日志里都用它）。</summary>
        public const string PackName = "打包文件夹为加密分卷";

        /// <summary>第一步之前的那一步：校验落点、算空间。</summary>
        public const string PackStepPreparing = "准备：校验落点、核算空间";

        /// <summary>第一步：7z 加密分卷。</summary>
        public const string PackStepVolumes = "步骤 1/2：生成 7z 加密分卷";

        /// <summary>第二步（外层容器 = rar）：生成外层加密 rar。</summary>
        public const string PackStepRar = "步骤 2/2：生成外层加密 rar";

        /// <summary>第二步（外层容器 = 7z）：生成外层加密 7z。</summary>
        public const string PackStepSevenZipOuter = "步骤 2/2：生成外层加密 7z";

        /// <summary>第三步（外层容器 = rar）：核对产物。</summary>
        public const string PackStepVerify = "校验产物：列出 rar 条目、核对分卷数";

        /// <summary>第三步（外层容器 = 7z）：核对产物。</summary>
        public const string PackStepVerifySevenZip = "校验产物：列出 7z 条目、核对分卷数";

        /// <summary>成功（外层容器做成了）。</summary>
        public const string PackSuccess = "打包成功";

        /// <summary>失败。</summary>
        public const string PackFailed = "打包失败";

        /// <summary>取消。</summary>
        public const string PackCancelled = "打包已取消";

        /// <summary>产物校验不通过（不变量 6：对不上就不显示成功）。</summary>
        public const string PackVerifyFailed = "产物校验不通过";

        /// <summary>按要求没有做外层容器（结果就是 B 里的 7z 分卷）。</summary>
        public const string PackPartialVolumesOnly = "只做了 7z 分卷（按要求不做外层容器）";

        /// <summary>
        /// 没有 Rar.exe 时的那句话。**必须包含"需要本机已安装 WinRAR"**（验收判据点名的字串）。
        /// </summary>
        public const string PackNeedRar =
            "这一步需要本机已安装 WinRAR（要 Rar.exe / WinRAR.exe）：程序不会替你装、也不会随包分发它。";

        /// <summary>
        /// 没有 Rar.exe 时的**三条出路**（用户 2026-09-23 决定）。
        ///
        /// <para>为什么是三条而不是两条：RARLAB 的 EULA（§3.1 / §3.2 / §3.3 / §10）明确禁止把
        /// <c>Rar.exe</c> 随任何软件包分发，所以"本机没装 WinRAR"是常态而不是异常；
        /// 只有"装 WinRAR"与"什么都不做"两条出路时，用户就只剩下"为了打包去装一个共享软件"这一条路。</para>
        /// </summary>
        public const string PackThreeWaysOut =
            "三条出路：① 装好 WinRAR（带 Rar.exe）后重试；"
            + "② 把「外层容器」改成 7z —— 无需额外安装（7-Zip 是 LGPL，随程序分发）；"
            + "③ 选「不做外层容器」，这次就只出 B 里的 7z 加密分卷。";

        // ── 外层容器三选一（用户 2026-09-23 决定；界面、日志、失败原因共用同一份说法） ──

        /// <summary>外层容器 = rar 的完整说法（含许可边界）。</summary>
        public const string PackOuterRarText =
            "外层容器 rar：把 B 压成一个带密码的 .rar（需要本机已安装的 WinRAR：程序只检测与调用它，绝不随包分发）";

        /// <summary>外层容器 = 7z 的完整说法（**无需额外安装**这句是关键信息）。</summary>
        public const string PackOuterSevenZipText =
            "外层容器 7z：把 B 压成一个带密码的 .7z（-mhe 连文件名一起加密）—— 无需额外安装（7-Zip 是 LGPL，随程序分发）";

        /// <summary>外层容器 = 不做。</summary>
        public const string PackOuterNoneText =
            "不做外层容器：结果就是 B 里的 7z 加密分卷（不需要 Rar.exe，也不需要多一份空间）";

        /// <summary>外层容器 rar 的短名（日志 / 摘要行）。</summary>
        public const string PackOuterRarShort = "rar";

        /// <summary>外层容器 7z 的短名。</summary>
        public const string PackOuterSevenZipShort = "7z";

        /// <summary>不做外层的短名。</summary>
        public const string PackOuterNoneShort = "不做外层";

        /// <summary>结果区提醒：B 可以自己删（用户可能要先检查分卷）。</summary>
        public const string PackKeepFolderHint =
            "文件夹 B 会保留下来（你可以先检查分卷）；确认结果没问题之后，B 可以自己删掉。";

        // ── 设置界面：自选 Rar.exe 路径（用户 2026-09-23 决定） ──

        /// <summary>
        /// 设置里那一格的标签。**必须写明许可边界**：这是用户自己装的 WinRAR 里的那一份，
        /// 程序只检测与调用，绝不随包分发（WinRAR 是共享软件）。
        /// </summary>
        public const string SettingsRarExePathLabel = "Rar.exe 路径：";

        /// <summary>设置里那一格留空时的含义（提示文案的后半句）。</summary>
        public const string SettingsRarExePathEmptyHint =
            "留空 = 自动用本机已装 WinRAR 目录里的那一份。";

        /// <summary>
        /// 设置里那一格的完整说明（**许可边界写在这里，不写"路径"了事**）。
        /// 界面提示、校验失败时的"改法"、以及日志都引它。
        /// </summary>
        public const string SettingsRarExePathHint =
            "这是你自己安装 / 下载的 WinRAR 里的 Rar.exe；本程序只检测与调用，绝不随包分发（WinRAR 是共享软件）。"
            + SettingsRarExePathEmptyHint;

        /// <summary>工具状态那一行的前缀（说明"当前用的是哪一份"）。</summary>
        public const string SettingsRarExePathStatusPrefix = "当前 Rar.exe：";

        // ================================================================
        // 「写回密码本…」（密码列表管理窗口）—— 用户 2026-09-24 反馈
        // ================================================================
        //
        // ⚠ 这一组是**这个功能的文案唯一来源**（AGENTS.md §7）：按钮 / 确认框 / 提示条 / 未写回标记
        //    都从这里引，别在 XAML 或 ViewModel 里另写一份中文字面量。
        //
        // 起因（用户真机反馈）：他在「密码列表管理」里手动加了一条密码、又调了上移/下移，
        // 关掉程序就没了。机制是 PasswordService.Passwords **只在内存里**，
        // 启动时由密码本 txt 重新加载 —— 所以手工条目与手工调的顺序重启即丢。
        //
        // ⚠ 2026-09-24 用户拍板后，不变量 5 已经改了：列表**可以**按本机 DPAPI（机器范围）加密落盘，
        //    重启会原样恢复（见 AGENTS.md §6 不变量 5）。但「写回密码本…」这条路**照旧重要** ——
        //    加密记忆换机器就解不开，只有用户自己的 txt 才是真正带得走的长期载体。

        /// <summary>按钮文案。</summary>
        public const string PasswordWriteBackButtonText = "写回密码本…";

        /// <summary>
        /// 按钮的 ToolTip。
        ///
        /// <para>⚠ 2026-09-24 起措辞改了：以前这里写"密码只存在内存里，关掉程序就没了" ——
        /// 现在列表本身是**按本机加密保存**的，那句话已经不准确了。真正需要写回的理由变成了
        /// "只有你自己的 txt 才带得走"（换机器 / 重装系统 / 关掉记忆开关时，加密记忆都靠不住）。</para>
        /// </summary>
        public const string PasswordWriteBackButtonHint =
            "把列表里手动添加的密码追加进你自己的密码本 txt（写前自动备份）。"
            + "列表本身按本机加密保存，但它换机器 / 重装系统就解不开了 —— 写回你自己的文件才真正带得走。";

        /// <summary>没有待写条目时的说明（按钮置灰时用户能看懂为什么）。</summary>
        public const string PasswordWriteBackNothingToWrite = "没有需要写回的条目。";

        /// <summary>
        /// 确认框正文（**只有数量与文件名，不含任何密码原文**）。
        ///
        /// <para>为什么数量与文件名要在这里：用户是在对"一个具体文件"动手，
        /// 文件名是他唯一能核对的线索；而密码本身放在 Detail 区（明文，
        /// 界面上给他看是应该的），不进 Message —— Message 会被无界面宿主的降级日志记下来。</para>
        /// </summary>
        public const string PasswordWriteBackConfirmFormat =
            "要把 {0} 条密码追加到「{1}」吗？";

        /// <summary>确认框正文的第二段：写前备份 + 只追加（不重写、不重排）。</summary>
        public const string PasswordWriteBackConfirmNote =
            "只追加到文件末尾，已有的内容一个字节都不会改；写之前会先把原文件备份成同目录下的 .bak-日期时间。";

        /// <summary>确认框 Detail 区的表头（下面是**明文**密码清单）。</summary>
        public const string PasswordWriteBackConfirmDetailHeader = "将要写入的密码（明文）：";

        /// <summary>明文清单里的一条。</summary>
        public const string PasswordWriteBackConfirmDetailItemFormat = "{0}. {1}";

        /// <summary>
        /// 确认框里的那条提醒：**候选顺序 + 每层尝试上限**。
        ///
        /// <para>写回是追加到**末尾**，而候选顺序里密码列表排在映射式命中与统一密码之后 ——
        /// 密码本条目一多，排在后面的候选会被上限截断（状态是「达到密码尝试上限」，不是「密码错误」），
        /// 用户看到的现象就是"我明明加了密码，它却像没识别到"。这条提醒把该改什么说清。</para>
        /// </summary>
        public const string PasswordWriteBackAttemptLimitHintFormat =
            "提醒：写回是追加到密码本**末尾**，而尝试顺序是「空密码 → 最近成功 → 映射式命中 → 统一密码 → 密码列表」。"
            + "你当前的「每层密码尝试上限」是 {0} 条 —— 密码本条目一多，排在后面的候选会被这个上限截断"
            + "（那时状态显示「达到密码尝试上限」，不是「密码错误」）。"
            + "想让它一定被试到：把上限调大（设置 → 密码设置），或者改用「名称:密码」的映射式写法（映射命中排在列表遍历之前）。";

        /// <summary>
        /// 未写回标记的**短文案**（备注列 / ToolTip 都用它）。
        /// 用户一眼能看出"这个值还不在任何一本已记住的密码本里"。
        /// </summary>
        public const string PasswordWriteBackPendingMarker = "手动添加 · 未写回";

        /// <summary>
        /// 未写回条目的 ToolTip 第二行：一句短的"为什么" + 该改什么（与确认框那条同一个意思）。
        ///
        /// <para>⚠ 判据是**按值**的（2026-09-24 第 21 条）：这条的值不在任何一本已记住的密码本里。
        /// 说清这一点很重要 —— 用户上次就是"按了写回、提示成功、重启又显示未写回"，
        /// 所以这里必须写明"重启后会重新读文件判定，不靠当场记一笔"。</para>
        /// </summary>
        public const string PasswordWriteBackPendingHint =
            "这条手动添加的密码，值**不在任何一本已记住的密码本里** —— 列表按本机加密保存，"
            + "但换机器、重装系统或关掉「记住密码列表」就没了，"
            + "点「" + PasswordWriteBackButtonText + "」才会长期保留在你自己的文件里。"
            + "写回成功后标记立刻消失，重启后仍然消失（程序重新读书里的值来判定，不靠当场记一笔）。"
            + "写回是追加到密码本末尾，条目多时要留意「每层密码尝试上限」会把后面的候选截断"
            + "（建议调大上限，或改用「名称:密码」的映射式写法）。";

        /// <summary>写回成功后给手动条目写的备注（标记消失后，这里留一句"已经安全了"）。</summary>
        public const string PasswordWriteBackDoneRemark = "手动添加 · 已写回密码本";

        /// <summary>成功后的提示条格式：写了几条 / 跳过几条 / 写到哪个文件 / 备份在哪。</summary>
        public const string PasswordWriteBackSucceededFormat =
            "已把 {0} 条密码写回密码本「{1}」。{2}";

        /// <summary>备份那一行的格式（给用户一个能自己去核对的路径）。</summary>
        public const string PasswordWriteBackBackupLineFormat = "写前已备份为：{0}。";

        /// <summary>成功但有跳过时追加的说明（空密码 / 文件里已经有 / 本次重复）。</summary>
        public const string PasswordWriteBackSkippedFormat = "另外 {0} 条没有写：{1}。";

        /// <summary>跳过原因之一：空密码（写进去没有意义，而且容易被误用）。</summary>
        public const string PasswordWriteBackSkippedEmptyText = "{0} 条是空密码";

        /// <summary>
        /// 跳过原因之一：只由空白字符组成。
        /// 密码本解析器把"只有空白字符的行"当排版空行跳过，写进去等于写一条读不回来的内容。
        /// </summary>
        public const string PasswordWriteBackSkippedBlankText = "{0} 条只由空格组成（密码本读不回来，已跳过）";

        /// <summary>跳过原因之一：文件里已经有同样的值。</summary>
        public const string PasswordWriteBackSkippedExistingText = "{0} 条文件里已经有";

        /// <summary>跳过原因之一：本次输入里重复出现。</summary>
        public const string PasswordWriteBackSkippedDuplicateText = "{0} 条本次重复";

        /// <summary>成功但一条都没写（全都是空密码或文件里已经有了）。</summary>
        public const string PasswordWriteBackNothingWrittenFormat =
            "没有可写的密码：{0} 条都不用写（密码本「{1}」一个字节都没有改动）。";

        /// <summary>失败提示条的前缀。</summary>
        public const string PasswordWriteBackFailedFormat = "写回密码本失败：{0}";

        /// <summary>
        /// 日志里唯一允许出现的那句（**只有数量与文件名，绝不出现明文密码** —— §8 隐私红线）。
        /// </summary>
        public const string PasswordWriteBackLogFormat = "已把 {0} 条密码写回密码本 {1}（写前已备份）。";

        /// <summary>日志：一条都没写。</summary>
        public const string PasswordWriteBackLogNoOpFormat =
            "写回密码本 {0}：没有可写的条目（{1} 条被跳过），文件一个字节都没有改动。";

        /// <summary>日志：用户取消了确认框。</summary>
        public const string PasswordWriteBackLogCancelled = "已取消「写回密码本」：没有写任何东西。";

        /// <summary>日志：失败（原因里不含密码原文）。</summary>
        public const string PasswordWriteBackLogFailedFormat = "写回密码本失败：{0}";

        /// <summary>日志：目标密码本文件不存在。</summary>
        public const string PasswordWriteBackLogTargetMissingFormat = "写回密码本失败：目标文件不存在（{0}）。";

        /// <summary>
        /// 目标密码本文件不存在时的提示（写回的目标是"他本来就有的那份密码本"，不新建一个空文件）。
        /// </summary>
        public const string PasswordWriteBackTargetMissingFormat =
            "设置里记的密码本文件已经不在了：「{0}」。请重新选一个 txt，或者先用「导入 txt」导入你的密码本。";

        /// <summary>窗口底部那一行的成功文案（与提示条同一口径的短句）。</summary>
        public const string PasswordWriteBackStatusFormat = "已把 {0} 条密码写回密码本「{1}」。";

        /// <summary>窗口底部那一行的"一条都没写"文案。</summary>
        public const string PasswordWriteBackStatusNoOp = "没有需要写回的条目。";

        /// <summary>用户取消了确认框。</summary>
        public const string PasswordWriteBackStatusCancelled = "已取消写回密码本。";

        // ================================================================
        // 密码列表的**本机加密记忆**（DPAPI 机器范围）—— 用户 2026-09-24 拍板
        // ================================================================
        //
        // ⚠ 这一组同样是**唯一来源**（AGENTS.md §7）：设置界面的开关与说明、密码列表窗口顶部那句、
        //    记忆摘要、读不出来的提示、以及日志文案都从这里引，别在 XAML / ViewModel 里另写一份。
        //
        // 用户选定的是 Windows 自带的 DPAPI **机器范围**（CryptProtectData + CRYPTPROTECT_LOCAL_MACHINE）：
        // 跟 Windows 账号无关（同机换谁登录都能用、不弹框），文件放 <程序目录>\data\。
        // **代价必须如实写在界面上**：同机任何本机用户都可能解开；换机器 / 重装系统解不开。

        /// <summary>
        /// 密码列表窗口顶部那句（**必须与事实一致**）。
        ///
        /// <para>改之前写的是"密码只存在内存里" —— 那是 2026-09-24 之前的实话，现在已经不准确了；
        /// 界面上留着一句过期的话，比没有那句话更糟（用户会照着它做判断）。</para>
        /// </summary>
        public const string PasswordListPrivacyHint =
            "列表按本机加密保存（机器范围 DPAPI，跟 Windows 账号无关；关掉「记住密码列表」则只在本次运行内有效）；"
            + "日志不记明文。导入时保留密码中的空格；列表自上而下就是尝试顺序。";

        /// <summary>密码列表窗口顶部的"当前列表"摘要：总数 / 启动时由记忆恢复的条数 / 记住的密码本本数。</summary>
        public const string PasswordListMemorySummaryFormat =
            "当前列表：{0} 条（其中启动时由记忆恢复 {1} 条）＋ 记住的密码本 {2} 本";

        /// <summary>「记住密码列表」关着时的摘要（一句话说清"这次关掉程序就回到纯内存"）。</summary>
        public const string PasswordListMemoryDisabledSummary =
            "「记住密码列表」已关闭：这份列表只在本次运行内有效，关掉程序就没了（磁盘上已记住的那份不会被删）。";

        /// <summary>
        /// 读不出记忆时给用户看的那一句（**不弹错误框、不阻断**）。
        ///
        /// <para>为什么要有"可用「写回密码本」把它带走"这半句：换机器之后那份记忆确实解不开了，
        /// 但用户的列表并没有"被程序弄丢"—— 出路是把列表写回他自己的密码本 txt，人肉搬过去。</para>
        /// </summary>
        public const string PasswordListMemoryUnavailableFormat =
            "上次的密码列表无法读取，已忽略（{0}）。这不会影响你的密码本文件：程序没有覆盖、也没有删除那份记忆。"
            + "如果是换了机器或重装了系统，可以用「写回密码本…」把当前列表带走。";

        /// <summary>记忆保存失败时的提示（不弹框、不阻断，只在提示条上说一句）。</summary>
        public const string PasswordListMemorySaveFailedFormat = "这次的密码列表没能存下来（{0}）；列表本身照常可用。";

        /// <summary>主界面摘要那一行挂的短提示（明细在密码列表窗口的提示条上）。</summary>
        public const string PasswordListMemoryUnavailableShort =
            "上次的密码列表读不出来（已忽略，文件没被动；明细见「密码列表管理」）";

        /// <summary>
        /// 启动时那条 INFO：一句话说清"记忆恢复了几条 + 几本密码本补了几条"（**只有条数与文件名**）。
        /// </summary>
        public const string PasswordListMemoryRestoreLogFormat =
            "密码列表记忆：恢复 {0} 条；按记住的 {1} 本密码本（{2}）补充 {3} 条。";

        /// <summary>上面那句里"一本都没有"时占位用的词。</summary>
        public const string PasswordListMemoryNoBooksText = "无";

        /// <summary>某本密码本找不到时的 WARN（**只写文件名**，§8：个人路径不入日志）。</summary>
        public const string PasswordListMemoryBookMissingLogFormat =
            "上次记住的密码本找不到了，已跳过自动加载：{0}";

        /// <summary>读记忆失败时的 WARN（原因已经脱敏，见 <c>PasswordListStore</c>）。</summary>
        public const string PasswordListMemoryLoadFailedLogFormat =
            "读取密码列表记忆失败，已忽略（{0}）；那份文件没有被覆盖也没有被删除。";

        /// <summary>写记忆失败时的 WARN。</summary>
        public const string PasswordListMemorySaveFailedLogFormat =
            "保存密码列表记忆失败（{0}）；列表只在本次运行内有效。";

        /// <summary>关掉「记住密码列表」时那条 INFO：说清"不写也不读"。</summary>
        public const string PasswordListMemoryDisabledLog =
            "「记住密码列表」已关闭：不写也不读密码列表记忆（磁盘上已有的那份不会被删除）。";

        // ── ④「密码」页：记住密码列表 + 已记住的密码本 ──

        /// <summary>设置里那个开关的文案。**必须写明"加密存在程序目录 + 跟 Windows 账号无关"**。</summary>
        public const string SettingsRememberPasswordListLabel = "记住密码列表（加密保存在程序目录，跟 Windows 账号无关）";

        /// <summary>
        /// 开关下面那行副作用说明（用户 2026-09-24 要求：代价与关闭后的行为都要写在界面上）。
        /// </summary>
        public const string SettingsRememberPasswordListHint =
            "开：列表的内容、顺序、启用状态、手动添加的条目会一起加密存到 程序目录\\data\\password-list.dat，"
            + "下次启动原样恢复。用的是 Windows 自带的 DPAPI 机器范围加密 —— 跟 Windows 账号无关（同机换谁登录都能用、不弹框），"
            + "代价是：同机任何本机用户都可能解开它；换机器或重装系统解不开，那时程序会忽略并提示，"
            + "可以用「写回密码本…」把列表带走。关：既不写也不读这个文件，重启回到纯内存（磁盘上已有的那份不会被删）。";

        /// <summary>"已记住的密码本"那一组的标题。</summary>
        public const string SettingsRememberedBooksTitle = "已记住的密码本（启动时按这个顺序逐本合并）";

        /// <summary>一本都没记住时的说明。</summary>
        public const string SettingsRememberedBooksEmptyText = "还没有记住任何密码本 —— 启动时不会自动加载任何密码本。";

        /// <summary>"已记住的密码本"那一组的说明。</summary>
        public const string SettingsRememberedBooksHint =
            "这些文件是你自己点过「导入密码本」的那些。启动时按这个顺序逐本读取，只补列表里还没有的密码；"
            + "移除一项只是不再自动加载它，磁盘上的文件一个字节都不会动。";

        /// <summary>移除一本的按钮文案。</summary>
        public const string SettingsRememberedBooksRemoveButtonText = "移除";

        /// <summary>移除一本的按钮提示。</summary>
        public const string SettingsRememberedBooksRemoveButtonHint =
            "不再自动加载这一本（文件本身不会被删、不会被改）。";

        /// <summary>设置里"记住密码列表"关着时，这一组的降级说明。</summary>
        public const string SettingsRememberedBooksDisabledText =
            "「记住密码列表」关着：这份清单这次不生效（密码列表窗口里的改动不会跨重启保留）。";

        // ── 清理与删除页：解压前的提醒开关（用户 2026-09-24 第 15 条的界面入口） ──

        /// <summary>
        /// 「解压前提醒无用物」这个开关的标签。
        ///
        /// <para>
        /// 提醒本身早就有了（AGENTS.md §9.7：每次解压前列出可能的无用物与"没有可用密码"的包），
        /// 但一直只能在弹窗里勾"本次运行不再提示"（只记内存）。用户第 15 条要求它可关，
        /// 于是补上这个设置项 —— 关掉就是**不再扫、不再弹**。
        /// </para>
        /// </summary>
        public const string SettingsRemindBeforeExtractLabel = "解压前提醒可能的无用物与没有可用密码的包";

        /// <summary>开关下面那一行说明（说清"关掉之后会少了什么"）。</summary>
        public const string SettingsRemindBeforeExtractHint =
            "开：每次解压前先扫一遍源目录，把「看起来像打包者附带的说明 / 网址 / 工具」的文件与「一个可用密码都没有」的包列出来，"
            + "让你先看一眼再决定（这两个提醒都不影响解压本身）。关：这一遍扫描与弹窗都不做，直接开始。";

        /// <summary>关掉提醒时写一条日志：事后能判断"这次为什么没弹提醒"。</summary>
        public const string RemindBeforeExtractDisabledLog =
            "设置里关掉了「解压前提醒」：本次跳过无用物 / 无可用密码的扫描与提醒。";

        // ── 关于 / 版本 / 许可（帮助 → 关于 与⑥设置页共用） ──

        /// <summary>
        /// 「关于」里的许可那一段。
        ///
        /// <para>
        /// 必须与 LICENSE 的第三方组件声明同口径：内置 7-Zip 是 LGPL（+ unRAR 限制条款）、
        /// 内置 UnRAR 是 RARLAB freeware（允许随包分发），而 <c>Rar.exe</c>/<c>WinRAR.exe</c>
        /// 是共享软件、**绝不随包分发**（AGENTS.md §3.1）。用户问"这东西能不能商用 / 能不能带走"时，
        /// 答案就在这里，不必去翻仓库。
        /// </para>
        /// </summary>
        public const string AboutLicenseText =
            "许可：本程序 MIT。" + "\r\n"
            + "内置 7-Zip（LGPL + unRAR 限制条款）、内置 UnRAR（RARLAB freeware，许可明确允许随包分发）。" + "\r\n"
            + "Rar.exe / WinRAR.exe 是共享软件：程序**只检测与调用**你自己装的那一份，绝不复制、绝不随包分发，" + "\r\n"
            + "也绝不会拿 7-Zip 假装做出 .rar。";

    }
}
