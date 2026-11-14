namespace ArchiveFixer.Models
{
    // StatusText 的一部分（partial）：密码本写回、本机加密记忆、④密码页、换引擎再试、候选上限点名。
    // 成员名、取值与主文件 StatusText.cs 逐字一致；按域拆文件只是为了单文件可读，不是新类型。
    public static partial class StatusText
    {
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
        // 跟 Windows 账号无关（同机换谁登录都能用、不弹框），文件放 %LOCALAPPDATA%\ArchiveFixer\。
        // **代价必须如实写在界面上**：同机任何本机用户都可能解开；换机器 / 重装系统解不开。

        /// <summary>
        /// 密码列表窗口顶部那句（**必须与事实一致**）。
        ///
        /// <para>改之前写的是"密码只存在内存里" —— 那是 2026-09-24 之前的实话，现在已经不准确了；
        /// 界面上留着一句过期的话，比没有那句话更糟（用户会照着它做判断）。</para>
        ///
        /// <para>⚠ 2026-10-04（真机）又改了一次：原来写"列表自上而下就是尝试顺序"，而**事实不是**——
        /// 空密码优先（<c>TryEmptyPasswordFirst</c> 默认开），列表内部按**历史成功次数从多到少**排
        /// （<c>PasswordService.OrderBySuccessCount</c>），这张表的顺序只在次数相同时才生效。
        /// 用户按表头那句话去推理"为什么试了这么多条"，推出来的结论必然是错的。</para>
        ///
        /// <para>⚠ 2026-10-04 第二次口述（当天）：用户明确否掉了"跨任务按成功次数排序"——
        /// 「谁规定你可以在**不同次任务**中降序排列的，**同一次任务中是可以这样，
        /// 不同次文件一律严格按我列表顺序**」⇒ 表里就是严格顺序，"按成功次数排"这一档**已被删掉**。</para>
        /// </summary>
        public const string PasswordListPrivacyHint =
            "密码值和顺序存在程序自带 data\\passwords.txt（明文）；启用状态和备注存在 passwords.meta.json。"
            + "关掉「记住密码列表」则只在本次运行内有效；日志不记明文。导入时保留密码中的空格。"
            + "⚠ 实际尝试顺序：先试一次空密码，再试密码本里命中名称的，最后严格按这张表自上而下的顺序逐条试；"
            + "同一批里前面刚解开的密码会先试一次（只在本次运行内，下一批回到这张表的顺序）。";

        /// <summary>
        /// ④页那张表的列头（**必须与真实顺序一致**，用户 2026-10-04 真机 + 当天第二次口述）。
        /// </summary>
        public const string PasswordListOrderHeader = "密码（严格按本表顺序尝试）";

        /// <summary>
        /// 「上移 / 下移」按钮的提示 —— 这张表的顺序**就是尝试顺序**（用户 2026-10-04 第二次口述后
        /// 不再有"按成功次数排序"那一档）。
        /// </summary>
        public const string PasswordListMoveHint = "越靠前的越先被尝试（这张表的顺序就是尝试顺序）";

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

        // ================================================================
        // 换引擎再试一遍（用户 2026-10-05 拍板"补"）
        // ================================================================
        //
        // 现场（AGENTS.md §11.5 最后一条）：同一个包、同一条密码 —— WinRAR 6.11 退出码 0、两卷原样解出；
        // 内置 7-Zip 26.03 退出码 2「Wrong password」、一个字节都没出来。而候选循环只用 7-Zip，
        // WrongPassword 又落在"不换引擎"那一档 ⇒ 程序把"这个引擎吃不下这个 ZIP"如实报成了
        // 「达到密码尝试上限」——用户自己用 WinRAR 十秒就能打开。
        //
        // ⛔ 这几句必须把两件事说清：① 是**哪个引擎**报的密码错；② **换没换过引擎再试**
        // （跑过就写"已用 WinRAR 再试过一遍"，没跑成就写"没找到 WinRAR，所以没换引擎再试"）。
        // ⛔ 措辞里不许有"密码不对"这种断言：真正的成因可能是引擎吃不下这一包。

        /// <summary>
        /// 兜底开始那一行（<c>{0}</c> = 任务名 / 层标签，<c>{1}</c> = 报密码错的那个引擎，
        /// <c>{2}</c> = 它报的结构化错误类型，<c>{3}</c> = 候选个数，<c>{4}</c> = WinRAR 版本）。
        /// </summary>
        public const string PasswordEngineFallbackStartLogFormat =
            "{0}：{1} 报的是「{2}」—— 这一步只说明这个引擎吃不下这一份，不等于密码不对；"
            + "已换本机装的 WinRAR（{4}）把同一批 {3} 个候选再试一遍。";

        /// <summary>兜底逐个候选那一行（<c>{0}</c> = 标签，<c>{1}</c> = 第几个，<c>{2}</c> = 共几个，<c>{3}</c> = 来源说明）。</summary>
        public const string PasswordEngineFallbackCandidateLogFormat =
            "{0}：WinRAR 兜底第 {1}/{2} 个候选，{3}";

        /// <summary>这个候选在 WinRAR 上也没解出东西（<c>{3}</c> = 退出码）⇒ 试下一个。</summary>
        public const string PasswordEngineFallbackCandidateRejectedLogFormat =
            "{0}：WinRAR 兜底第 {1}/{2} 个候选也没解出东西（退出码 {3}），试下一个。";

        /// <summary>
        /// 兜底命中（<c>{1}</c>/<c>{2}</c> = 第几个 / 共几个，<c>{3}</c> = 来源说明，<c>{4}</c> = WinRAR 版本）。
        ///
        /// <para>⚠ 句尾必须**以来源说明结束**：脱敏器只认「描述：******」收尾的形状，
        /// 在说明后面再挂"用 WinRAR 6.11 解开了"会把那半句一起擦掉（引擎名就看不见了）。</para>
        /// </summary>
        public const string PasswordEngineFallbackHitLogFormat =
            "{0}：WinRAR 兜底成功 —— 用 WinRAR {4} 把这一份解开了，按「这个密码是对的」继续走后面的正常管线；"
            + "命中的是第 {1}/{2} 个候选：{3}";

        /// <summary>兜底跑完仍没解开（<c>{1}</c> = 主引擎，<c>{2}</c> = 试了几个候选，<c>{3}</c> = 最后一次退出码）。</summary>
        public const string PasswordEngineFallbackFailedLogFormat =
            "{0}：{1} 报的是密码错；已换 WinRAR 把同一批 {2} 个候选再试过一遍，仍然没解开（最后一次退出码 {3}）—— 结论照旧。";

        /// <summary>
        /// 退出码不属于"密码不对"那一类时的如实记录（<c>{1}</c> = 第几个，<c>{2}</c> = 共几个，<c>{3}</c> = 退出码）。
        ///
        /// <para>⛔ 记下来但**照样把剩下的候选试完**：实测退出码语义与文档并不一致
        /// （本机 WinRAR 6.11：密码不对是 <c>10</c> 而不是文档的 11、空密码与"打不开"都是 <c>1</c>），
        /// 而漏试那个**对的**候选会让整条兜底白做 —— 多试一个最坏只多花几百毫秒。</para>
        /// </summary>
        public const string PasswordEngineFallbackUnexpectedExitCodeLogFormat =
            "{0}：WinRAR 兜底第 {1}/{2} 个候选的退出码是 {3}（不是「密码错」那一类），照旧把剩下的候选试完"
            + "（实测退出码语义与文档不完全一致，漏试对的候选会让整条兜底白做）。";

        /// <summary>
        /// 取消 / 超时处收手（<c>{1}</c> = 第几个，<c>{2}</c> = 共几个）—— 这一档必须停，⛔ 不许再起进程。
        /// </summary>
        public const string PasswordEngineFallbackCancelledLogFormat =
            "{0}：WinRAR 兜底在第 {1}/{2} 个候选处收手（取消或超时），剩下的候选不再试 —— 结论照旧。";

        /// <summary>
        /// **退出码 0，但产物目录里一个字节都没多出来** ⇒ ⛔ 不算命中，试下一个
        /// （<c>{1}</c> = 第几个，<c>{2}</c> = 共几个）。
        ///
        /// <para>为什么必须有这条判据（实测，2026-10-05）：<c>-mhe</c>（连文件名一起加密）的 7z
        /// 用**错密码**时，WinRAR 照样回**退出码 0**、却一个文件都没解出来 ——
        /// 只看退出码就会把它当成"这个密码是对的"，一路走到假的成功（不变量 6 的红线）。
        /// 用户 2026-10-05 的原话也是"⛔ 不许只看退出码"。</para>
        /// </summary>
        public const string PasswordEngineFallbackNoProductsLogFormat =
            "{0}：WinRAR 兜底第 {1}/{2} 个候选回了退出码 0，但产物目录里一个字节都没多出来 —— "
            + "不算命中（实测加密头的包用错密码时它也会回 0），试下一个。";

        /// <summary>
        /// 本机没找到 WinRAR ⇒ 不换引擎再试（<c>{0}</c> = 标签，<c>{1}</c> = 主引擎）。
        /// ⛔ 不许静默跳过：结论与日志都要如实说清"没换引擎再试"。
        /// </summary>
        public const string PasswordEngineFallbackNoEngineLogFormat =
            "{0}：{1} 报的是密码错；本机没找到 WinRAR（程序只检测与调用你自己装的那一份，不随包分发），"
            + "所以没换引擎再试 —— 结论照旧。装好 WinRAR 后重跑一遍即可换引擎再试。";

        /// <summary>
        /// 判据成立、但**主引擎已经写出过字节** ⇒ 不换引擎再试（<c>{0}</c> = 标签，<c>{1}</c> = 主引擎）。
        ///
        /// <para><b>为什么必须挡这一下</b>（2026-10-05 实测逮到的假成功）：WinRAR 用**错密码**解一个普通
        /// <c>-p</c> 加密的 7z 时，照样回**退出码 0**、而且会**按原大小把解密出来的乱码写进产物目录**
        /// （实测 23 字节，文件名 / 大小 / 时间戳与真产物逐字一样）—— 于是"退出码 0 且产物目录里多出了东西"
        /// 这条判据会把错密码认成命中，整条链把乱码当产物发布、结论从「密码错误」翻成「解压成功」。
        /// 而一旦主引擎也写出过字节（说明它进得去这一份的数据流），两个引擎给出的产物在现有判据下
        /// 分辨不出"真解开了"与"写的是乱码" ⇒ 按「判不出 ⇒ 什么都不做」不换引擎、不改结论。</para>
        ///
        /// <para>⛔ 不许静默跳过：这一行就是"为什么没换引擎再试"的如实说明（与"没装 WinRAR"那一档
        /// 必须分开说 —— 两句对应的事实与用户要做的动作完全不同）。
        /// ⛔ 文案里不许写 Markdown（强调一律用「」，见 AGENTS.md §9.3）。</para>
        /// </summary>
        public const string PasswordEngineFallbackIndistinguishableLogFormat =
            "{0}：{1} 报的是密码错，但它已经在这一层的产物目录里写出过字节（说明它进得去这一份的数据流）；"
            + "这一档换引擎给不出可分辨的证据（WinRAR 用错密码时也会把解密出来的乱码按原大小写出来），"
            + "所以不换引擎再试 —— 结论照旧。";

        /// <summary>
        /// **安全网**：交给 WinRAR 的那一份归档在这一次调用之后不见了（源包必须原地不动，不变量 1）。
        /// <c>{0}</c> = 标签。
        ///
        /// <para>主防线是参数里的 <c>-cfg-</c>（实测：本机 WinRAR 设了「解压后删除压缩包 = 总是」时，
        /// 不加它 WinRAR 真会把源包删掉）。这一行是"那条防线没生效"的如实报告 ——
        /// 已经删掉的找不回来，但绝不许静默。</para>
        /// </summary>
        public const string PasswordEngineFallbackSourceVanishedLogFormat =
            "{0}：⚠ 危险 —— 交给 WinRAR 的那一份归档在这一次调用之后不见了（源包必须原地不动）。"
            + "程序已经用 -cfg- 让 WinRAR 忽略本机配置（本机若设了「解压后删除压缩包」正是靠它挡住的），"
            + "出现这一行说明这条防线没生效，请立刻检查磁盘与 WinRAR 的设置。";

        /// <summary>
        /// **兜底跑过**时追加进结论的那一句（<c>{0}</c> = 报密码错的那个引擎）。
        /// ⛔ 两件事都不许省：是哪个引擎报的、**已经换引擎再试过一遍**。
        /// </summary>
        public const string PasswordEngineFallbackRetriedSuffixFormat =
            "（{0} 报的是密码错，已换 WinRAR 把同一批候选再试过一遍，仍然没解开 —— "
            + "所以既有可能是密码不对，也可能是两个引擎都吃不下这一份。）";

        /// <summary>
        /// **兜底没跑成**时追加进结论的那一句（<c>{0}</c> = 报密码错的那个引擎）：
        /// 本机没有 WinRAR ⇒ 没换引擎再试。⛔ 不许把这句省掉（那会让用户以为已经换过引擎了）。
        /// </summary>
        public const string PasswordEngineFallbackNotRetriedSuffixFormat =
            "（{0} 报的是密码错；本机没找到 WinRAR，所以没换引擎再试。）";

        // ================================================================
        // 候选被每层上限截断时要**点名**"哪几条没试到"（用户 2026-10-05 真机第七批）
        // ================================================================
        //
        // 现场：候选共 12 个（空密码 + 密码本 11 条）、每层上限 10 ⇒ 第 11 条从没被试过，
        // 而日志只说「达到密码尝试上限」——用户看不出"是哪一条没试"，也就无从决定要不要调上限 / 调顺序。
        // 点名用的是既有描述器（形如「尝试密码列表第 11 项」），⛔ 绝不出明文。

        /// <summary>
        /// 收尾那行（<c>{0}</c> = 任务名 / 层标签，<c>{1}</c> = 有几条没试到，<c>{2}</c> = 每层上限，
        /// <c>{3}</c> = 点名那几个候选）。
        ///
        /// <para>⚠ 句尾必须**以点名结束**（后面不再挂别的字，也⛔ 不许在点名后面折一行"还有 K 条"）：
        /// 脱敏器只认「描述：******」收尾这一种形状，尾巴上再挂任何东西都会把整行擦成
        /// 「尝试密码 ******」——那正是这条文案要治的那个毛病。所以"一共几条"写在**前半句**里，
        /// 后面只列前几条。</para>
        /// </summary>
        public const string PasswordCandidateUntriedLogFormat =
            "{0}：候选里还有 {1} 条因为每层上限 {2} 没试到，只点名前几条：{3}";

        /// <summary>
        /// 同一件事的结论形态（进失败原因；<c>{0}</c> = 有几条没试到，<c>{1}</c> = 点名）。
        /// ⚠ 同上：句尾以点名结束，⛔ 不许再挂括号 / 折行之类的尾巴。
        /// </summary>
        public const string PasswordCandidateUntriedSuffixFormat =
            "；候选里还有 {0} 条因为每层上限没试到，只点名前几条：{1}";

    }
}
