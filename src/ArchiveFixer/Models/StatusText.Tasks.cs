namespace ArchiveFixer.Models
{
    // StatusText 的一部分（partial）：任务状态 / 后缀状态 / 密码状态列 / 改名预览状态。
    // 成员名、取值与主文件 StatusText.cs 逐字一致；按域拆文件只是为了单文件可读，不是新类型。
    public static partial class StatusText
    {
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
        /// <summary>
        /// **列表那一格"这一组解压成功"**（用户 2026-10-06 拍板的口径①：一行只讲"你导进来的那个文件"，
        /// 而"这一组由这一行发起解开"写进状态那一格）。
        ///
        /// <para>⛔ **它只是显示文案，不是机器状态**：`ArchiveTask.Status` 一个字不改
        /// （仍是 <see cref="ExtractSuccess"/>）⇒ 所有 `== ExtractSuccess` 的判断、失败名单、
        /// 汇总与配色全都不受影响。⛔ 别把它塞进任何判据里。</para>
        /// </summary>
        public const string ExtractSuccessAsGroup = "这一组解压成功";

        /// <summary>内容组里的过程任务：已并入组，不重复显示成最终成功。</summary>
        public const string ContentGroupProcessTask = "已并入内容组（过程任务）";

        /// <summary>组已有产物，但最终内容没有通过完整校验。</summary>
        public const string ContentGroupPartial = "组内容未全部证实";

        /// <summary>组没有最终内容。</summary>
        public const string ContentGroupMissing = "组没有最终内容";

        public const string VolumeMissing = "分卷缺失";

        /// <summary>第二大步最终内容组缺片；链失败，不是第一大步的交接跳过。</summary>
        public const string FinalGroupIncomplete = "分卷组残缺";

        /// <summary>第二大步最终内容组完整但数据损坏；链失败，不是密码错误。</summary>
        public const string FinalGroupCorrupted = "分卷组损坏";

        /// <summary>分卷结构异常（数量/槽位/大小/混族矛盾）：只显示，不处理。</summary>
        public const string VolumeSizeAnomaly = "分卷结构异常";

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
        public const string SecurityCapHint = "（这是程序的安全上限，不是这个包坏了；可在⑧设置 →「安全上限」里调大）";

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
        /// **真正的 APK 不碰**（用户 2026-10-10 拍板：有些是用户真的要转移到手机上安装的）。
        ///
        /// <para>判据在唯一出口 <see cref="Detection.AndroidPackageDetector"/>（内容证据：ZIP 里同时有
        /// <c>AndroidManifest.xml</c> 与 <c>classes.dex</c>，⛔ 不看后缀）；
        /// 行为 = 不解压 / 不递归 / 不改名，源文件一个字节不动。⛔ 这三句都得写出来 ——
        /// 用户看到"跳过"时要能一眼知道**为什么**、以及**去哪儿处理**。</para>
        /// </summary>
        public const string AndroidPackageSkipped =
            "这是 Android 应用安装包（APK）—— 按设置不解压、不递归、不改名，源文件一个字节都不动；"
            + "要处理它请用专门的 APK 工具";

        /// <summary>
        /// 用户点了「停止后续」之后**本批唯一**那条日志（2026-09-27 真机：他连点两次，
        /// 日志里出现两遍"已请求停止后续任务"、收尾又是另一句"已停止后续任务"，三句不同的话）。
        ///
        /// <para>措辞按他当天的口径写实：停止后续 = 停止所有的东西，
        /// 但**当前任务是在安全点停**（候选密码之间），要立刻掐断得用①页「取消当前」——
        /// 两档的区别必须写在这一句里，否则用户不知道还有更强的那一档。</para>
        /// </summary>
        public const string StopRequestedNotice =
            "已按「暂停所有操作」停下：当前正在跑的任务立即结束，队列不再启动任何任务；"
            + "要恢复请重新按「一键处理」。「停止当前任务」只停当前，随后会继续队列。";

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

        /// <summary>密码这一列对“跟班卷 / 缺卷 / 根本没有密码语义的跳过行”的显示值。</summary>
        public const string PasswordNotApplicable = "不适用";

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
        /// 分卷文件**五种改名操作一律不动**（原话口径见 <c>LegacyVolumeNames.IsVolumePart</c>）。
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

    }
}