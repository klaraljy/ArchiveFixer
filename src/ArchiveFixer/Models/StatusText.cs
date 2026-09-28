namespace ArchiveFixer.Models
{
    /// <summary>
    /// 任务状态、操作、后缀状态、密码状态等界面文本的统一定义。
    /// 所有状态字符串统一从这里引用，避免各处散落字面量不一致。
    /// </summary>
    public static class StatusText
    {
        /// <summary>
        /// 主窗口标题（左上角那一行）。两件事在这里合流：
        /// ①**反馈方式写在标题上**（用户 2026-09-26："现在将应用左上角的应用名可以改一下，
        /// 在后面加上『遇到问题反馈给作者，发邮件 klaraljy0617@outlook.com』"）；
        /// ②**标题里不带版本号**（同一天他要求删掉"第十一版"这几个字）——
        /// 版本只在「帮助 → 关于」里报（那份读的是程序集版本，不靠手写字面量）。
        ///
        /// <para>⛔ 邮箱是**唯一**要改的地方：About 那一块也引用 <see cref="FeedbackEmail"/>，
        /// 不许在别处再写一遍字面量。</para>
        /// </summary>
        public const string AppWindowTitle =
            "ArchiveFixer - 批量压缩包识别与解压工具　｜　遇到问题请反馈给作者（发邮件 klaraljy0617@outlook.com）";

        /// <summary>作者的反馈邮箱（标题与「关于」共用一份）。</summary>
        public const string FeedbackEmail = "klaraljy0617@outlook.com";

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
        /// 资源预算上限类拒绝的**统一尾巴**（用户 2026-09-25 第 36 条）。
        ///
        /// <para>为什么要写这一句：他真机看到的失败清单是
        /// "单个文件解压后 5242880000 字节（4.88 GiB）超过单文件上限 4294967296 字节（4 GiB）：
        /// Code Complete-BZ.7z(删掉.001" —— 读起来像"这个包坏了"，而实际是程序自己的安全上限，
        /// 而且当时界面上根本没有这一格。判决必须自己说清它是什么、去哪儿改。</para>
        ///
        /// <para>⛔ 目标盘空间不足那一档**不加**这句（原因真是"盘不够"，处置方式是清盘 / 换盘，
        /// 与"调上限"不是一回事）。文案只有这一份：<c>ResourceBudget</c>（解压前预检与运行时累计）、
        /// <c>ExtractionCoordinator</c>（产物事后核算）都引用它。</para>
        /// </summary>
        public const string SecurityCapHint = "（这是程序的安全上限，不是这个包坏了；可在⑥设置 →「安全上限」里调大）";

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
        /// 用户点了「停止后续」之后**本批唯一**那条日志（2026-09-27 真机：他连点两次，
        /// 日志里出现两遍"已请求停止后续任务"、收尾又是另一句"已停止后续任务"，三句不同的话）。
        ///
        /// <para>措辞按他当天的口径写实：停止后续 = 停止所有的东西，
        /// 但**当前任务是在安全点停**（候选密码之间），要立刻掐断得用①页「取消当前」——
        /// 两档的区别必须写在这一句里，否则用户不知道还有更强的那一档。</para>
        /// </summary>
        public const string StopRequestedNotice =
            "已按「停止后续」停下：不再启动新任务；正在跑的那一个会在下一个安全点停下（密码候选之间、写完当前条目之后）。"
            + "要立刻掐断当前任务，点①页「取消当前」。";

        /// <summary>「移除勾选的」那颗按钮的说明 —— 两种用法都要写明（只动列表，磁盘一个字节都不动）。</summary>
        public const string RemoveCheckedTasksHint =
            "把勾选的任务从列表里去掉；一个都没勾时，就移除「当前点中（高亮）的那一行」。"
            + "只动列表：源文件、输出目录、日志一个字节都不动（移除错了再「添加文件 / 添加文件夹」一次就回来）。"
            + "处理中也能用 —— 正在跑的那一个会继续跑完，只是不再显示在列表里。";

        /// <summary>
        /// 没有勾选、于是退到"当前高亮那一行"时写的那条日志（用户 2026-09-27：以前这种时候
        /// 一个字都不说，界面上看着就是"按钮没反应"）。{0} = 被移除的文件名。
        /// </summary>
        public const string RemoveCheckedTasksHighlightFallbackLogFormat =
            "「移除勾选的」：没有勾选任何任务，改为移除当前点中的那一行 —— 「{0}」。";

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
        /// 后缀一栏给这一类，是为了让用户一眼看出"不要改后缀"：
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

        /*
         * ===== 改名预览：跳过 / 冲突的**原因**（唯一文案来源） =====
         *
         * 为什么集中放这里：这几句会出现在预览表的「错误信息」列、任务列表的错误信息列、
         * 以及失败清单里，写散在各个分支里就会各说各话（2026-09-26 审计顺手收口）。
         */

        /// <summary>
        /// 分卷文件**五种改名操作一律不动**（原话口径见 <c>FileNameHelper.IsVolumePartFileName</c>）。
        ///
        /// <para>以前这道闸门只长在「智能修正」里，于是「替换后缀」能把 <c>set.7z.001</c> 改成
        /// <c>set.7z.7z</c> —— 用户点一下就把整组弄成"再也解不开"，程序之后只会报「分卷缺失」。</para>
        /// </summary>
        public const string RenameVolumeSkippedReason = "分卷文件不改名（7z / WinRAR 只认 .001 这一套命名，改了名整组就解不开）";

        /// <summary>
        /// 本次批次里两行改成了同一个名字 → 后一行自动错开（与执行期同一套"绝不覆盖"的语义）。
        ///
        /// <para>修的是一个真缺陷：预览以前只查**磁盘上**的冲突，不查**本批次内**两行撞名，
        /// 于是预览说"可以改 2 个"，执行到落位那一步 File.Move 撞名 → 整批回滚 + 一句看不懂的报错。</para>
        /// </summary>
        public const string RenameBatchDuplicateAutoRenameFormat =
            "本次有两行改成了同一个名字，这一行自动改成 {0}（绝不覆盖）";

        /// <summary>「询问」档：这一行撞名了，要在预览表里逐条选怎么办（**列名必须写对**）。</summary>
        public const string RenameConflictNeedChoiceReason =
            "目标文件已存在 —— 在「冲突」列里选怎么办（不选 = 自动重命名，绝不覆盖）";

        /*
         * ===== 「列表里只有分卷组的后续卷」这一档（用户 2026-09-26 拍板） =====
         *
         * 以前这种文件（`set.7z.002` 自己一个任务）报的是「格式未知 / 未识别为支持的压缩格式」——
         * 那句话会让人以为文件坏了，而真相是"缺第 1 卷"。
         * ⛔ 四个占位符的顺序不许改：卷名 / 第几卷 / 标准首卷名 / 那句"去哪找首卷"。
         */
        public const string LaterVolumeOnlyFormat =
            "「{0}」是分卷组的第 {1} 卷（后续卷）：本程序按第 1 卷开解，所以要连「{2}」一起加进来 —— {3}";

        /// <summary>首卷就在同目录里时后面那半句（**这一行本身不用管** —— 首卷那一行会带着整组一起解）。</summary>
        public const string LaterVolumeOnlyFirstVolumeHere =
            "它就在同一个文件夹里：如果它也在列表里，这一行不用管（那才是开解的那一行，整组会跟着它走）；不在列表里就用「添加文件」把它加进来。";

        /// <summary>同目录里也找不到首卷时后面那半句。</summary>
        public const string LaterVolumeOnlyFirstVolumeMissing =
            "同一个文件夹里没有找到它；把首卷找回来放进同一个文件夹即可（本程序不会去别的目录替你找）。";

        /// <summary>
        /// 「首卷补齐后**并组**」（用户 2026-09-26 拍板的那半句）：后续卷那一行在列表里找得到
        /// 它那一组的首卷时，改成「已并入」——那一组由首卷那一行带着一起解，这一行不再单独算一单。
        ///
        /// <para>为什么必须改：改之前这种行会一直顶着「分卷缺失」停在列表里，汇总把它算成失败/未处理，
        /// 而它其实**什么都不用做**（7z 自己会按名字找同组的其他卷）。</para>
        /// </summary>
        public const string LaterVolumeMergedNoteFormat =
            "已并入「{0}」那一行：分卷组由「第 1 卷」那一行带着一起解，这一行不用管（也不会被单独处理）。";

        /// <summary>日志里那一行（并组时写一条，可追溯）。</summary>
        public const string LaterVolumeMergedLogFormat =
            "{0}：已并入「{1}」那一行（分卷组按第 1 卷开解，这一行不再单独处理）";

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
            "「一键处理 / 只解压 / 智能修正 / 移除选中 / 删除其余物 / 清理空文件夹」一律只认勾选（最左侧一列）；" +
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
            "列表里有 {1} 个任务，当前一个都没勾。这个命令只处理你勾选的任务：" +
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
            "添加 = 先清空整张任务列表再加入你这次选的内容（替换语义）。" +
            "要往现有列表里加，用「文件 → 追加到列表」。";

        /// <summary>「追加到列表」的界面提示。</summary>
        public const string AppendToListHint =
            "追加 = 保留列表里现有的任务，只把你这次选的内容加到末尾（与「添加」的替换语义相反）。";

        /// <summary>
        /// 导入日志：**替换**语义（<c>{0}</c> = "，已清掉原有 N 个任务"或空串）。
        /// 日志里也必须说清这次用的是哪一种语义 —— 用户事后要能回答"我上一批怎么不见了"。
        /// </summary>
        public const string ImportReplaceLogFormat =
            "开始导入路径（替换语义：先清空整张表再添加{0}）。要往现有列表里加，用「文件 → 追加到列表」。";

        /// <summary>导入日志：**追加**语义。</summary>
        public const string ImportAppendLogFormat =
            "开始导入路径（追加语义：保留列表里现有的任务，新任务加到末尾）。";

        /// <summary>导入日志：清掉了多少个旧任务（拼在 <see cref="ImportReplaceLogFormat"/> 里）。</summary>
        public const string ImportReplacedTasksTextFormat = "，已清掉原有 {0} 个任务";

        /// <summary>导入日志：新增了多少个任务。</summary>
        public const string ImportFinishedLogFormat = "导入完成，新增任务 {0} 个。";

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
            + "（本程序只按文件名 + 魔数判了个大概，不保证它们真的没用）：";

        /// <summary>① 段结尾：**程序不会动它们** + 解压完由用户自己判断（用户点名要写清的两件事）。</summary>
        public const string JunkReminderJunkFooter =
            "上面这些文件本程序一个都不会动（不删、不改名、不搬走）；"
            + "解压完成后你可以自己看一眼，再决定要不要删。";

        /// <summary>① 段：这次一共认出多少个（列出来的最多 10 条，其余只报个数）。</summary>
        public const string JunkReminderJunkCountFormat = "这次一共认出 {0} 个：";

        /// <summary>无用物还有多少个没列出来（上限见 <c>SourceJunkScanner.MaxReportedItems</c>）。</summary>
        public const string JunkReminderJunkMoreFormat = "  …还有 {0} 个（无用物最多列 10 条）";

        /// <summary>撞到扫描上限（每个目录 2000 个文件 / 魔数体检预算）时如实说明，不假装扫全了。</summary>
        public const string JunkReminderTruncatedNote = "  （源目录里的文件太多，本次只核对了前一部分）";

        /// <summary>② 段标题：需要密码、但当前一个可用候选都没有的包。</summary>
        public const string JunkReminderPasswordHeaderFormat =
            "② 本批有 {0} 个包需要密码，但当前一个可用候选都没有"
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

        /// <summary>其余物这一档：删除操作=彻底删除 → 解压成功后自动彻底删掉（不可恢复）。</summary>
        public const string OneClickConfirmRestAutoDelete = "解压成功后自动彻底删除（不可恢复，直接省空间）";

        /// <summary>其余物这一档：删除操作=移入回收站 → 解压成功后自动移入回收站（可还原）。</summary>
        public const string OneClickConfirmRestRecycle = "解压成功后自动移入回收站（可还原；空间要等清空回收站才释放）";

        /// <summary>其余物这一档：删除操作=不动其余物（默认）→ 留在输出目录的「其余物」里。</summary>
        public const string OneClickConfirmRestKeep = "不自动删除，留在输出目录的「其余物」里";

        /// <summary>第三行（短）：源包怎么处理。</summary>
        public const string OneClickConfirmSourceLabel = "源包：";

        /// <summary>
        /// 确认框里那条**常驻提醒**：程序不会判断"内容物里该不该有压缩包"（用户 2026-09-26 要求）。
        ///
        /// <para>他的原话（我按他的意思整理成界面上能站得住的一段）："我们没有压缩包内容识别操作，
        /// 很有可能您最终想要得到的内容物里面含不该解开的压缩文件，请您仔细判别，
        /// 并在解压方式里面调节一次解压的检测次数"。</para>
        ///
        /// <para>⚠ 为什么必须写在**动手之前**：嵌套那一档（②「解压方式」→「嵌套与覆盖」）默认是
        /// 「只解当前这一层」，可选「单链自动展开」「展开所有分支」+ 最大层数（默认 10）——
        /// 选错档位时，本来想留着的那几个内层包会被一路解开（第 26 条那个待商榷的形状）。
        /// 程序不去猜"这个包该不该有内层包"，但必须在动手前把这件事说清、并指出去哪儿调。</para>
        /// </summary>
        public const string OneClickConfirmNestedCaveat =
            "⚠ 这一步不会判断「内容物里该不该有压缩包」：如果最终想要的内容物里面本来就放着压缩包，"
            + "按②「解压方式」页 →「嵌套与覆盖」里那一档（含最大嵌套层数），它们会被一起解开。"
            + "请先看清这个包，并在那一栏把嵌套模式与层数调成你要的次数。";

        /// <summary>可选项位：以后不再询问。</summary>
        public const string OneClickConfirmSuppressText = "以后不再询问，按当前设置直接开始（可在「解压方式」页再打开）";

        /// <summary>折叠区标题：要改就展开（默认收起 —— 用户嫌旧面板啰嗦）。</summary>
        public const string OneClickConfirmExpanderHeader = "本次改一下（落点 / 终端落法 / 源包处理）";

        /// <summary>多包时那一行的后缀：每个包各建一个同名子文件夹。</summary>
        public const string OneClickConfirmMultiPerArchiveFormat = "（本批 {0} 个包，每个包各建一个同名子文件夹）";

        // ==================================================================
        //   ①页「空间不足」模式（用户 2026-09-27 拍板）
        //
        //   它是一个**运行期**开关：⛔ 不写设置、不记忆，开着一批就按它跑完这一批。
        //   它覆盖三件事：并发档（含「全速」）、源包处理（定稿 + 校验通过即永久删除）、
        //   其余物（过程物彻底删除）。下面这几条文案就是它的全部"对外说法"。
        // ==================================================================

        /// <summary>①页主操作栏那个黄色勾选框的文字。</summary>
        public const string SpaceTightToggleLabel = "空间不足";

        /// <summary>
        /// 「空间不足」旁边那个**不删原包**勾选框的文字（用户 2026-09-27："所有测试的情况下弄一个设置
        /// 不删除原包的功能，在空间不足旁边弄一个，空间不足但是不删除原包的操作"）。
        /// </summary>
        public const string SpaceTightKeepSourceLabel = "不删原包";

        /// <summary>
        /// 那个勾选框的说明。必须写清三件事：它是①那个模式的**安全档**、它只管"动不动源包"、
        /// 以及它**不省源包那份空间**（空间不够时该拦还是会拦）。
        /// </summary>
        public const string SpaceTightKeepSourceToolTip =
            "「空间不足」的安全档：并发与排序照旧由空间决定，但源包一个字节都不动"
            + "（不搬进其余物、也不删除），只把过程物（内层包等）按成功后的删除档清掉。"
            + "⚠ 它「不省源包那份空间」：盘放不下时该拦还是会拦（这正是它的用途 —— 先看清空间怎么变，"
            + "再决定要不要开那个会删源包的档）。"
            + "勾上它会自动把「空间不足」也勾上；只对本次运行有效，不写设置。";

        /// <summary>
        /// ②③页那条覆盖提示的**安全档**版本（「不删原包」勾着时用它）。
        ///
        /// <para>⚠ 两条提示必须各说各的：安全档下源包**一个字节都不动**，
        /// 拿"会删除源包"那条去吓用户（或反过来让他以为源包安全）都是撒谎。</para>
        /// </summary>
        public const string SpaceTightKeepSourceOverrideNotice =
            "⚠ 本次开着①页的「空间不足」+「不删原包」：并发被自动接管（「全速」不生效，"
            + "你的并发档调得更低时按更低的跑），其余物（过程物）任务成功后彻底删除，"
            + "但「源包一个字节都不动」（不搬、不删）。"
            + "⛔ 本次不写设置；由于源包不回收，这一档需要的空间比会删源包的那一档多一份源包。";

        /// <summary>
        /// 一键处理确认框里的那条红字（安全档版本）。
        /// </summary>
        public const string SpaceTightKeepSourceConfirmText =
            "⚠ 「空间不足」+「不删原包」已开：并发与「全速」改由空间自己决定，其余物（过程物）成功后彻底删除；"
            + "源包一个字节都不动（不搬进其余物、也不删除）。"
            + "这一档不回收源包那份空间 —— 盘上需要的余量约等于「源包 + 内容物」。";

        /// <summary>
        /// 那个勾选框的说明（悬停看）。必须写清两件他最在意的事：**这不是设置**、**它会删源包**。
        /// </summary>
        public const string SpaceTightToggleToolTip =
            "盘快满了、普通模式跑不动时开它：程序会按空间自己决定同时跑几个（忽略「全速」；"
            + "你要是把②页的「最大并发解压数」调得更低，就按更低的那个跑），"
            + "每个包一旦解压完、校验通过就立刻永久删除它的源包（不进回收站、不可恢复），"
            + "把空间当场还给后面的包；其余物（过程物）也在任务成功后彻底删除。"
            + "⚠ 它删的是你的源包。这个勾只对本次运行有效，不会写进设置，下次启动是关着的。";

        /// <summary>
        /// ②「解压方式」页 / ③「清理与删除」页上那条**本次被覆盖**的提示（模式开着时才显示）。
        ///
        /// <para>为什么这两页也必须有：那两页正是用户查看"源包怎么处理、其余物怎么删"的地方，
        /// 只在①页说一句，他翻到②③页看到的仍是设置里的档位 —— 就会读成"程序没按我看到的跑"。</para>
        /// </summary>
        public const string SpaceTightOverrideNotice =
            "⚠ 本次开着①页的「空间不足」模式：源包与其余物这两档被自动覆盖为"
            + "「放入其余物 + 彻底删除」、且每个包校验通过后立刻永久删除源包；"
            + "并发由空间决定（「全速」不生效，但你的并发档调得更低时按更低的跑）。"
            + "⛔ 本次不写设置（页面上这些档位仍是你原来的选择，下一批照旧生效）。";

        /// <summary>
        /// 一键处理确认框里那条**红字**（模式开着时才显示）。
        ///
        /// <para>四件事一句不落：覆盖了哪两档、源包**什么时候**被删、删了不可恢复、
        /// 以及**本次不写设置**（用户明确要求"覆盖是运行期的，别动我的设置"）。</para>
        /// </summary>
        public const string SpaceTightConfirmText =
            "⚠ 「空间不足」模式已开：本次会自动覆盖为「源包 → 放入其余物 + 其余物 → 彻底删除」，"
            + "并且每个包一旦定稿 + 校验通过就立刻永久删除它的源包（不进回收站、不可恢复），"
            + "并发改由空间自己决定（「全速」本次不生效；你要是把并发档调得更低就按更低的跑）。"
            + "这些覆盖只对这一次运行有效，不写进设置。";
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
        // 工作区根：默认跟着输出盘（用户 2026-09-24 拍板）
        // ================================================================
        //
        // 用户原话："刚刚我就发现你会讲解压失败的残留放在安装包的位置，这次是小的 5G 左右，
        // 那要是 40G 的东西，解压不小心失败了，你同样会放在安装位置吗"。
        // 定为：默认根 = <输出盘>\.ArchiveFixer.work（既不在源目录里、也不在成品目录里，
        // 不变量 12 照旧）；用户显式设过缓存根目录时**仍以它为准**；拿不到盘才回落程序目录。
        // 名字里的点开头是刻意的：Windows 默认隐藏，用户翻输出盘时不会把它当成自己的东西。

        /// <summary>默认档：本批工作区跟着输出盘走（含"为什么这么定"的一句话）。</summary>
        public const string WorkspaceRootOnOutputDriveFormat =
            "本批工作区在 {0} 盘：{1}（默认跟输出盘走：既不在源目录里、也不在成品目录里；"
            + "暂存与成品同盘，定稿是改名而不是跨盘复制）";

        /// <summary>跨盘时说清"跨了几个盘、工作区固定在哪个盘"——**不许假装它们同盘**。</summary>
        public const string WorkspaceRootCrossDriveFormat =
            "；注意：本批的落点跨 {0} 个盘（{1}），工作区固定在 {2} 盘 —— "
            + "其它盘上的任务解压时中间产物落在这个盘上，定稿那一步是跨盘复制（比同盘改名慢，也不省空间）";

        /// <summary>用户显式设过缓存根目录：以它为准，一个字都不改他的选择。</summary>
        public const string WorkspaceRootConfiguredFormat =
            "工作区 = {0}（设置里显式指定了缓存根目录，以它为准；留空才是「跟着输出盘」那一档）";

        /// <summary>拿不到输出盘 → 回落程序目录（老行为），原因要写清，整批照常开工。</summary>
        public const string WorkspaceRootFallbackFormat =
            "输出盘用不了，工作区回落到程序目录：{0} —— 原因：{1}（老行为；整批照常开工，不会因此开不了工）";

        /// <summary>工作区不在这批的输出盘上（用户设过缓存根目录 / 回落程序目录）：如实说明后果 + 已知限制。</summary>
        public const string WorkspaceNotOnOutputDriveFormat =
            "⚠ 工作区不在这批的输出盘上（工作区在 {0}，落点盘是 {1}）：定稿那一步会是跨盘复制（慢、也不省空间），"
            + "而且空间门只按落点盘核算 —— 工作区那块盘还要另留得下「内容物 + 过程物」，"
            + "这一半不在账面上（已知限制）。想让它跟着输出盘走：把「缓存根目录」留空。";

        /// <summary>空壳工作区（一个文件都没解出来）当场清掉。</summary>
        public const string WorkspaceEmptyShellRemovedFormat =
            "{0}：这次没解出任何东西（工作区里一个文件都没有），空壳工作区已清掉：{1}";

        /// <summary>空壳没清掉（被占用 / 权限）：只写 WARN，绝不因此改任务结论。</summary>
        public const string WorkspaceEmptyShellRemoveFailedFormat =
            "{0}：空壳工作区没清掉（{1}），目录保留：{2}";

        /// <summary>失败 / 取消时工作区里**有东西** → 保留（那是那批唯一解出来的一份）。</summary>
        public const string WorkspaceKeptOnFailureFormat =
            "{0}：任务没成功，工作区里的 {1} 个文件 / {2} 保留在原处（那是这次唯一的一份产物线索；"
            + "要清就在 ③「清理与删除」页点「清理工作区」）：{3}";

        // ================================================================
        // 失败 / 取消不留残留（用户 2026-09-25 第 25 条追加）
        // ================================================================
        //
        // 用户原话："我不希望有这么多的失败残留，还是这么说如果解压 40G，两层，解压失败有 80G 的
        // 卸载残留，用户不得气死，你为什么要弄卸载残留，有一个导出失败列表不就可以了吗，
        // 而且对于用户来说，失败了就失败了，成功了就成功了"。
        //
        // 结论：**默认档 = 没成功就一个中间产物都不留**（任务工作区 + 递归逐层工作区整份删掉），
        // 只留日志与失败清单；要留现场排查的人在 ③ 页打开「失败时保留中间产物」（KeepFailedWorkspace）。
        // 三条红线一个字不动：成功路径的清理口径照旧、源包一个字节都不动、已经定稿搬出去的内容物不受影响。

        /// <summary>没成功（失败 / 取消 / 部分完成）→ 默认把这次的工作区整份清掉，并说清怎么改主意。</summary>
        public const string WorkspaceClearedOnFailureFormat =
            "{0}：已清理工作区：{1} 个文件 / {2}（这次没成功；要留现场请在 ③ 页打开「失败时保留中间产物」）：{3}";

        /// <summary>没成功的清理没删掉（被占用 / 权限不足）：只写 WARN + 说清路径，绝不改任务结论。</summary>
        public const string WorkspaceClearOnFailureFailedFormat =
            "{0}：工作区没清掉（{1}），目录保留：{2}";

        /// <summary>③ 页「工作区残留」那一组里的开关标题。</summary>
        public const string SettingsKeepFailedWorkspaceLabel = "失败时保留中间产物（排查用）";

        /// <summary>它的说明：默认关 = 失败/取消一个中间产物都不留；打开才留，而且能在本页清理。</summary>
        public const string SettingsKeepFailedWorkspaceHint =
            "默认关：失败 / 取消 / 部分完成的任务不留任何中间产物（暂存目录、抠出来的内嵌归档副本、"
            + "已解出的过程物一起删掉），只留日志与失败清单。打开之后才留着便于排查 —— "
            + "那些目录会出现在本页上面那一行里，可以随时清理。"
            + "⚠ 两条不受它影响：成功路径照旧「成功且校验通过才清」，源包在任何情况下都一个字节不动。";

        /// <summary>启动 / 刷新日志里那句"这些根都扫过了"（默认跟输出盘之后根会变，位置必须写全）。</summary>
        public const string WorkspaceScannedRootsLogFormat = "本次扫描的工作区根目录：{0}";

        /// <summary>
        /// ⑥设置页「缓存根目录」下面那句说明（留空 = 跟输出盘，而不是跟程序目录）。
        ///
        /// <para>⚠ 这句话会经 <c>x:Static</c> 直接绑到 XAML 上，所以**不许出现尖括号**
        /// （XML 属性值里的 <c>&lt;</c> 不是良构字符）—— 要写"输出盘"就直接写中文。</para>
        /// </summary>
        public const string SettingsCacheRootHint =
            "留空（默认）= 工作区跟着输出盘走：在输出盘的根目录下建一个 .ArchiveFixer.work"
            + "（点开头，Windows 默认隐藏）—— 中间产物不再堆在程序盘上，定稿也是同盘改名（快、不占双份）。"
            + "日志 / 临时 / 设置仍在程序目录下的 data。填了就以它为准（工作区 = 它下面的 work 子目录）；"
            + "缓存不能落 C 盘：填了系统盘会拒绝保存并说明原因。";

        /// <summary>⑥设置页「打开工作区目录」按钮的提示（默认跟输出盘，所以要说清它会开到哪）。</summary>
        public const string SettingsWorkDirectoryButtonHint =
            "默认跟着输出盘：输出盘根目录下的 .ArchiveFixer.work（点开头，默认隐藏）。"
            + "失败 / 取消留下的中间产物在这里（③「清理与删除」页可以看体积并清理）。";

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

        /// <summary>
        /// 紧跟其后的一句：为什么不自动删、去哪儿清。
        ///
        /// <para>⚠ 2026-09-25 第 25 条之后，"怎么会留下东西"只有两种可能（默认档一个都不留），
        /// 这句话必须把两种都说出来 —— 否则用户会以为"程序明明说失败不留残留，怎么又冒出来了"。</para>
        /// </summary>
        public const string WorkspaceLeftoverHintLog =
            "程序不会自动删它们（只可能是这两种来路：打开了「失败时保留中间产物」，"
            + "或者上一次被强杀 / 断电来不及收尾）；要清就在「清理与删除」页点「清理工作区」，删前会再确认一次。";

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
        // ①「任务」页的「输出位置」那一格（用户 2026-09-25 第 27 条）
        // ================================================================
        //
        // 用户原话："输出的指定位置可以放在主界面进行选择，这个没有问题，选项卡里面的也可以留着，
        // 在添加文件夹和全选中间还有那么多的位置，如果选择解压到位置名字太长，可以简写……
        // 反正我要求的就是我们最好能够看到完整的解压地址"。
        //
        // 落点：①页主操作条「添加文件夹」与「全选」之间那一格（Grid 的星号列），显示 + 选择 + 一个开关。
        // 真值只有一份：MainViewModel.SelectedOutputDirectory（= Settings.CustomOutputDirectory）
        // 与 Settings.ExtractToOriginalDirectory —— 与②「解压方式」页是同一个值，两边同时变。
        // 长路径中间省略（Helpers/PathMiddleEllipsis），**完整路径在 ToolTip 与右键菜单里**。

        /// <summary>那一格的前缀。</summary>
        public const string OutputLocationLabel = "输出位置：";

        /// <summary>选位置按钮（与②页那个「选择」同一个动作、同一份值）。</summary>
        public const string OutputLocationChooseButtonText = "选择…";

        /// <summary>开关：勾上 = 不指定统一位置（产物落在每个包自己所在的目录）。</summary>
        public const string OutputLocationFollowsArchiveLabel = "未指定位置";

        /// <summary>它的提示（说清两件事：落在哪、与②页是同一个设置项）。</summary>
        public const string OutputLocationFollowsArchiveHint =
            "勾上 = 产物落在每个包自己所在的目录（与②「解压方式」页的落点是同一个设置项）。"
            + "取消勾选后点「选择…」挑一个统一的位置；两处改哪一处，另一处立刻跟着变。";

        /// <summary>未指定位置时那一行的显示（不是空白，必须说清东西会落在哪）。</summary>
        public const string OutputLocationUnspecifiedText = "（未指定：产物落在每个包自己的目录）";

        /// <summary>取消勾选但还没挑目录时的显示（说清下一步点哪里）。</summary>
        public const string OutputLocationNotChosenText = "（还没选位置 —— 点「选择…」挑一个目录）";

        /// <summary>完整路径的 ToolTip（界面上显示的是中间省略过的，这里给全文）。</summary>
        public const string OutputLocationToolTipFormat = "完整路径：{0}";

        /// <summary>右键菜单：把完整路径（不省略）复制走。</summary>
        public const string OutputLocationCopyMenuText = "复制完整路径";

        /// <summary>复制成功 / 没有可复制内容的两条日志。</summary>
        public const string OutputLocationCopyLogFormat = "已复制输出位置：{0}";

        /// <summary>没有指定位置时点复制：如实说清，不假装复制成功。</summary>
        public const string OutputLocationCopyNothingLog = "现在是「未指定位置」，没有可复制的输出路径。";

        /// <summary>①页那个开关打开时的一条日志（说清东西会落在哪，不让人自己猜）。</summary>
        public const string OutputLocationSwitchedToOriginalLog =
            "输出位置：未指定 —— 产物落在每个包自己所在的目录。";

        /// <summary>①页那个开关关掉时的一条日志（提醒下一步点哪里，否则落点会变成"没填路径的指定位置"）。</summary>
        public const string OutputLocationSwitchedToCustomLog =
            "输出位置：改为指定位置（请点「选择…」挑一个目录）。";

        // ================================================================
        // 手动档「解压到当前文件夹」（用户 2026-09-27 拍板）
        // ================================================================
        //
        // 用户原话："再加一个解压到当前文件夹的功能（WinRAR 右键那种）"。
        // 语义 = `111\222.rar` → `111\内容物`：**不建包名那一层**，内容物直接落在源包所在的那个目录。
        //
        // ⚠ 三条边界（措辞在这里，判据在 ExtractionCoordinator._extractIntoSourceFolderThisRun）：
        //    ① **只有这一个按钮**会摊平 —— 一键处理 / 继续解 / 批量一律套包名那一层（用户红线）；
        //    ② 摊平更容易撞名，同名照走当前冲突档（用户明确接受这个代价，不是我们替他兜的底）；
        //    ③ 它是个动作而不是设置项：不进 appsettings.json，关掉程序就没了。

        /// <summary>①页「手动操作」里那个按钮的文案（与 WinRAR 右键同一句话，用户点名要这个语义）。</summary>
        public const string ExtractIntoSourceFolderText = "解压到当前文件夹";

        /// <summary>按钮的 ToolTip：一句话说清它会少一层、以及撞名怎么办。</summary>
        public const string ExtractIntoSourceFolderToolTip =
            "只对这次点击生效：内容物不建包名那一层，直接落在压缩包自己所在的那个文件夹里"
            + "（111\\222.rar → 111\\内容物）。同名文件 / 文件夹按当前的「解压覆盖策略」处理。"
            + "一键处理不会这么做 —— 那条路永远套一层包名文件夹。";

        /// <summary>点了按钮之后写进日志的那条提醒（事后能回答"这批为什么没有包名那一层"）。</summary>
        public const string ExtractIntoSourceFolderReminderLog =
            "本次按「解压到当前文件夹」执行：内容物不建包名那一层，直接落在源包所在目录"
            + "（111\\222.rar → 111\\内容物）；同名冲突按当前覆盖策略处理。"
            + "这个选择只对本次点击有效，没有写进设置。";

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
            + "（本程序只按文件名 + 魔数判了个大概，不保证它们真的没用）：";

        /// <summary>最多列这么多条（其余只报个数）。</summary>
        public const string ImportJunkReminderCountFormat = "这次一共认出 {0} 个：";

        /// <summary>还有多少个没列出来。</summary>
        public const string ImportJunkReminderMoreFormat = "  …还有 {0} 个（最多列 10 条）";

        /// <summary>撞到扫描上限时如实说明。</summary>
        public const string ImportJunkReminderTruncatedNote = "  （文件太多，本次只核对了前一部分）";

        /// <summary>收尾：程序不动它们 + "从列表里移除"动的只是列表。</summary>
        public const string ImportJunkReminderFooter =
            "本程序对上面这些文件一个都不会动（不删、不改名、不搬走）；"
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

        /// <summary>
        /// 一键处理开工前**自动**把无用物从列表里移掉（用户 2026-09-28：「1.要」）。
        /// <c>{0}</c> = 移掉几个，<c>{1}</c> = 前几个名字（最多 5 个）。
        /// </summary>
        public const string OneClickJunkRemovedLogFormat =
            "一键处理：先把 {0} 个无用物从列表里移掉（它们对解压没用；源文件一个字节都没动）：{1}";

        /// <summary>列表里手动移除任务（右键 / 「移除勾选的」）。</summary>
        public const string RemoveTasksLogFormat = "已从任务列表里移除 {0} 个（源文件一个字节都没动）。";

        /// <summary>一个都没勾时点「移除勾选的」。</summary>
        public const string RemoveCheckedTasksNoneText = "没有勾选任何任务，没有可移除的。";

        /// <summary>③「清理与删除」页那个开关的文案（第 15 条：导入后就提醒无用物）。</summary>
        public const string SettingsRemindJunkAfterImportLabel = "导入文件夹后提醒一次「疑似无用物」";

        /// <summary>它的说明（默认开；关掉 = 导入后一次都不提醒，判据与扫描都不跑）。</summary>
        public const string SettingsRemindJunkAfterImportHint =
            "默认开：每次选完文件夹就扫一遍源目录，把打包者常带的说明 / 网址 / 工具列出来，"
            + "并可以一键把它们从任务列表里去掉（程序对这些文件一个都不会动：不删、不改名、不搬走）。"
            + "关掉 = 导入后完全不提醒。";

        /// <summary>③ 页「工作区残留」那一组的标题。</summary>
        public const string WorkspaceLeftoverGroupHeader = "工作区残留（失败 / 取消留下的中间产物）";

        /// <summary>③ 页「工作区残留」那一组的说明（为什么默认一个都不留、什么情况下才会看见东西）。</summary>
        public const string WorkspaceLeftoverGroupHint =
            "默认档下只有两种情况会留下东西：① 你在下面打开了「失败时保留中间产物」（排查用）；"
            + "② 程序被强杀 / 断电，来不及收尾。这里列出它们占了多少空间，确认之后可以一次清掉 —— "
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
        /// ⛔ 2026-09-26 第 46 条：这里原来的 <c>PackNeedRar</c>（"这一步需要本机已安装 WinRAR"）
        /// 与 <c>PackThreeWaysOut</c>（三条出路，让用户自己去⑤页换容器）**两条都已删除** ——
        /// 没装 WinRAR 时打包会**自动改用 7z 外层**，而"外层容器"那个控件也已经不在界面上了。
        /// 现在"没有 Rar.exe"那句话的唯一来源是 <c>ToolLocator.DescribeNoRarAvailable()</c>
        /// （⑤页状态格 / 日志 / 失败原因都引它）。
        /// </summary>

        // ── 外层容器三选一（用户 2026-09-23 决定；界面、日志、失败原因共用同一份说法） ──

        /// <summary>外层容器 = rar 的完整说法（含许可边界）。</summary>
        public const string PackOuterRarText =
            "外层容器 rar：把那个装分卷的文件夹压成一个带密码的 .rar（需要本机已安装的 WinRAR：程序只检测与调用它，绝不随包分发）";

        /// <summary>外层容器 = 7z 的完整说法（**无需额外安装**这句是关键信息）。</summary>
        public const string PackOuterSevenZipText =
            "外层容器 7z：把那个装分卷的文件夹压成一个带密码的 .7z（-mhe 连文件名一起加密）—— 无需额外安装（7-Zip 是 LGPL，随程序分发）";

        /// <summary>外层容器 = 不做。</summary>
        public const string PackOuterNoneText =
            "不做外层容器：结果就是那个装分卷的文件夹里的 7z 加密分卷（不需要 Rar.exe，也不需要多一份空间）";

        /// <summary>外层容器 rar 的短名（日志 / 摘要行）。</summary>
        public const string PackOuterRarShort = "rar";

        /// <summary>外层容器 7z 的短名。</summary>
        public const string PackOuterSevenZipShort = "7z";

        /// <summary>不做外层的短名。</summary>
        public const string PackOuterNoneShort = "不做外层";

        /// <summary>结果区提醒：那个装分卷的文件夹还在时，用户可以自己看、也可以自己删。</summary>
        public const string PackKeepFolderHint =
            "「其余物」（装 7z 分卷的那个文件夹）按你选的档留着了 —— 可以先检查分卷，确认没问题之后自己删掉它就行。";

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
            "提醒：写回是追加到密码本末尾，而尝试顺序是「空密码 → 最近成功 → 映射式命中 → 统一密码 → 密码列表」。"
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
            "这条手动添加的密码，值不在任何一本已记住的密码本里 —— 列表按本机加密保存，"
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

        // ── 特定解压（用户 2026-09-24 拍板：①页一个开关 + ②页**可扩展**的一栏） ──
        //
        // 用户原话："我是想在一键解压旁边弄一个特定解压开关，这样要弄特定解压你就在选项卡里面有解压方式，
        // 在解压方式里面就可以去添加一个特定解压这一栏，也就是以后可能会经常加的东西，
        // 因为每个人的特定的解压方式不同"。
        //
        // ⚠ 这一组是「特定解压」的**唯一措辞来源**（AGENTS.md §7）。三条纪律：
        //    ① 规则自己的名称与说明由注册表给（<c>Extraction/SpecialExtractionRules.cs</c>），
        //       这里只放**界面骨架**（标题 / 开场白 / 优先级 / ①页 ToolTip / 日志与 WARN 模板）；
        //    ② 界面上不写规则清单，一律按注册表渲染 —— 以后加规则不动 XAML；
        //    ③ "关掉全部 = 与现在完全一样"这句必须出现在界面上（用户最恨"我以为它按默认跑的"）。

        /// <summary>功能名（①页开关、②页分组标题、日志里都用它）。</summary>
        public const string SpecialExtractionName = "特定解压";

        /// <summary>①页「一键处理」旁边那个总开关的文案。</summary>
        public const string SpecialExtractionToggleLabel = "特定解压";

        /// <summary>①页开关的 ToolTip：没有启用任何规则时显示它（指路去②页）。</summary>
        public const string SpecialExtractionNoRuleHint =
            "还没有开任何特定解压规则 —— 去②「解压方式」页的「特定解压」那一栏里挑一条。";

        /// <summary>①页开关的 ToolTip：列出现在会生效的规则（措辞与②页同一份）。</summary>
        public const string SpecialExtractionToolTipFormat =
            "特定解压：{0}。规则在②「解压方式」页里挑；关掉 = 与现在完全一样（判定表照旧套那一层）。";

        /// <summary>②页那一栏的标题。</summary>
        public const string SpecialExtractionRulesGroupHeader = "特定解压（可以往上加的规则）";

        /// <summary>②页那一栏的开场白（用户要的那句"可加的"）。</summary>
        public const string SpecialExtractionRulesIntro =
            "这些是可加的特殊解压方式：开哪条就按哪条跑（关掉全部 = 与现在完全一样）。"
            + "要它生效，先把①「任务」页「一键处理」旁边的「特定解压」总开关打开。";

        /// <summary>②页那一栏里"它与终端落法谁优先"的那句话（用户点名要写明优先级）。</summary>
        public const string SpecialExtractionPriorityHint =
            "与上面「内容物最后那一层（终端落法）」的优先级：这里的规则优先。"
            + "规则生效时，判定表里要套的那一层不再套，内容物直接落在成品目录里（222\\1111\\内容物）；"
            + "规则不生效（总开关关着 / 规则没开 / 包内不止一个文件夹）时才按终端落法走。";

        /// <summary>确认框正文里那一行（开了特定解压才出现）。</summary>
        public const string SpecialExtractionConfirmLabel = "特定解压：";

        /// <summary>任务详情 / 失败清单里的那一行（<c>ArchiveTask.RunOptionsNote</c>）。</summary>
        public const string SpecialExtractionNoteFormat = "特定解压：{0}";

        /// <summary>日志：本次按哪几条特定规则跑（一条都不开时不写）。</summary>
        public const string SpecialExtractionAppliedLogFormat = "{0}：特定解压 —— {1}";

        /// <summary>定稿结论：这一单真的按规则塌了那一层（说清落在哪）。</summary>
        public const string SpecialExtractionAppliedSummaryFormat =
            "特定解压「{0}」生效：不再套那一层，内容物直接落在 {1}";

        /// <summary>
        /// 不塌的理由之一：多个包共用同一个成品目录（"添加文件夹 + 指定位置"那一档）。
        ///
        /// <para>
        /// 为什么不塌：那一档下"包名那一层"<b>就是</b>判定表套出来的那一层，
        /// 不套它等于把好几个包的内容物倒进同一个目录（用户最反感的"东西挤在一起"）。
        /// 保守档 + 一条 WARN，**绝不静默**。
        /// </para>
        /// </summary>
        public const string SpecialExtractionSkippedSharedRootFormat =
            "特定解压「{0}」这次没按它走：本批有多个包共用同一个成品目录（{1}），"
            + "去掉那一层会让几个包的内容物混在一起 —— 按原判定表套一层（保守档，不是失败）。";

        /// <summary>不塌的理由之二：包内有多个并列文件夹 / 多个分支（不是一条单链）。</summary>
        public const string SpecialExtractionSkippedBranchFormat =
            "特定解压「{0}」这次没按它走：包内不是一个内容文件夹，而是 {1} 个并列的文件夹（{2}）—— "
            + "再去掉一层就分不清哪个才是内容物 —— 按原判定表套一层（保守档，不是失败）。";

        /// <summary>规则不适用时的兜底理由（没有并列文件夹可数，例如内容物是单个文件）。</summary>
        public const string SpecialExtractionSkippedNoContentReason =
            "这一单没有可塌的那一层（内容物本来就直接落在成品目录里）";

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
            + "Rar.exe / WinRAR.exe 是共享软件：程序只检测与调用你自己装的那一份，绝不复制、绝不随包分发，" + "\r\n"
            + "也绝不会拿 7-Zip 假装做出 .rar。";

        // ── 「按建议改名并重试」（用户 2026-09-25 第 41 条） ──

        /// <summary>
        /// 「按建议改名并重试」按钮的说明（XAML 里那个 ToolTip 的内容来源）。
        ///
        /// <para>为什么要把"只改名字、内容不动"写进提示：这个按钮会**动用户的源文件名**
        /// （不变量 1 的唯一例外是用户显式发起），必须一眼就能看清它到底做什么、不做什么。</para>
        /// </summary>
        public const string VolumeRepairButtonToolTip =
            "只对「名字被改坏的分卷第一卷」有效（例如 set.7z(删掉.001）：按程序给出的标准名把这一卷改名" +
            "（只改文件名，内容一个字节都不动），然后立刻重新识别并重试解压。\n" +
            "灰着 = 勾选的任务里没有这种卷（判据是文件系统事实：名字带卷号、是第 1 卷、同目录有后续卷、" +
            "推得出标准名、目标名没被占）。扫完就判，不用先跑一次解压。";

        /// <summary>一个可改名的任务都没有时点它的提示。</summary>
        public const string VolumeRepairNoneText =
            "勾选的任务里没有「名字被改坏的分卷第一卷」—— 没有可改名的。\n" +
            "（判据：名字带卷号且是第 1 卷、同目录里能找到后续卷、能从后续卷推出标准名、" +
            "现在的名字确实不是标准名、目标名没被占。想先看一眼诊断：日志里每次扫描都会写这一档。）";

        /// <summary>确认框标题。</summary>
        public const string VolumeRepairConfirmTitle = "修复分卷名并重试";

        /// <summary>确认框正文（<c>{0}</c> = 逐条 `旧名 → 新名`）。</summary>
        public const string VolumeRepairConfirmBodyFormat =
            "将要重命名下面这些文件（只改名字，内容一个字节都不动）：\n\n" +
            "{0}\n\n" +
            "改完程序会立刻重新识别它们并重试解压。源包操作与删除操作仍然按③页的档位走（默认什么都不搬、不删）。";

        /// <summary>改名成功（写进日志 / 状态栏）。</summary>
        public const string VolumeRepairDoneFormat = "已按建议改名：{0} → {1}";

        /// <summary>整组改名成功（网盘给每卷缀了垃圾那种）。<c>{0}</c>/<c>{1}</c> = 第一卷一旧一新，<c>{2}</c> = 一共改了几卷。</summary>
        public const string VolumeRepairGroupDoneFormat = "已按建议改名（整组 {2} 卷）：{0} → {1} 等";

        /// <summary>整组改到一半失败。<c>{0}</c> = 已经改好的卷数，<c>{1}</c> = 卡住的原因。</summary>
        public const string VolumeRepairGroupPartialFormat = "整组改名只完成了 {0} 卷就停下了（已改的那几卷不会再动）：{1}";

        /// <summary>改名失败（日志 / 弹窗）。<c>{0}</c> = 目标名，<c>{1}</c> = 原因。</summary>
        public const string VolumeRepairRenameFailedFormat = "改名失败（目标名「{0}」）：{1}";

        /// <summary>算计划时的意外（读不到文件属性等）。<c>{0}</c> = 原因。</summary>
        public const string VolumeRepairPlanFailedFormat = "读不到这一卷的信息：{0}";

        /// <summary>不能改名的原因：源文件不在了。</summary>
        public const string VolumeRepairSourceMissing = "源文件不在了（可能已被移动、改名或删除）";

        /// <summary>不能改名的原因：名字里没有卷号。</summary>
        public const string VolumeRepairNotAVolumeName = "这个名字里没有卷号，程序不敢替它猜一个标准名";

        /// <summary>不能改名的原因：不是第一卷。</summary>
        public const string VolumeRepairNotFirstVolume = "它不是这一组的第 1 卷（只有第 1 卷的名字被改坏时，改名才有用）";

        /// <summary>不能改名的原因：同目录没有像后续卷的文件。</summary>
        public const string VolumeRepairNoSiblings = "同目录里没有找到像后续卷的文件（先把后续卷放回来，改名才凑得齐一组）";

        /// <summary>不能改名的原因：推不出标准名。</summary>
        public const string VolumeRepairNoSuggestion = "从后续卷的名字推不出这一组的标准名（宁可不说，也不给一个错的建议）";

        /// <summary>不能改名 / 不需要改名的原因：名字本来就是标准的。</summary>
        public const string VolumeRepairAlreadyStandard = "它的名字本来就是标准的，问题不在名字上";

        /// <summary>不能改名的原因：目标名被占用。<c>{0}</c> = 目标名。</summary>
        public const string VolumeRepairTargetTakenFormat = "目标名「{0}」已经被别的文件占用了，程序不会覆盖它";

        /// <summary>改名批次收尾（写进日志）。<c>{0}</c> = 成功数，<c>{1}</c> = 失败数。</summary>
        public const string VolumeRepairBatchDoneFormat = "按建议改名：成功 {0} 个、失败 {1} 个；接着重试解压勾选的任务。";

        // ── 「容器里装的是分卷第一卷」（用户 2026-09-25 第 42 条：探针查出来的实话） ──

        /// <summary>
        /// 内嵌归档 + 引擎报「分卷缺失」时补的一句事实。
        ///
        /// <para>为什么必须补：真 7z 实测（`封面.jpg` 里装着 `set.7z.001`、后续卷在外面）时，
        /// 引擎报的原文是"这是分卷压缩包的后续卷，缺少首卷"——**与事实正好相反**：
        /// 首卷就在我们手上（刚从容器里抠出来），缺的是"名字对得上的后续卷"。
        /// 用户拿着那句话只会去满盘找首卷，而首卷根本不在任何文件里。</para>
        ///
        /// <para>⚠ 2026-09-25 第 42 条方案 A 落地后这句话改了后半段：程序**已经能**自动把两边接起来
        /// （②页那个开关，默认关）。所以这里不再说"程序不会替你凑"，而是说清"怎么让它替你凑"。</para>
        ///
        /// <para><c>{0}</c> = <see cref="Detection.OrphanVolumeSet.Describe"/>，
        /// <c>{1}</c> = 容器的文件名。</para>
        /// </summary>
        public const string EmbeddedFirstVolumeHintFormat =
            "更正引擎那一句：它说「这是后续卷、缺少首卷」——说反了。这个容器里装着的就是那一组分卷的第 1 卷" +
            "（已按偏移取出），缺首卷的是外面那一组：{0}。引擎是按文件名去找同一组的其他卷的，" +
            "抠出来的那一段既不在那个目录、名字也不带卷号，所以它找不到。" +
            "要让程序自己把两边接起来：②页打开「容器里装的是分卷第 1 卷时，自动接上同目录的后续卷」" +
            "（工作区里接名字；同一块盘不复制字节，跨盘才复制且先查空间），再重跑这一单；" +
            "也可以自己把首卷弄出来、按标准名与后续卷放进同一个目录（{2}）。";

        /// <summary>日志里那一行的前缀（免得整段塞进失败清单时看不出是补充说明）。</summary>
        public const string EmbeddedFirstVolumeHintLogFormat =
            "说清一句：{0} 里装的是这一组分卷的第 1 卷，缺首卷的是外面那一组（{1}）—— 详见失败原因。";

        /// <summary>
        /// ②页那个「自动接上同目录的后续卷」开关**已经打开**时，在失败原因末尾补的一句。
        ///
        /// <para>为什么必须补：开关打开之后上面那句建议就变成了错的（"让它替你接"——它已经试过了）。
        /// 失败原因里的每一句都要与"当前这一档到底做了什么"一致，否则用户会反复点重跑。</para>
        /// </summary>
        public const string EmbeddedFirstVolumeHintAssemblyTriedSuffix =
            "（②页那个开关已经打开，所以这一次程序已经试过自动拼装 —— 没成的原因写在日志里那几条 WARN 上；" +
            "反复重跑不会有不同结果，除非先解决它说的那个原因。）";

        // ── 日志瘦身（用户 2026-09-25 第 44 条："一次导出 713KB，这个多吓人"） ──

        /// <summary>
        /// 其余物处理的**逐任务一行**：<c>{0}</c> = 动作（彻底删除 / 移入回收站），
        /// <c>{1}</c> = 短路径，<c>{2}</c> = 条目数，<c>{3}</c> = 释放大小。
        ///
        /// <para>⛔ 这里**不许**再堆理由与"不可逆"的长文案：那样 68 个包就是 400 多行、7 万字符。
        /// 理由与"不可逆"在**批首一条** + ③页那条常驻红提示里已经说过一遍了。</para>
        /// </summary>
        public const string RestPurgedCompactFormat = "{0}：{1}（{2} 项 / {3}）";

        /// <summary>批末的其余物汇总（一条顶掉几十行）：<c>{0}</c> = 任务数，<c>{1}</c> = 合计大小。</summary>
        public const string RestPurgedBatchSummaryFormat = "本批其余物已处理：{0} 个任务，合计 {1}。";

        /// <summary>其余物动作：彻底删除（逐任务一行里的动作名）。</summary>
        public const string RestActionDelete = "彻底删除";

        /// <summary>其余物动作：移入回收站（逐任务一行里的动作名）。</summary>
        public const string RestActionRecycleBin = "移入回收站";

        /// <summary>
        /// 「其余物保留」的**逐任务一行**：<c>{0}</c> = 任务名，<c>{1}</c> = 短路径，<c>{2}</c> = 条目数。
        ///
        /// <para>⛔ 这里不许再带"去哪儿改档位"那段说明 —— 那句话批首已经说过一遍了（第 44 条）。</para>
        /// </summary>
        public const string RestKeptCompactFormat = "{0}：其余物保留：{1}（{2} 项）";
    }
}
