namespace ArchiveFixer.Models
{
    // StatusText 的一部分（partial）：日志瘦身、批末诊断清单、详细日志与引擎原话。
    // 成员名、取值与主文件 StatusText.cs 逐字一致；按域拆文件只是为了单文件可读，不是新类型。
    public static partial class StatusText
    {
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

        /// <summary>
        /// 「下一步」：**定稿搬运失败**那一档。
        ///
        /// <para>为什么要单独一句（用户 2026-10-04 真机）：那一批的「下一步」只写了
        /// <see cref="BatchDiagnosticsActionOther"/>，而它让人去"看引擎原话" —— 定稿搬运失败
        /// **根本没有引擎原话**（它是程序自己搬不动，原因由程序写在①页「错误信息」列里）。
        /// 指路指错方向，用户翻遍日志也找不到那句话。</para>
        ///
        /// <para>⛔ <see cref="BatchDiagnosticsActionOther"/> 一个字都不许删：它对引擎类失败是对的；
        /// 这一句只在"那一组全是定稿搬运失败"时顶替它（判据 = <c>ArchiveTask.CommitMoveFailed</c>）。</para>
        /// </summary>
        public const string BatchDiagnosticsActionCommitMoveFailed =
            "定稿搬运失败的那些（这一类失败没有引擎原话：原因就在①页「错误信息」列）";

        /// <summary>
        /// 「其他失败」那一组的注脚：组里混着引擎类失败与定稿搬运失败时，把后者单独点出来
        /// （混合档下「下一步」照旧用 <see cref="BatchDiagnosticsActionOther"/>，靠这一句补充指路）。
        /// <c>{0}</c> = 其中定稿搬运失败的有几个。
        /// </summary>
        public const string BatchDiagnosticsCommitMoveFailedNoteFormat =
            "其中 {0} 个是定稿搬运失败（这一类失败没有引擎原话，原因在①页「错误信息」列）";

        /// <summary>
        /// 「其余物为什么原封不动地留着」那一句（**唯一出口**：①页「错误信息」列、批末诊断、
        /// 失败清单三处打印的是同一份文字）。
        ///
        /// <para>用户 2026-10-04 真机：日志里写着「链尾的其余物不处理（链上的「老王.apk」没有成功…）」，
        /// 而界面上只看到"解压成功、其余物还在"。<c>{0}</c> = 其余物目录，<c>{1}</c> = 原因。</para>
        /// </summary>
        public const string RestKeptNoteFormat = "其余物没有处理：留在 {0}，原因：{1}；一个字节都没动。";

        /// <summary>同一句，但这一单没记下其余物目录（只写得清原因）。<c>{0}</c> = 原因。</summary>
        public const string RestKeptNoteNoPathFormat = "其余物没有处理：{0}；一个字节都没动。";

        /// <summary>批末诊断里那一行（<c>{0}</c> = 任务名，<c>{1}</c> = <see cref="RestKeptNoteFormat"/>）。</summary>
        public const string BatchDiagnosticsRestKeptLineFormat = "· {0} —— {1}";

        /// <summary>
        /// 批末诊断里"成品目录里还留着内层包、内容还没解出来"那一行
        /// （<c>{0}</c> = 任务名，<c>{1}</c> = 个数，<c>{2}</c> = 点名；用户 2026-11-10：汇总里不许没有）。
        /// </summary>
        public const string BatchDiagnosticsLeftBehindInnerPackagesLineFormat =
            "· {0} —— 成品目录里还留着 {1} 个内层包没解开：{2}（内容还在里面；要解就把它们加进任务列表）";

        /// <summary>「一键处理完成」那一行后面补的一句（<c>{0}</c> = 去重后的个数）。</summary>
        public const string OneClickLeftBehindInnerPackagesSuffixFormat =
            " 另有 {0} 个内层包没解开（内容还在里面，见批末诊断）。";

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
        /// **引擎报「缺卷」时那一行**（<c>{0}</c> = 层标记，<c>{1}</c> = 引擎原话）。
        ///
        /// <para>⚠ 用户 2026-10-09 点名：「为啥显示解压失败」—— 缺卷**不是失败**（「连完整的都没有，
        /// 这就不是程序的错误」），而且「在最后一步之前碰到分卷缺失的就先跳过」⇒ 批中间这一行是
        /// **WARN + 先跳过**，⛔ 不许写"解压失败"、⛔ 不许落缺卷结论（那是最后那一轮的事）。
        /// 判据读结构化错误码 <c>EngineErrorTypes.VolumeMissing</c>，⛔ 不比中文文案。</para>
        /// </summary>
        public const string CandidateMissingVolumeLogFormat =
            "{0}：这一组还没凑齐（{1}）—— 先跳过，等这一批的解压都跑完、最后那一轮再判（⛔ 不是失败）。";

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