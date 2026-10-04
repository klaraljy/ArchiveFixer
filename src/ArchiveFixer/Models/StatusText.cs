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

        /// <summary>
        /// **两种可能同时成立**：密码可能不对，包的数据也可能是坏的。
        ///
        /// <para>为什么要单独一类（用户 2026-09-30 真机）：RAR 1.5–4.x 的 <c>-p</c> 包在密码不对时，
        /// UnRAR 只打一句 <c>Checksum error in the encrypted file X. Corrupt file or wrong password.</c>
        /// （中文版：「在加密文件 X 里校验和错误。文件已损坏或密码错误。」、退出码 3）——
        /// 这一句话里**同时**给了两种可能，引擎自己也分不开。以前它落到 <see cref="Corrupted"/>，
        /// 于是①程序当场不再试其余密码候选（"换密码候选没有帮助"），11 个候选只试了第 1 个；
        /// ②结论断言"文件损坏"，把用户指去重新下载。翻过来判成 <see cref="WrongPassword"/> 同样错 ——
        /// 那会把真损坏说成"密码错"，用户会反复核对根本没写错的密码本。
        /// 所以结论必须是**两义**：状态写两种可能，原因里必须带引擎原话。</para>
        ///
        /// <para>失败类状态：配色与统计必须与既有失败口径一致（见 <c>StatusToBrushConverter</c> 的错误色、
        /// <c>TaskSummaryService</c> 的失败分桶、<c>TaskOutcomeClassifier</c> 的失败名单、
        /// <c>BatchSummaryDiagnosticsRules</c> 的归组）—— 按 AGENTS.md §7 同改。</para>
        /// </summary>
        public const string PasswordOrCorrupted = "密码错误或文件损坏";

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

        /// <summary>
        /// 「移除勾选的」那颗按钮的说明 —— 用法与"它什么时候是灰的"都要写明（只动列表，磁盘一个字节都不动）。
        ///
        /// <para>用户 2026-09-29 两次反馈之后重写：① 空列表 / 一个都没勾时它不该亮（按钮现在跟着
        /// <c>MainViewModel.CanRemoveSelectedTasks</c> 灰下去）；② 它灰下去的时候必须还答得出"为什么"，
        /// 所以「先在列表里勾上要移除的那几行」这句是给禁用态看的（那颗按钮带 <c>ShowOnDisabled</c>）。</para>
        ///
        /// <para>⚠ 但"灰"只有"没有可移除的对象"这一个原因：**跑批中途照样能点**（用户 2026-09-27 原话
        /// "我就是修改列表删除东西，和正在处理有什么关系"）—— 这句话里写着"正在处理时也是灰的"的那一版
        /// 是错口径，⛔ 不许写回去。</para>
        /// </summary>
        public const string RemoveCheckedTasksHint =
            "把勾选的任务从列表里去掉：先在列表里勾上要移除的那几行，再点这里。"
            + "没有导入文件、或者一个都没勾时它是灰的（没有可移除的对象）；正在跑批时照样能点，"
            + "它是纯列表操作，和正在处理的那一批没有关系。"
            + "只动列表：源文件、输出目录、日志一个字节都不动（移除错了再「添加文件 / 添加文件夹」一次就回来）。";

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

        /// <summary>
        /// 识别阶段从**RAR 头结构**读出来的加密结论后缀（用户 2026-09-29 任务 A）：
        /// 只有数据加密（WinRAR 的 <c>-p</c>），文件名与头仍然可见。
        ///
        /// <para>拼在识别消息后面（<c>识别为 RAR4 压缩包（文件数据已加密）</c>），
        /// 让"这一包为什么被算进'需要密码'"在任务详情里看得见 —— 判据出处见
        /// <c>Detection/RarEncryptionReader</c>。</para>
        /// </summary>
        public const string DetectRarDataEncryptedNote = "（文件数据已加密）";

        /// <summary>
        /// 识别阶段从 RAR 头结构读出来的加密结论后缀：**连文件名与头一起加密**（WinRAR 的 <c>-hp</c>）——
        /// 这种包连条目名都列不出来，只有正确密码才有下一步。
        /// </summary>
        public const string DetectRarHeadersEncryptedNote = "（文件名与头已加密）";

        /// <summary>
        /// 识别阶段从 **ZIP 中央目录**读出来的加密结论后缀（用户 2026-09-30）：
        /// 至少有一个条目的通用位标志 bit0 置位（ZipCrypto 与 AES 都置这一位，实测
        /// <c>zc.zip</c> = flags <c>0x0001</c>/method 0、<c>aes.zip</c> = flags <c>0x0001</c>/method 99）。
        ///
        /// <para>措辞刻意说"条目"而不是"整包"：ZIP 允许**逐个条目**加密，
        /// 只加密其中几个（实测 <c>mixed.zip</c> 的中央目录 flags = <c>0x0001 0x0000 0x0001</c>）
        /// 也是常态，说成"整包加密"是过度断言。判据出处见 <c>Detection/ZipEncryptionReader</c>。</para>
        /// </summary>
        public const string DetectZipEncryptedNote = "（有加密的条目）";

        /// <summary>
        /// 识别阶段从 **7z 明文头**读出来的加密结论后缀：只有文件数据加密（7-Zip 的 <c>-p</c>）——
        /// 文件名与头照样看得见。判据出处见 <c>Detection/SevenZipEncryptionReader</c>。
        /// </summary>
        public const string DetectSevenZipDataEncryptedNote = "（文件数据已加密）";

        /// <summary>
        /// 识别阶段从 **7z 编码头**读出来的加密结论后缀：**连头一起加密**（7-Zip 的 <c>-mhe</c> / <c>-hp</c>）——
        /// 这种包连条目名都列不出来（<c>7z l -slt</c> 一条都不报），只有正确密码才有下一步。
        /// </summary>
        public const string DetectSevenZipHeadersEncryptedNote = "（文件名与头已加密）";

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
        public const string OneClickConfirmExpanderHeader = "本次改一下（落点 / 源包操作 / 删除操作）";

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

        /// <summary>
        /// **多层解压可能中途空间不足**那句提醒（用户 2026-09-29 第 2 条）。
        ///
        /// <para>他原话的意思是："好是可以提醒用户打开空间不足，坏是中途报错" ——
        /// 所以这句话必须在**动手之前**就看得见（批首日志 + 唯一那个确认框），而不是等跑到一半再说。</para>
        ///
        /// <para>三个数都来自**现有判据口径**（<c>Σ 任务 FreeSpaceDemandBytes</c> 与排计划时的可用空间，
        /// 见 <c>MultiLayerSpaceRiskRules</c>）：本批要新写多少、现在可用多少、差多少。
        /// ⛔ 不写"峰值"（那个数含源包，会让人以为盘上要放两份东西）。</para>
        ///
        /// <para>⚠ 措辞带"可能"：这只是**按需求之和**做的提醒，实际跑起来前面的包收尾会回收空间，
        /// 不一定真的撞上 —— 但它撞上时不会问用户任何问题（那一单不启动、整批照跑），
        /// 所以事先说一句才是对的做法。</para>
        /// </summary>
        public const string MultiLayerSpaceRiskFormat =
            "⚠ 本批最多解 {0} 层，而各包加起来要从可用空间里新写 {1}（已含一层内层展开的增量），"
            + "目标盘现在可用 {2}，差 {3} —— 多层解压可能中途空间不足（撞上时那个包不启动，整批照常往下跑）。"
            + "想更稳：先清理「其余物」腾空间 / 换一个空间更大的输出盘 / 开①页的「空间不足」模式"
            + "（边解边回收源包，⚠ 会永久删除源包）。";

        /// <summary>
        /// 中途真的撞上空间不足时那一条**纯提示**（用户 2026-09-29 第 2 条）。
        ///
        /// <para>一键档的红线是"批中间不许要用户点一下才能继续"，所以它是**非模态**窗口、
        /// 一个按钮、同一批只弹一次（后面的同类情况只写日志，批末汇总里给完整清单）。</para>
        /// </summary>
        public const string SpaceBlockedNoticeFormat =
            "有 {0} 个任务因为磁盘空间不足「没有解压」（例如 {1}：需要 {2}，当时可用 {3}，差 {4}）。"
            + "这些任务一个字节都没动，后面的任务照常跑，本批不会再弹这个提示（跑完的汇总与日志里有完整清单）。"
            + "想腾空间，从这几条里挑一条：清理「其余物」/ 换一个空间更大的输出盘 / "
            + "开①页的「空间不足」模式（每解完一层立刻回收那一层的源包，⚠ 会永久删除源包）。";

        /// <summary>
        /// 空间门**放行**那一行的账面口径（用户 2026-10-02 真机）。
        ///
        /// <para>现场：同一分钟里「空间门通过（精确）…… 可用 31.85 GiB」与空间趋势的
        /// 「空间变化：目标盘 可用 26.91 GiB」差了 5 GiB —— 因为前者那个数是**账本上的数**
        /// （批首那一针，之后只在"有任务在等空间"时才重探），不是此刻的真实可用。
        /// 用户按日志读会以为程序把盘算错了（"少算 6 GB"那一条就是这么读出来的）。</para>
        ///
        /// <para>⛔ 只改措辞：判据、比较、数字来源一个字都没动。这一档由
        /// <c>SpaceReservationLedger</c>（账本）发起；打包那条路是**刚刚探到的**，
        /// 照旧用 <see cref="SpaceGatePassedFormat"/>。</para>
        ///
        /// <c>{0}</c> = 需要多少，<c>{1}</c> = 账面可用，<c>{2}</c> = 余下，<c>{3}</c> = 要保留的余量。
        /// </summary>
        public const string SpaceGatePassedBookValueFormat =
            "空间门通过：需要 {0}，账面可用 {1}"
            + "（这是本次判断用的账面数字，不是此刻的真实可用 —— 实时可用看日志里的「空间变化」行），"
            + "余下 {2}（保留 {3}）";

        /// <summary>空间门**放行**那一行的普通口径（那个可用空间是刚刚探到的；打包走它）。</summary>
        public const string SpaceGatePassedFormat =
            "空间门通过：需要 {0}，可用 {1}，余下 {2}（保留 {3}）";

        /// <summary>
        /// 空间曲线**补记**那一针（用户 2026-10-02 真机）。
        ///
        /// <para>批末那条空间曲线的"收"是在**其余物删除之前**打的：随后那些源包 / 过程物被彻底删掉，
        /// 盘上真实可用比曲线里的"收"多出一大截 —— 用户按日志读会以为"这一批净吃掉 5.5 GB"
        /// （他那一批实际净省 0.5 GB）。所以其余物处理完之后**再补一针**，并如实标注它的位置。</para>
        ///
        /// <para>⛔ 这一针走的是**本批那个侦察器自己的探测函数**（不是第二套取数），
        /// 文案也由 <c>SpaceTrendMonitor</c> 产出（不是第二套曲线）。</para>
        ///
        /// <c>{0}</c> = 这一针的来由，<c>{1}</c> = 现在真实可用，<c>{2}</c> = 本批起，<c>{3}</c> = 本批最低。
        /// </summary>
        public const string SpaceCurvePostscriptFormat = "空间曲线补记：{0} 可用 {1}（本批起 {2} / 最低 {3}）";

        /// <summary>落点算不出来时的那一行（**不许**静默显示一个假路径）。</summary>
        public const string OneClickConfirmDestinationUnknownFormat = "暂时算不出来：{0}";

        /// <summary>B 段（没有可用密码）并进确认框的那一行。</summary>
        public const string OneClickConfirmNoPasswordFormat =
            "注意：本批有 {0} 个包可能需要密码，而当前一个可用候选都没有（例如 {1}）—— "
            + "继续大概率以「密码错误」或「达到密码尝试上限」结束。要补密码请到「密码」页一键导入（本批现在就开始，不等待）。";

        /// <summary>一键档不弹手动密码框时写的那条 INFO（说清"要手动给去哪儿给"）。</summary>
        public const string OneClickConfirmManualPasswordSkippedLog =
            "一键处理：本批有包可能需要密码，但一键档不在批中间弹任何框 —— "
            + "要补密码请在开始前到「密码」页一键导入（或先在④页把密码加进列表），没补就按空密码 / 统一密码 / 密码本的顺序试。";

        /// <summary>
        /// 批末那条**红字**（用户 2026-09-29 要求）：把"可能是没有密码 / 密码不对"导致的失败单独点出来。
        ///
        /// <para>⚠ 措辞必须带"可能"：引擎给的失败原因不止一种（文件损坏、缺卷、空间不足…），
        /// 程序只能按"错误信息是不是密码类"来点名，⛔ 不许写成"就是没密码"。</para>
        /// </summary>
        public const string BatchPasswordSuspectsLogFormat =
            "{0} 个包「可能」是没有密码、或者密码不对，才没解压完成（例如 {1}）。"
            + "⚠ 这只是可能 —— 出错也可能是文件损坏、缺卷、空间不足等；逐个包的真正原因看①页「错误信息」列或「导出失败清单」。"
            + "要补的话，到「密码」页一键导入，再重跑这些包。";

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
        // 工作区根：默认落在这一单的目标目录里（用户 2026-09-30 明确指示）
        // ================================================================
        //
        // 用户原话："我不喜欢现在这样的模式就是专门在一个盘里面去开一个工作区……我这个 H 盘总共才三个
        // 最顶层文件夹，但是你这个第四个就会占一个，我身为用户，非常的讨厌"；
        // "要么是在解压的地方临时创一个，然后弄完就直接全部清除什么都不留，原地就在原地创，
        // 指定就在指定创，不要有固定一个位置的思想……甚至危险操作固定到了 C 盘"；
        // "就弄到工作区，而且是隐形的……要创的话就隐秘文件"；
        // （2026-09-30 收口）"这个彻底取消，用户没有定工作区的权力，就是在解压的地方设立隐形的工作区，
        // 这就完全不存在跨盘的操作"。
        // 定为：**唯一一条路** = <目标目录>\.ArchiveFixer.work（点开头 + Hidden 属性，用户翻自己目录看不见它）；
        // 设置项「缓存根目录」已删除，⛔ 目标位置不可用就**报错说清是哪一个**，绝不悄悄换个盘。

        /// <summary>本批工作区落在这一单的目标目录里面（含"为什么这么定"的一句话）。</summary>
        public const string WorkspaceRootInOutputDirectoryFormat =
            "本批工作区在目标目录里面：{0}\\.ArchiveFixer.work（就在成品落地的那个目录里建，"
            + "不在盘根另开一个；点开头 + 隐藏属性，翻目录时看不见它。暂存与成品同一棵树 ⇒ 定稿是同卷改名，"
            + "不是跨盘复制）—— 完整路径：{1}";

        /// <summary>跨盘时说清"这一批有落点在别的盘上"——**不许假装它们同盘**。</summary>
        public const string WorkspaceRootCrossDriveFormat =
            "；注意：本批的目标目录跨 {0} 个盘（{1}），工作区跟着「第一个」目标目录（{2} 盘）—— "
            + "其它盘上的那些任务，中间产物在 {2} 盘上解、成品也在 {2} 盘上先落好再跨盘搬过去"
            + "（比同盘改名慢，也不省空间）。工作区位置只由目标目录决定，没有第二个可以设的地方";

        /// <summary>
        /// ⛔ 目标位置不可用 → 停下报错，并**明确说清是哪个目标位置不行**。
        ///
        /// <para>工作区只有"由目标位置派生"这一条路（设置里的「缓存根目录」已删除），
        /// 所以这里没有第二个候选可选 —— 只能把原因摆出来让用户去修落点。</para>
        ///
        /// <para>这里刻意**不回落 C 盘 / 程序目录**（用户 2026-09-30 点名的红线）：悄悄换个盘写中间产物，
        /// 正是他说"危险操作固定到了 C 盘"的那件事。</para>
        /// </summary>
        public const string WorkspaceTargetUnusableFormat =
            "⛔ 目标位置不可用，工作区没法建，这一批没有开工（一个字节都没写）：{0}。"
            + "工作区只有一个来源：建在「这一单成品要落的那个目标目录」里面（该目录下的 .ArchiveFixer.work）。"
            + "现在这个位置建不出来，所以既不知道建在哪、也不会替你随便挑一个盘。"
            + "请在①页给这一批指定一个有效的输出位置（或改「解压到源文件所在目录」那类落点设置）；"
            + "工作区位置本身没有任何设置项可以改。";

        /// <summary>一个算得出落点的任务都没有（<see cref="WorkspaceTargetUnusableFormat"/> 里那一段原因）。</summary>
        public const string WorkspaceNoUsableTargetReason = "这一批里没有任何算得出目标目录的任务";

        /// <summary>目标目录路径形状不合法（原因句）。</summary>
        public const string WorkspaceTargetInvalidShapeFormat = "目标目录路径形状不合法：{0}";

        /// <summary>目标目录建不出来（原因句）。</summary>
        public const string WorkspaceTargetNotWritableFormat = "目标目录建不出来（不可写）：{0}";

        /// <summary>目标目录里建不出工作区（原因句）。</summary>
        public const string WorkspaceUnderTargetNotWritableFormat = "目标目录里建不出工作区（不可写）：{0}";

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

        /// <summary>⑥设置页「打开工作区目录」按钮的提示（工作区只由目标目录派生，所以要说清它会开到哪）。</summary>
        public const string SettingsWorkDirectoryButtonHint =
            "工作区只跟目标目录走：这一单成品要落的那个目录下的 .ArchiveFixer.work"
            + "（点开头 + 隐藏，翻目录看不见）。失败 / 取消留下的中间产物在这里"
            + "（③「清理与删除」页可以看体积并清理）。";

        // ================================================================
        // 部分完成也把已解出的内容放进目标目录（用户 2026-10-02 拍板的口径 A）
        // ================================================================
        //
        // 用户原话（`docs/部分完成发布方案.md` §6.3）：「这个的操作最少差不多就占了两份原包的空间
        // 要在开启界面详细写清楚」。⇒ 小字里四件事一件都不许省：两份空间 / 源包不删 /
        // 与「空间不足」互斥 / 落点固定且重跑不撞名。
        // ⛔ 文案里不许写 Markdown（会原样显示成星号），强调一律用「」（AGENTS.md §9.3）。

        /// <summary>③ 页那个开关的标题（**默认关**）。</summary>
        public const string SettingsPartialPublishLabel = "部分完成也把已解出的内容放进目标目录";

        /// <summary>③ 页那一组的标题。</summary>
        public const string SettingsPartialPublishGroupHeader = "部分完成的内容";

        /// <summary>开关下面那段小字（用户要求"在开启界面详细写清楚"）。</summary>
        public const string SettingsPartialPublishHint =
            "打开后，一个包解开了一大半、最后坏在个别条目上时，已经解出来并且逐条核对过的那部分会放进 "
            + "「目标目录\\包名\\部分完成\\」，不再整份丢弃。"
            + "① 代价：这一档跑完后盘上最多占两份 —— 源包 + 已解出的内容物，"
            + "而且源包一定不删、不搬（哪怕③页设了「彻底删除」）：缺的那些条目只存在源包里，"
            + "删掉就没有第二份完整数据了。"
            + "② 它与「空间不足」模式互斥：那一档开着时本开关不生效"
            + "（那一档要的是当场删源包腾地方，两者目的相反），空间账也会按「源包还占着」多算一整个源包。"
            + "③ 其余物里除最外层源包以外的过程物、以及这一单的工作区，会在收尾时删掉。"
            + "④ 判不出就什么都不发布：拿不到清单、引擎自报有错却点不出名、缺得太多（超过 5 个条目且"
            + "不足 95% 字节）—— 这三种情况源包原地不动、目标目录一个字节都不写。"
            + "⑤ 落点固定用「部分完成」这个名字，重跑同一份包还是进这个目录，同名只让位、绝不覆盖。";

        /// <summary>开关的悬浮提示（一句话版，与上面那段小字同源）。</summary>
        public const string SettingsPartialPublishTooltip =
            "默认关。打开后：解压中途失败时，已解出并逐条核对过的文件放进「目标目录\\包名\\部分完成\\」。"
            + "代价是跑完之后盘上最多两份（源包 + 内容物），源包一定不删；"
            + "与「空间不足」模式互斥（那一档开着时本开关不生效）。";

        /// <summary>真的发布成功：点名发了几个 / 多少字节 / 发到哪 / 缺了多少。</summary>
        public const string PartialPublishedFormat =
            "{0}：部分完成 —— 已把逐条核对过的 {1} 个文件 / {2} 放进 {3}"
            + "（清单共 {4} 个 / {5}；这一份没发出去或没解出来的有 {6} 个）。"
            + "源包原地不动（部分完成这一档一律不搬不删源包）。";

        /// <summary>命中让位（同名已存在，改名发布）时的补充一句。</summary>
        public const string PartialPublishedRenamedFormat =
            "{0}：其中 {1} 个文件因为落点已经有同名的（多半是上一次那半份），"
            + "按「名字(1)」让位发布 —— 一个字节都没有覆盖。";

        /// <summary>这一份不发布（闸门挡下 / 判不出）：原因原样带出来。</summary>
        public const string PartialPublishSkippedFormat =
            "{0}：这一份不按「部分完成」发布 —— {1}";

        /// <summary>发布前二次空间体检没过 ⇒ 不发布（判不出 / 不够 ⇒ 什么都不做）。</summary>
        public const string PartialPublishSpaceBlockedFormat =
            "{0}：部分完成的内容先不发布 —— 发布之前再探了一次空间，不够：{1}";

        /// <summary>与「空间不足」模式互斥（⛔ 设置本身一个字节都不改，只是这一轮不生效）。</summary>
        public const string PartialPublishSpaceTightBlockedFormat =
            "{0}：部分完成发布这一轮不生效 —— 本批按「空间不足」模式跑"
            + "（那一档要的是定稿后当场删源包回收空间，与「留源包 + 内容物两份」正好相反）。"
            + "③页那个开关本身没有被改动。";

        /// <summary>发布成功之后，其余物里除最外层源包以外的项被清掉。</summary>
        public const string PartialPublishRestPurgedFormat =
            "{0}：其余物里除最外层源包以外的 {1} 个项已删掉（部分完成这一档只留源包 + 已解出的内容物两份）：{2}";

        /// <summary>其余物没清（源包不在盘上 / 路径判不出）⇒ 一个字节都不删，如实说明。</summary>
        public const string PartialPublishRestKeptFormat =
            "{0}：其余物一个字节都没删 —— {1}";

        /// <summary>部分完成这一档对源包的动作（写一行 INFO，让人一眼看到"源包刻意没动"）。</summary>
        public const string PartialPublishSourceKeptFormat =
            "{0}：源包原地不动（部分完成这一档不搬也不删源包 —— 缺的那些条目只在源包里）";

        /// <summary>
        /// 批末诊断里那一组下面的补充一句：这一组里有几单**不是全丢**，已经按「部分完成」发布出来了。
        ///
        /// <para>为什么要写：批末那一行只数"失败 / 部分完成几个"，用户看到"3 个没做成"会以为
        /// 那些包的内容一个字节都没救回来 —— 而实际上有的已经发布了 557 个文件、现在就能用。</para>
        /// </summary>
        public const string BatchDiagnosticsPartialPublishedNoteFormat =
            "其中 {0} 个已经按「部分完成」发布出来了（共 {1} 个文件），那些内容现在就能用（源包仍在原处）";

        // ================================================================
        // L4 完整性分类（用户 2026-09-30 定下的检验等级）
        // ================================================================
        //
        // 三态：可证完整 / 可证不完整（带证据）/ **判不出**。
        // 红线：判不出 ⇒ 一律不删源、不搬源、结果照常保留，结论如实写"无法确认完整性"。
        // 唯一出口 = Extraction/ResultCompleteness.cs 的 ResultCompletenessClassifier。

        /// <summary>可证完整（唯一允许删 / 搬源包的那一档）。</summary>
        public const string CompletenessCompleteFormat =
            "完整性：可证完整（拿归档清单逐条核对过，文件数与总字节都对得上）";

        /// <summary>可证不完整（带证据）—— 说清证据是什么，不许含糊成"好像不太对"。</summary>
        public const string CompletenessIncompleteFormat =
            "完整性：可证不完整（有证据：产物为空 / 少于清单 / 分卷缺失）";

        /// <summary>
        /// ⛔ 判不出 —— 一律不删源、结果照常保留，并如实告诉用户"这次没法确认完整性"。
        /// </summary>
        public const string CompletenessUndeterminableFormat =
            "完整性：无法确认（没有可用的归档清单可核对，只做了「有产物、不是全 0 字节」的底线检查）—— "
            + "按规矩不动源包，解出来的结果照常保留";

        /// <summary>
        /// 「为什么没删源包」——**唯一**一处拼这句话（<c>ResultCompletenessVerdict.Blocker</c>）。
        /// {0} = 判据（三态那句结论）。
        /// </summary>
        public const string SourceRemovalBlockedFormat = "为什么没删源包：{0}";

        /// <summary>
        /// 「为什么没删源包」的完整版：多带一句"哪一层 + 什么原因"。
        /// {0} = 判据，{1} = 取不到清单的那一层与原因（用户能照着做下一步）。
        ///
        /// <para>用户 2026-09-30 真机报的 bug 里，日志只有前半句（"没有可用的归档清单可核对"），
        /// 一个字都没说清是哪一层、为什么 —— 他只能猜。这一句就是为了不再让人猜。</para>
        /// </summary>
        public const string SourceRemovalBlockedDetailFormat = "为什么没删源包：{0}；具体判据 —— {1}";

        /// <summary>
        /// 链尾那一档**被拦下**时那一行 WARN 的开头（<c>ExtractionCoordinator.AppendRemovalBlocked</c> 唯一使用处）。
        ///
        /// <para>为什么要有这个常量：这行日志是"用户没看到东西被删，到底是哪一条判据拦下的"的唯一答案，
        /// 而用例要能**不手抄中文**地断言它（⛔ 抄中文的断言改一次文案就假绿）。</para>
        /// </summary>
        public const string ChainRestBlockedPrefix = "链尾没有按「删除操作」处理其余物 / 源包";

        /// <summary>链尾那一档被"任务没成功"拦下。</summary>
        public const string ChainRestBlockedTaskOutcomeFormat = "任务没有成功";

        /// <summary>链尾那一档被"没有其余物"拦下（这一单确实没产生过程物、源包也没搬进来）。</summary>
        public const string ChainRestBlockedNoRestDirectoryFormat = "这一单没有其余物（没有过程物，源包也没被搬进来）";

        /// <summary>链尾那一档被"记下来的其余物目录已经不在了"拦下。{0} = 目录。</summary>
        public const string ChainRestBlockedRestMissingFormat = "记下来的其余物目录已经不在盘上（{0}）";

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
        // 导入时的无用物处理 + 列表里删无用物
        // ================================================================
        //
        // 用户 2026-09-24 第 15 条要的是"列表要能删无用物"；2026-09-28 他把时机收得更死：
        // 原话「为什么不在检测到的时候就直接移除」「当选择文件的时候，移除无用物的弹窗，没有弹出来
        // 我还以为你又没弄好，但是在点击一键处理之后，他一出来，但是在我点击之前他一直还是勾选着，
        // 你为什么要这样」——于是**导入一完成就自动把无用物移出任务列表**，
        // ⛔ 那条「导入后提醒」的弹窗已经退休（相关文案一并删除，别再加回来）。
        //
        // 与 §9.7「解压前的提醒」仍是**同一个判据**（SourceJunkScanner）；程序对无用物依旧
        // **一个都不动**（不删/不改名/不搬），"移出列表"动的只是任务列表。

        /// <summary>
        /// 导入完成时**自动**把无用物从任务列表里移出（用户 2026-09-28）。
        /// <c>{0}</c> = 移掉几个，<c>{1}</c> = 前几个名字（最多 5 个）。
        /// </summary>
        public const string ImportJunkRemovedLogFormat =
            "导入完成：已自动把 {0} 个无用物从任务列表里移出（它们对解压没用；源文件一个字节都没动）：{1}";

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

        /// <summary>
        /// ③「清理与删除」页那个开关的文案。
        ///
        /// <para>⚠ <b>2026-09-28：它已经不再控制任何行为</b> —— 那个「导入后提醒」弹窗退休了，
        /// 无用物改成**导入一完成就自动移出列表**（<c>ScanCoordinator.AddPathsAsync</c>），
        /// 与这个开关的开关状态无关。设置项本身先留着（⛔ 不动设置序列化，用户盘上的
        /// <c>appsettings.json</c> 里这个键还得读得进来），界面上这一格也留着，
        /// 但文案如实说明它现在不生效 —— 一个写着"关了就不提醒"却什么都不管的开关，对用户就是一句谎。</para>
        /// </summary>
        public const string SettingsRemindJunkAfterImportLabel = "导入文件夹后提醒一次「疑似无用物」（已停用）";

        /// <summary>它的说明（如实写明：2026-09-28 起这个开关不再控制导入时的行为）。</summary>
        public const string SettingsRemindJunkAfterImportHint =
            "已停用：无用物现在是「导入一完成就自动从任务列表里移出」（只动列表，源文件一个字节都不动），"
            + "跟这个开关无关。这一格先留着（设置文件不动），改它现在什么都不影响。";

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
            "上次的密码列表无法读取，已忽略（{0}）。";

        /// <summary>记忆保存失败时的提示（不弹框、不阻断，只在提示条上说一句）。</summary>
        public const string PasswordListMemorySaveFailedFormat = "这次的密码列表没能存下来（{0}）；列表本身照常可用。";

        /// <summary>主界面摘要那一行挂的短提示（明细在密码列表窗口的提示条上）。</summary>
        public const string PasswordListMemoryUnavailableShort =
            "上次的密码列表读不出来（已忽略，原件已另存一份备份；明细见「密码列表管理」）";

        /// <summary>
        /// 读不出来时那句"已经另存了一份备份"（2026-09-29 复核补：老文案说"程序没有覆盖、也没有删除"，
        /// 而启动接线随后就会把这份读不出来的记忆**覆盖掉** —— 话是假的，用户的密码列表会真的丢）。
        /// </summary>
        public const string PasswordListMemoryBackedUpFormat =
            "读不出来的那份已另存为 {0}（同一目录下），换回原来的机器或系统时把它改回原名就能再用。"
            + "如果是换了机器，也可以用「写回密码本…」把当前列表带走。";

        /// <summary>连备份都没做成时的后半句 —— 如实说，别让用户以为盘上还留着一份。</summary>
        public const string PasswordListMemoryBackupFailedFormat =
            "⚠ 读不出来的那份也没能另存备份（{0}），它随时可能被新的记忆覆盖。";

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

        /// <summary>
        /// 整组改名成功（网盘给每卷缀了垃圾那种）。<c>{0}</c> = **逐条** `旧名 → 新名`
        /// （<c>VolumeNameRepairPlan.Describe()</c>），<c>{1}</c> = 一共改了几卷。
        ///
        /// <para>⚠ <c>{0}</c> 里已经是"每一卷都点名"的整串：老写法写的是"第一卷一旧一新 + 等"
        /// （真机上另一卷 `风景02.mp4` 因此一个字都没出现在日志里，用户对不上账）。</para>
        /// </summary>
        public const string VolumeRepairGroupDoneFormat = "已按建议改名（整组 {1} 卷）：{0}";

        /// <summary>整组改到一半失败。<c>{0}</c> = 已经改好的卷数，<c>{1}</c> = 卡住的原因。</summary>
        public const string VolumeRepairGroupPartialFormat = "整组改名只完成了 {0} 卷就停下了（已改的那几卷不会再动）：{1}";

        /// <summary>
        /// 整组没改成、但**已经全部改回原名**（内容一个字节没动）。<c>{0}</c> = 卡在哪一卷、为什么。
        /// <para>回滚是"全成或全不成"的另一半：留着半改状态比不改更糟（7-Zip 按新基名找后续卷，
        /// 名字七零八落时整组都打不开）。</para>
        /// </summary>
        public const string VolumeRepairRolledBackFormat =
            "整组改名没做成，已把改过的名字全部改回原样（内容一个字节没动）：{0}";

        /// <summary>
        /// 整组没改成、而且**有卷没能改回原名** —— 必须如实点名，⛔ 别让用户以为没动过。
        /// <c>{0}</c> = 没改回来的卷数，<c>{1}</c> = 那几卷的名字。
        /// </summary>
        public const string VolumeRepairRollbackIncompleteFormat =
            "整组改名没做成，且有 {0} 卷没能改回原名（请手动核对）：{1}";

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

        // ── 「名字完全靠不住 → 靠内容 + 尺寸 + 试开」这一档（用户 2026-09-28 三层方案） ──

        // ── 「内容里有卷号」这一档（用户 2026-09-29：RAR / ZIP 的分卷号写在内容里） ──

        /// <summary>内容既不是 RAR 分卷也不是跨盘 zip 的片（7z 那条路另说）。</summary>
        public const string VolumeRepairContentNotAVolumeMember =
            "它的内容没说自己是分卷组的一员（不是 RAR 分卷、也不是跨盘 zip 的片），程序不在内容上猜";

        /// <summary>RAR 1.5–4.x（WinRAR 里叫 RAR4）的老式编号族（第一卷 .rar、之后 .r00/.r01……）：本程序只认 partN.rar 一族。</summary>
        public const string VolumeRepairContentRarOldNumbering =
            "这是 RAR4（RAR 1.5–4.x）的老式编号分卷（第一卷 .rar、之后 .r00/.r01……）：程序只认 partN.rar 这一种拼法，" +
            "换成别的拼法可能整组解不开，所以一个字节都不动";

        /// <summary>RAR 1.5–4.x 主头的「这一卷是第 1 卷」标记与推出来的卷号对不上。</summary>
        public const string VolumeRepairContentRarFirstVolumeMismatch =
            "RAR4（RAR 1.5–4.x）头里标着「这一卷是第 1 卷」的那一卷，与按卷号排出来的第 1 卷不是同一卷 —— " +
            "很可能真正的第 1 卷不在这个目录里，那就不能改（把第 2 卷改名叫第 1 卷只会更糟）";

        /// <summary>RAR 1.5–4.x 卷号的基数（0 起还是 1 起）两种解释都成立 / 都不成立。</summary>
        public const string VolumeRepairContentRarBaseAmbiguous =
            "RAR4（RAR 1.5–4.x）的卷号字段没写基数是 0 起还是 1 起，而这一组两种解释都说得通 —— 程序不猜";

        /// <summary>整组卷号连不成 1..N。<c>{0}</c> = 同目录里认出几卷。</summary>
        public const string VolumeRepairContentGroupNotContiguousFormat =
            "同目录里认出 {0} 卷，但它们的卷号连不成 1、2、3……（缺卷，或者被改坏的那一卷已经认不出来了），整组都不动";

        /// <summary>手上的这一片不在这一组里。</summary>
        public const string VolumeRepairContentCurrentNotInGroup =
            "你手上这一片不在同目录里那一组跨盘 zip 里（目录里还有别的组），程序不替它猜位置";

        /// <summary>EOCD 说这是单盘 zip。</summary>
        public const string VolumeRepairContentZipSingleDisk =
            "它的中央目录说这是单盘 zip（盘号 0），不是跨盘组 —— 问题不在盘号上";

        /// <summary>跨盘 zip 片数 ≥ 3。<c>{0}</c> = 末片说的总片数。</summary>
        public const string VolumeRepairContentZipTooManyDisksFormat =
            "末片说这一组有 {0} 片：除末片外，片的内容里没有盘号（只有结尾的跨盘标记），" +
            "中间那几片的先后无从判断 —— 只有 2 片时才能用消去法定序，所以不动";

        /// <summary>跨盘 zip 少了片。<c>{0}</c> = 同目录里认出的片数。</summary>
        public const string VolumeRepairContentZipPartsMissingFormat =
            "末片说这一组是跨盘的，但同目录里只认出 {0} 片能接上它 —— 凑不齐整组，一个字节都不动";

        // ── 「按归档自己的索引定盘」这一档（用户 2026-10-01 的专属算法：末片的中央目录写着每一片在第几盘） ──

        /// <summary>
        /// 末片的中央目录说某一盘上该有一个本地头，可整个目录里没有一份文件对得上 —— 那一片**不在**。
        /// <c>{0}</c> = 总片数，<c>{1}</c> = 缺几片，<c>{2}</c> = 缺的是第几片（1 起，逗号分隔）。
        /// </summary>
        public const string VolumeRepairSpannedZipMissingFormat =
            "末片说这一组一共 {0} 片，其中第 {2} 片（共 {1} 片）的内容对不上这个目录里的任何一份文件 —— "
            + "那几片不在这里，凑不齐整组，一个字节都不动";

        /// <summary>
        /// 有几片的内容里"一个文件都没开始"（整段夹在别的文件数据中间）⇒ 这几片的先后在内容里没有任何线索。
        /// <c>{0}</c> = 总片数，<c>{1}</c> = 定不下来的片数。
        ///
        /// <para>它与"缺卷"是两件事：片都在、内容也对得上，只是**顺序**没线索。用户看到这句话应该明白
        /// 该做什么（把密码补上让程序试拼，或者按工具原本的顺序排），而不是去满盘找片。</para>
        /// </summary>
        public const string VolumeRepairSpannedZipUndecidedFormat =
            "这一组 {0} 片里，有 {1} 片的内容里一个文件都没开始（整段夹在别的文件数据中间）—— "
            + "只靠内容定不出这几片的先后（除末片外每一片都一样大，体积也分不出来）";

        /// <summary>
        /// **缺的是末片**。<c>{0}</c> = 手上几片满片，<c>{1}</c> = 每片多大，<c>{2}</c> = 第 1 片的文件名。
        ///
        /// <para>为什么必须说这一句：用户 2026-10-01 要的正是"在引擎报缺卷之前就说清缺的是第几片"。
        /// 末片（<c>.zip</c>）是解压入口、里面还有中央目录 —— 它不在，整组就没法开工；
        /// 而那几片长得一模一样，光看名字分不出缺的是哪一片。</para>
        /// </summary>
        public const string VolumeRepairSpannedZipTailMissingFormat =
            "同目录里这 {0} 份文件彼此等大（每份 {1} 字节），其中「{2}」的开头是跨盘标记 —— "
            + "这是一组跨盘 zip 的「满片」，缺的是「末片」（就是叫 `名字.zip` 的那一片：中央目录在它里面，"
            + "它是解压入口）。把末片放回来这一组才凑得齐，现在一个字节都不动";

        /// <summary>
        /// 一组卷散在两层目录里，但其中一卷与目标目录**不在同一个盘**上 —— 收不到一起，整组不改。
        /// <c>{0}</c> = 那一卷的名字，<c>{1}</c> = 入口那一卷的名字。
        ///
        /// <para>为什么必须拒绝而不是"就地改名"：引擎找兄弟卷**只看入口文件旁边那一层**，散着放即使名字都对
        /// 也解不开（实测 7-Zip 就是这样）—— 改了名却还是解不开，等于骗用户。
        /// 而跨盘搬大文件正是这条路上⛔ 绝不做的事（不复制、不跨盘搬），所以只能如实说"收不到一起"。</para>
        /// </summary>
        public const string VolumeRepairGatherAcrossVolumeFormat =
            "这一组卷散在不同的文件夹里，而「{0}」与入口那一卷「{1}」不在同一个盘上 —— "
            + "程序只把同盘的卷收到一起（⛔ 不跨盘搬、也绝不复制大文件），所以整组一个名字都不改";

        /// <summary>
        /// **密码已经证实、坏的是数据**（用户 2026-10-01 真机 `giu910`：17.7 GiB 解到 98% 才有一个文件 CRC 失败，
        /// 老口径却把它当成"这个密码候选不对"，解压完又回去试了 7 个候选、13 分钟的产物全扔）。
        /// <c>{0}</c> = 已经解出来的文件数，<c>{1}</c> = 这些文件的字节数。
        ///
        /// <para>⛔ 措辞必须把两件事说清：① 密码不是问题（别再核对密码本）；② 坏的是数据（哪一个文件坏了
        /// 由紧跟着的"引擎原话"点出来）。⛔ 不许写成「密码错误」，也不许说成"已修好"。</para>
        /// </summary>
        public const string PasswordProvenDataCorruptedFormat =
            "文件损坏：密码已经证实是对的（这一趟真的解出了 {0} 个文件 / 共 {1} 字节），"
            + "失败在数据层面 —— 个别条目的 CRC 对不上（具体是哪一个看紧跟着的引擎原话）；"
            + "换密码不会改变结果，所以不再往下试密码候选";

        /// <summary>
        /// 一批里有包**撞上「每层密码尝试上限」**时追加的指路（用户 2026-10-02 真机要求：
        /// 没试出来要提醒"可能是没有密码，也可能是密码排在候选 10 条之后，让用户调整顺序"）。
        /// <c>{0}</c> = 每层最多试几条（设置项 <c>MaxPasswordAttemptsPerLayer</c>，默认 10，④页可改）。
        ///
        /// <para>⛔ 三段都不许省：①「可能」—— 程序与引擎都没有断言这些包就是密码问题；
        /// ②"本来就没有密码"这一档；③"对的密码排在候选更靠后"这一档 + 去④页调顺序 / 调大上限的出口。</para>
        ///
        /// <para>⛔ 它**不是**新状态：机器终态仍是 <see cref="PasswordAttemptLimitReached"/>，
        /// 这里只是一句指路（不要接进 <c>StatusToBrushConverter</c> / <c>TaskSummaryService</c> 的分档）。</para>
        /// </summary>
        public const string PasswordAttemptLimitHintFormat =
            "按上限停下的那些包，不等于确认是密码问题：可能它们本来就没有密码，"
            + "也可能正确的密码排在候选里更靠后的位置（每层最多只试前 {0} 条）—— "
            + "可以到④页把候选顺序调一调，或把「每层密码尝试上限」调大。";

        /// <summary>
        /// 同目录里连"值得试开一次"的证据都没有（尺寸规律或两卷形状）。
        ///
        /// <para>文案跟着 2026-09-29 的放宽改过：老口径只说"除最后一卷外应当一样大"，
        /// 而**两卷**时第二卷就是余量、必然更短 —— 那句话等于告诉用户"你这种切法程序不认"。
        /// 现在两张门票都写出来（用户原话："可以靠后缀数字 2 的情况猜一猜是第二卷"）。</para>
        /// </summary>
        public const string VolumeRepairContentNoSizePattern =
            "同目录里看不出这是一组分卷：既没有与第一卷一样大的满片，也没有「后缀数字接得上」或"
            + "「第一卷正好是整数 MiB（切分上限）」的两卷形状，没有把握就不动";

        /// <summary>试开验证没通过。<c>{0}</c> = 试开失败的原因。</summary>
        public const string VolumeRepairContentProbeFailedFormat =
            "把候选按假设的顺序试开，引擎也读不出里面的东西（{0}），所以一个字节都不动";

        /// <summary>
        /// **没法试开**（没工作区根 / 跨盘）。<c>{0}</c> = 没法试开的原因。
        ///
        /// <para>为什么与上一条分开（用户 2026-09-30 红线：工作区只准设在解压的地方）：
        /// 改名那条路**没有目标目录** ⇒ 没有工作区根 ⇒ 按口径**不许**另找地方开一个 ⇒ 一次都不试。
        /// ⛔ 那时不许写成"试开过了、不成立"（那是把"没试"说成"试过"），只能如实报"无法确认"，
        /// 结论照旧是**不改名**（判不出就不动）。</para>
        /// </summary>
        public const string VolumeRepairNoProbeFormat =
            "这一组没法做试开验证（{0}），证不出整组是齐的 ⇒ 无法确认，一个字节都不动";

        /// <summary>改名批次收尾（写进日志）。<c>{0}</c> = 成功数，<c>{1}</c> = 失败数。</summary>
        public const string VolumeRepairBatchDoneFormat = "按建议改名：成功 {0} 个、失败 {1} 个；接着重试解压勾选的任务。";

        // ── 「一组分卷只解一次」（用户 2026-10-01 真机：三棵一模一样的产物树 + `111(1)`） ──

        /// <summary>
        /// 后续卷那一单被跳过时的原因（显示在任务行的原因列）。<c>{0}</c> = 负责整组的首卷文件名。
        ///
        /// <para>为什么必须说清"谁在负责"：用户看到一行写着「已跳过」而没有任何解释，
        /// 只会以为程序漏掉了他的包 —— 而这件事的真相是"整组已经由另一行解完了"。</para>
        /// </summary>
        public const string VolumeGroupFollowerSkippedFormat =
            "这是同一分卷组的后续卷，整组由「{0}」那一单负责（一组只解一次：同组每一卷都解一遍会多出好几棵一模一样的产物树）。";

        /// <summary>同一件事写进日志。<c>{0}</c> = 本单文件名，<c>{1}</c> = 负责整组的首卷文件名。</summary>
        public const string VolumeGroupFollowerSkippedLogFormat =
            "{0}：这一卷是分卷组的后续卷，整组由「{1}」那一单从首卷启动 —— 本单不重复解"
            + "（同一份内容解两遍会多出一棵产物树，还会把整组的链尾处理拦住，源包就一个都删不掉了）。";

        /// <summary>
        /// 汇总里**跟班卷**那一句 —— 「一键处理完成」那一行与批末「本批汇总」那一行**共用同一句**
        /// （用户 2026-10-02 真机：同一份日志里两处口径打架，一处「跳过 1」、一处「跳过 0」）。
        ///
        /// <para>判据是事实位 <c>ArchiveTask.CountsTowardBatchOutcome</c>（跟班卷 = false）：
        /// 它们**不算"跳过"**（不是没做成 —— 整组已经由第一卷那一单解完了），
        /// 但要在总数里占一个名额，所以汇总里"未处理"那一档要把它们减掉，
        /// 否则恒等式「各分项之和 + 未处理 = 本次任务数」当场破掉。</para>
        ///
        /// <c>{0}</c> = 几个跟班卷。
        /// </summary>
        public const string VolumeGroupFollowerSummaryFormat =
            "另有 {0} 个是同一分卷组的后续卷 —— 整组由第一卷那一单解完，按设计跳过（不是没做成）。";

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

        /// <summary>
        /// 其余物处理的**空壳那一行**（用户 2026-10-03）：其余物里递归地一个文件都没有 ⇒
        /// 已就地删掉空目录，**一个字节都没进回收站**。<c>{0}</c> = 短路径。
        ///
        /// <para>⛔ 这一句**不许**复用 <see cref="RestPurgedCompactFormat"/>：那一句的第一个槽是**档名**，
        /// 选了「移入回收站」时会写成"移入回收站：…（0 项 / 0 B）"，而空壳档**根本没进回收站**
        /// —— 那就是一句假话。选哪一句只读 <c>RestPurgeOutcome.RemovedAsEmptyShell</c> 这个**事实位**，
        /// ⛔ 不许拿"0 项 0 B"去猜（那几个数证明不了"没进回收站"）。</para>
        /// </summary>
        public const string RestPurgedEmptyShellFormat = "其余物里只剩空壳：已就地删掉空目录、没有进回收站 —— {0}";

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

        // ================================================================
        // 批末诊断清单（用户 2026-09-30 第 1 条：批末那个汇总框不能只给数字，要像编译器那样说清"错在哪"）
        // ================================================================
        //
        // 他原话："我说的最后的汇总框，不单是要改颜色，而且你要特别说明哪里出错了，就像一般的编译器里面
        // 最后会报出错在什么地方，比如空间不足、哪个压缩包密码不对、111.7z.001 的分卷找不到……
        // 你和我这总会仔细看日志，但是用户不会，他们只想看看错误出在哪里。"
        //
        // 措辞口径：每一组 = 组名 + 个数 + **具体包名**（最多几个，其余写"还有 K 个"）；
        // 组名能复用既有状态常量的就复用（磁盘空间不足 / 分卷缺失 / 文件损坏 / 权限不足 /
        // 输出路径冲突 / 源文件已变化 / 部分完成 / 已跳过 / 已取消），只有"密码问题"与"其他失败"
        // 这两个**分组**名是这里新写的。

        /// <summary>诊断清单的标题行（这一批一条问题都没有时**一个字都不写**；⛔ 不许出现空标题）。</summary>
        public const string BatchDiagnosticsTitle = "出错在哪（逐组点名）：";

        /// <summary>密码类那一组的组名（密码错误 / 达到密码尝试上限 / 文件名已加密三种终态合并成一组）。</summary>
        public const string BatchDiagnosticsPasswordTitle = "密码问题";

        /// <summary>
        /// 密码类那一组的注脚。⚠ 必须带"可能"（AGENTS.md §11）：引擎给的结论并不专一，
        /// 同一个包也可能是缺卷 / 损坏，⛔ 不许写成"就是没有密码"。
        /// </summary>
        public const string BatchDiagnosticsPasswordNote = "可能是没有密码、或者密码不对";

        /// <summary>落不到任何具体原因的那些失败（解压失败 / 未知错误 / 没有可用的解压引擎 …）。</summary>
        public const string BatchDiagnosticsOtherTitle = "其他失败";

        /// <summary>这一批根本没轮到它（没有终态）的那些。</summary>
        public const string BatchDiagnosticsNotReachedTitle = "没轮到";

        /// <summary>一行的形状：<c>{0}</c> = 组名（可能带注脚），<c>{1}</c> = 个数，<c>{2}</c> = 包名清单。</summary>
        public const string BatchDiagnosticsLineFormat = "{0}：{1} 个 —— {2}";

        /// <summary>每组最多列几个名字之后的那一句（<c>{0}</c> = 还有几个名字没列）。</summary>
        public const string BatchDiagnosticsMoreFormat = "（还有 {0} 个）";

        /// <summary>分卷缺失那一组的逐条说明：<c>{0}</c> = 缺的卷名（不变量 7 要求报"缺哪几个"）。</summary>
        public const string BatchDiagnosticsMissingVolumesFormat = "缺 {0}";

        /// <summary>空间不足那一组的逐条说明：<c>{0}</c> = 需要多少，<c>{1}</c> = 差多少。</summary>
        public const string BatchDiagnosticsSpaceDetailFormat = "需要 {0}，差 {1}";

        /// <summary>每一组前面那个小圆点（与①页日志的缩进风格区分开：这是给弹窗读的清单）。</summary>
        public const string BatchDiagnosticsBullet = "· ";

        /// <summary>「下一步」那一行的前缀。</summary>
        public const string BatchDiagnosticsNextStepPrefix = "下一步：";

        /// <summary>「下一步」里最多说几条动作；超出部分折成这一句（弹窗总长度必须能一眼看完）。</summary>
        public const string BatchDiagnosticsNextStepRest = "其余看①页「错误信息」列或「导出失败清单」";

        /// <summary>「下一步」：空间不足那一档（与空间门/中途提示给的三条出路同一口径）。</summary>
        public const string BatchDiagnosticsActionDiskSpace = "清空间（清理「其余物」，或换一个空间更大的输出盘）";

        /// <summary>「下一步」：密码类那一档。</summary>
        public const string BatchDiagnosticsActionPassword = "补密码（到「密码」页一键导入后重跑这些包）";

        /// <summary>「下一步」：分卷缺失那一档。</summary>
        public const string BatchDiagnosticsActionMissingVolume = "补卷（把缺的那几卷放到与第一卷同一个目录里）";

        /// <summary>「下一步」：文件损坏那一档。</summary>
        public const string BatchDiagnosticsActionCorrupted = "损坏的只能重新下载";

        /// <summary>「下一步」：权限不足那一档。</summary>
        public const string BatchDiagnosticsActionAccessDenied = "权限（关掉占用文件的程序，或换一个能写的输出目录）";

        /// <summary>「下一步」：输出路径冲突那一档。</summary>
        public const string BatchDiagnosticsActionOutputConflict = "冲突（换一个输出目录，或先处理掉同名文件）";

        /// <summary>「下一步」：源文件已变化那一档（不变量 11）。</summary>
        public const string BatchDiagnosticsActionSourceChanged = "源文件变了（右键「重新扫描此文件」后再处理）";

        /// <summary>
        /// 「下一步」：其他失败（含校验没通过、改名/测试失败、没有可用引擎…）。
        ///
        /// <para>⚠ 这是**真说不出原因时**才允许出现的那一档（用户 2026-09-27：
        /// 「诊断只说"下一步：其他"」就是被他点名的那句废话）。所以它必须给一个**能行动的句子**，
        /// ⛔ 不许写成"其他"两个字了事；而且判据严格排在所有具体原因之后
        /// （见 <see cref="BatchSummaryDiagnosticsRules.Classify"/> 的 ② 与 ③ 两支）。</para>
        /// </summary>
        public const string BatchDiagnosticsActionOther =
            "归不到具体原因的那些（对照①页「错误信息」列或「导出失败清单」里的引擎原话逐条看）";

        /// <summary>「下一步」：取消 / 没轮到的那一档。</summary>
        public const string BatchDiagnosticsActionNotFinished = "没跑完的那些（再点一次「一键处理」接着跑）";

        /// <summary>
        /// 「下一步」：**部分完成**那一档（内容物是好的、只是没做完 —— 例：源包没能移入其余物）。
        ///
        /// <para>为什么要单独一句（用户 2026-09-27：批末诊断只说「下一步：其他」）：
        /// 这一档的用户动作与"归不到具体原因"完全不同 —— 东西已经解出来了，再跑一次就能收尾，
        /// ⛔ 不该把他指去"逐条看引擎原话"。差在哪一步由清单里那一行的括号补出来
        /// （见 <c>BatchSummaryDiagnosticsRules</c> 的 <c>DescribeDetail</c>）。</para>
        /// </summary>
        public const string BatchDiagnosticsActionPartiallyCompleted =
            "做了一半的那些（内容物是好的，再点一次「一键处理」把它收尾）";

        /// <summary>日志里逐组那一行的前缀（弹窗里是「·」，日志里带个来源标记更好搜）。</summary>
        public const string BatchDiagnosticsLogPrefix = "批末诊断：";

        // ===== 详细日志 / 引擎原话（用户 2026-09-27：「开了更详细的日志选项怎么还是这么简单」）=====
        //
        // 这一组是**日志口径的唯一来源**（§9.5）：单层路径与递归路径必须逐字一致，
        // 否则同一件事在日志里长成两句话，读的人会以为是两件事。

        /// <summary>详细日志里"这次拿什么参数调的引擎"那一行的前缀。</summary>
        public const string EngineCommandSummaryPrefix = "引擎调用：";

        /// <summary>
        /// **密码候选循环**里"开始试第 i 个候选"的格式（<c>{0}</c> = 任务名，<c>{1}</c> = 第几个，
        /// <c>{2}</c> = 共几个，<c>{3}</c> = 候选来源说明）。
        ///
        /// <para>单层路径与递归路径共用这一句 —— 递归那条路以前**一条候选日志都没有**，
        /// 真机那次 13 分钟走的是递归路径，日志里连"试了几个候选"都看不出来。</para>
        /// </summary>
        public const string PasswordCandidateAttemptLogFormat =
            "{0}：开始解压，密码候选 {1}/{2}，{3}";

        /// <summary>候选不对、继续试下一个（单层与递归共用）。</summary>
        public const string PasswordCandidateRejectedLogFormat =
            "{0}：这个密码候选不对，继续试下一个。";

        /// <summary>损坏归档：不换候选、直接停（单层与递归共用）。</summary>
        public const string CandidateStoppedByCorruptedLogFormat =
            "{0}：这个包已损坏，换密码候选没有帮助 —— 停下（不再重试其余候选）。";

        /// <summary>
        /// **两义那一档**（<see cref="PasswordOrCorrupted"/>）：引擎自己说"密码可能不对、也可能数据坏"，
        /// 所以**不许停下**（与 7-Zip 侧同一口径：那句 CRC 原话也从不当成"已损坏"）。
        /// </summary>
        public const string CandidatePasswordOrCorruptedLogFormat =
            "{0}：这一次没能确认密码 —— 引擎的原话把「密码不对」与「数据坏了」两种可能一起给了出来，"
            + "换密码候选还有意义，继续试下一个。";

        /// <summary>
        /// 两义那一档的**结论文案**（<c>{0}</c> = 引擎原话）：
        /// 两种可能都写着、都不断言，并给出各自动作 —— 与"密码错误"和"文件损坏"两档都不同。
        /// </summary>
        public const string PasswordOrCorruptedMessageFormat =
            "密码可能不对，也可能这个包的数据坏了（引擎那句话把两种可能一起给了出来：{0}）。"
            + "先拿密码本里的候选再试；密码确实对得上还是这样，就说明数据不完整，只能重新下载。";

        /// <summary>其余引擎错误：换密码也解决不了，直接停（单层与递归共用；<c>{1}</c> = 原因）。</summary>
        public const string CandidateStoppedByEngineErrorLogFormat =
            "{0}：解压失败：{1}";

        /// <summary>
        /// 递归层日志里的来源标记（<c>{0}</c> = 层号）。
        ///
        /// <para>为什么要前缀（与既有日志的调用处同口径）：同一个包在日志里"这一条来自第几层"
        /// 是排查多层嵌套时唯一能对号入座的信息（既有实现用的是 <c>$"  ├ 第 {depth} 层：…"</c>，
        /// 这里沿用同一个形状，避免同一件事出现两种行首）。</para>
        /// </summary>
        public const string RecursionLayerLogPrefixFormat = "  ├ 第 {0} 层：";

        /// <summary>详细日志里"这一层开始解压"（递归每层一条；<c>{1}</c> = 层号，<c>{2}</c> = 归档名）。</summary>
        public const string RecursionLayerAttemptLogFormat =
            "{0}：递归第 {1} 层开始解压：{2}（候选顺序见下面每一条）。";
    }
}
